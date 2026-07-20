using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentService.Api.Data;
using PaymentService.Api.Dtos;
using PaymentService.Api.Models;

namespace PaymentService.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
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

            // Correlation ID'yi taşı (3 servisin logları bağlansın)
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

            // ---- 4) Ödemeyi işle (SİMÜLASYON) ----
            // Gerçekte burada Stripe/iyzico gibi bir ödeme sağlayıcısı olurdu.

            bool paymentSuccess = false;

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
            _logger.LogInformation("Order completed: {OrderId} {Total} {EventType}",
                order.Id, total, "OrderCompleted");
            // ---- 5) Siparişi kaydet (başarılı da başarısız da kayda geçer) ----
            _db.Orders.Add(order);
            await _db.SaveChangesAsync();

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

            // ---- 6) Stokları düş ----
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

            // ---- 7) Sepeti temizle ----
            try { await client.DeleteAsync($"/api/Basket/{request.UserId}/clear"); }
            catch (Exception ex) { _logger.LogWarning(ex, "Sepet temizlenemedi ama sipariş tamam"); }

            // ---- 6) Ödeme başarılı → sepeti temizle ----
            try
            {
                await client.DeleteAsync($"/api/Basket/{request.UserId}/clear");
            }
            catch (Exception ex)
            {
                // Sepet temizlenemese bile sipariş geçerli — sadece logla
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

        [HttpPost("refund")]
        public async Task<IActionResult> Refund(RefundRequest request)
        {






        }

    }
}