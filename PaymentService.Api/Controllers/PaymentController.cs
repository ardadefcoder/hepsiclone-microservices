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
            // Biz %90 başarı ile taklit ediyoruz.
            bool paymentSuccess = new Random().Next(1, 11) <= 9;   // 1-9 = başarılı, 10 = red

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
    }
}