using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentService.Api.Data;
using PaymentService.Api.Dtos;
using PaymentService.Api.Models;

namespace PaymentService.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class PaymentController : ControllerBase
    {
        private readonly PaymentDbContext _db;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<PaymentController> _logger;

        public PaymentController(
            PaymentDbContext db,
            IHttpClientFactory httpClientFactory,
            ILogger<PaymentController> logger)
        {
            _db = db;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        [HttpPost("checkout")]
        public async Task<IActionResult> Checkout(CheckoutRequest request)
        {
            var client = _httpClientFactory.CreateClient("BasketService");

            // Correlation ID'yi taşı (servislerin logları bağlansın)
            var correlationId = HttpContext.Response.Headers["X-Correlation-Id"].ToString();
            if (!string.IsNullOrEmpty(correlationId))
                client.DefaultRequestHeaders.Add("X-Correlation-Id", correlationId);

            // ---- 1) BasketService'ten sepeti çek ----
            BasketDto? basket;
            try
            {
                basket = await client.GetFromJsonAsync<BasketDto>(
                    $"/api/Basket/{request.UserId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BasketService'e ulaşılamadı: {UserId}", request.UserId);
                return StatusCode(503, "Sepet servisi şu an yanıt vermiyor");
            }

            // ---- 2) Sepet boş mu? ----
            if (basket == null || basket.Items.Count == 0)
            {
                _logger.LogWarning("Boş sepetle ödeme denemesi: {UserId}", request.UserId);
                return BadRequest("Sepetiniz boş");
            }

            // ---- 3) Toplam tutarı hesapla ----
            decimal total = basket.Items.Sum(i => i.Price * i.Quantity);

            // ---- 4) Ödemeyi işle (SİMÜLASYON: %90 başarılı) ----
            // Gerçekte burada Stripe/iyzico gibi bir ödeme sağlayıcısı olurdu.
            bool paymentSuccess = new Random().Next(1, 11) <= 9;

            var order = new Order
            {
                UserId = request.UserId,
                TotalAmount = total,
                Status = paymentSuccess ? "Paid" : "Failed",
                Items = basket.Items.Select(i => new OrderItem
                {
                    ProductId = i.ProductId,
                    ProductName = i.ProductName,
                    Price = i.Price,
                    Quantity = i.Quantity
                }).ToList()
            };

            // ---- 5) Siparişi kaydet (başarılı da başarısız da kayda geçer) ----
            _db.Orders.Add(order);
            await _db.SaveChangesAsync();   // order.Id burada doğar

            _logger.LogInformation("Sipariş oluşturuldu: {OrderId} {Total} {Status}",
                order.Id, total, order.Status);

            if (!paymentSuccess)
            {
                _logger.LogWarning("Ödeme reddedildi: {UserId}, tutar {Total}", request.UserId, total);
                return BadRequest(new
                {
                    message = "Ödeme reddedildi, lütfen tekrar deneyin",
                    orderId = order.Id,
                    status = "Failed"
                });
            }

            // ---- 6) Stokları düş (ProductService'e söyle) ----
            var productClient = _httpClientFactory.CreateClient("ProductService");
            if (!string.IsNullOrEmpty(correlationId))
                productClient.DefaultRequestHeaders.Add("X-Correlation-Id", correlationId);

            var stockRequest = new
            {
                items = basket.Items.Select(i => new { productId = i.ProductId, quantity = i.Quantity })
            };

            var stockResponse = await productClient.PostAsJsonAsync("/api/Products/reduce-stock", stockRequest);

            if (!stockResponse.IsSuccessStatusCode)
            {
                // Stok düşürülemedi → siparişi iptal et
                order.Status = "Failed";
                await _db.SaveChangesAsync();

                var reason = await stockResponse.Content.ReadAsStringAsync();
                _logger.LogError("Stok düşürülemedi, sipariş iptal: {OrderId} - {Reason}", order.Id, reason);
                return BadRequest(new { message = "Stok yetersiz: " + reason, orderId = order.Id, status = "Failed" });
            }

            // ---- 7) Sepeti temizle (başarısız olsa da sipariş geçerli) ----
            try
            {
                await client.DeleteAsync($"/api/Basket/{request.UserId}/clear");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sepet temizlenemedi ama sipariş tamam: {OrderId}", order.Id);
            }

            _logger.LogInformation("Ödeme başarılı: {UserId}, sipariş {OrderId}, tutar {Total}",
                request.UserId, order.Id, total);

            return Ok(new
            {
                message = "Ödeme başarılı, siparişiniz alındı",
                orderId = order.Id,
                total,
                status = "Paid"
            });
        }

        // ---- Tüm siparişler (dashboard için) ----
        [HttpGet("orders")]
        public async Task<IActionResult> GetAllOrders()
        {
            var orders = await _db.Orders
                .Include(o => o.Items)
                .OrderByDescending(o => o.CreatedAt)
                .Take(100)
                .ToListAsync();

            return Ok(orders);
        }

        // ---- Kullanıcının sipariş geçmişi ----
        [HttpGet("orders/{userId}")]
        public async Task<IActionResult> GetOrders(string userId)
        {
            var orders = await _db.Orders
                .Include(o => o.Items)
                .Where(o => o.UserId == userId)
                .OrderByDescending(o => o.CreatedAt)
                .ToListAsync();

            return Ok(orders);
        }

        // ---- 1) KULLANICI: kendi siparişi için iade TALEBİ açar ----
        [HttpPost("refund/request")]
        public async Task<IActionResult> RequestRefund(RefundRequest request)
        {
            if (request.OrderId <= 0)
                return BadRequest("Geçerli bir sipariş numarası göndermelisin.");

            var order = await _db.Orders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.Id == request.OrderId);

            if (order == null)
                return NotFound("Sipariş bulunamadı.");

            // GÜVENLİK: sadece kendi siparişini iade edebilir
            if (order.UserId != request.UserId)
            {
                _logger.LogWarning("Yetkisiz iade denemesi: {UserId} sipariş {OrderId} (sahibi: {Sahip})",
                    request.UserId, order.Id, order.UserId);
                return Forbid();   // 403 — başkasının siparişi
            }

            if (order.Status != "Paid")
            {
                return BadRequest(new
                {
                    message = "Sadece ödenmiş siparişler için iade talep edilebilir.",
                    orderId = order.Id,
                    status = order.Status
                });
            }

            order.Status = "RefundRequested";
            await _db.SaveChangesAsync();

            _logger.LogInformation("İade talebi açıldı: {OrderId} kullanıcı {UserId}", order.Id, request.UserId);
            return Ok(new { message = "İade talebin alındı, onay bekleniyor", orderId = order.Id, status = "RefundRequested" });
        }

        // ---- 2) ADMIN: bekleyen iade taleplerini listeler ----
        [HttpGet("refund/pending")]
        public async Task<IActionResult> GetPendingRefunds()
        {
            var pending = await _db.Orders
                .Include(o => o.Items)
                .Where(o => o.Status == "RefundRequested")
                .OrderBy(o => o.CreatedAt)
                .ToListAsync();

            return Ok(pending);
        }

        // ---- 3) ADMIN: talebi ONAYLAR ya da REDDEDER ----
        [HttpPost("refund/decision")]
        public async Task<IActionResult> DecideRefund(RefundDecisionRequest request)
        {
            var order = await _db.Orders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.Id == request.OrderId);

            if (order == null)
                return NotFound("Sipariş bulunamadı.");

            if (order.Status != "RefundRequested")
                return BadRequest("Bu sipariş için bekleyen bir iade talebi yok.");

            // ---- REDDET ----
            if (!request.Approve)
            {
                order.Status = "Paid";   // eski haline döner
                await _db.SaveChangesAsync();
                _logger.LogInformation("İade reddedildi: {OrderId}", order.Id);
                return Ok(new { message = "İade talebi reddedildi", orderId = order.Id, status = "Paid" });
            }

            // ---- ONAYLA → stoğu geri ekle (compensating transaction) ----
            var productClient = _httpClientFactory.CreateClient("ProductService");

            var correlationId = HttpContext.Response.Headers["X-Correlation-Id"].ToString();
            if (!string.IsNullOrEmpty(correlationId))
                productClient.DefaultRequestHeaders.Add("X-Correlation-Id", correlationId);

            var restoreRequest = new
            {
                items = order.Items.Select(i => new { productId = i.ProductId, quantity = i.Quantity })
            };

            var response = await productClient.PostAsJsonAsync("/api/Products/restore-stock", restoreRequest);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("İade onayında stok geri eklenemedi: {OrderId}", order.Id);
                return StatusCode(503, "İade işlenemedi, stok servisi yanıt vermiyor");
            }

            order.Status = "Refunded";
            await _db.SaveChangesAsync();

            _logger.LogInformation("İade onaylandı ve tamamlandı: {OrderId}, tutar {Total}", order.Id, order.TotalAmount);
            return Ok(new { message = "İade onaylandı, stok geri eklendi", orderId = order.Id, status = "Refunded" });
        }
    }
}