using BasketService.Api.Data;
using BasketService.Api.Dtos;
using BasketService.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BasketService.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class BasketController : ControllerBase
    {
        private readonly BasketDbContext _db;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<BasketController> _logger;

        public BasketController(
            BasketDbContext db,
            IHttpClientFactory httpClientFactory,
            ILogger<BasketController> logger)
        {
            _db = db;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        // ---- Kullanıcının sepetini getir ----
        [HttpGet("{userId}")]
        public async Task<IActionResult> GetBasket(string userId)
        {
            var basket = await _db.Baskets
                .Include(b => b.Items)
                .FirstOrDefaultAsync(b => b.UserId == userId);

            if (basket == null)
                return Ok(new Basket { UserId = userId });   // sepet yoksa boş sepet dön

            return Ok(basket);
        }

        // ---- Sepete ürün ekle ----
        [HttpPost("items")]
        public async Task<IActionResult> AddItem(AddItemRequest request)
        {
            // 1) ProductService'e sor: ürün var mı, stok/fiyat ne?
            var client = _httpClientFactory.CreateClient("ProductService");

            var correlationId = HttpContext.Response.Headers["X-Correlation-Id"].ToString();
            if (!string.IsNullOrEmpty(correlationId))
                client.DefaultRequestHeaders.Add("X-Correlation-Id", correlationId);

            ProductDto? product;
            try
            {
                var response = await client.GetAsync($"/api/Products/{request.ProductId}");

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Sepete eklenmek istenen ürün bulunamadı: {ProductId}", request.ProductId);
                    return NotFound("Ürün bulunamadı");
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("ProductService beklenmedik yanıt: {Status}", response.StatusCode);
                    return StatusCode(503, "Ürün servisi beklenmedik bir yanıt verdi");
                }

                product = await response.Content.ReadFromJsonAsync<ProductDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProductService'e ulaşılamadı: {ProductId}", request.ProductId);
                return StatusCode(503, "Ürün servisi şu an yanıt vermiyor");
            }

            if (product == null)
                return NotFound("Ürün bulunamadı");

            // 2) Sepeti bul ya da oluştur
            var basket = await _db.Baskets
                .Include(b => b.Items)
                .FirstOrDefaultAsync(b => b.UserId == request.UserId);

            if (basket == null)
            {
                basket = new Basket { UserId = request.UserId };
                _db.Baskets.Add(basket);
            }

            // 3) BİRİKİMLİ stok kontrolü: sepetteki + yeni istenen
            var existing = basket.Items.FirstOrDefault(i => i.ProductId == request.ProductId);
            int sepettekiAdet = existing?.Quantity ?? 0;
            int toplamIstenen = sepettekiAdet + request.Quantity;

            if (product.Stock < toplamIstenen)
            {
                _logger.LogWarning("Yetersiz stok: {ProductId} sepette {Sepetteki}, istenen {Istenen}, stok {Stok}",
                    request.ProductId, sepettekiAdet, request.Quantity, product.Stock);
                return BadRequest($"Yeterli stok yok. Stok: {product.Stock}, sepetinizde zaten: {sepettekiAdet}");
            }

            // 4) Ekle ya da adedi arttır (buraya geldiysek stok yeterli)
            if (existing != null)
            {
                existing.Quantity += request.Quantity;
            }
            else
            {
                basket.Items.Add(new BasketItem
                {
                    ProductId = product.Id,
                    ProductName = product.Name,   // snapshot: o anki isim
                    Price = product.Price,        // snapshot: o anki fiyat
                    Quantity = request.Quantity
                });
            }

            await _db.SaveChangesAsync();

            _logger.LogInformation("Sepete eklendi: {ProductName} x{Quantity} kullanıcı {UserId}",
                product.Name, request.Quantity, request.UserId);

            return Ok(basket);
        }

        // ---- Sepeti tamamen temizle (checkout sonrası) ----
        [HttpDelete("{userId}/clear")]
        public async Task<IActionResult> ClearBasket(string userId)
        {
            var basket = await _db.Baskets
                .Include(b => b.Items)
                .FirstOrDefaultAsync(b => b.UserId == userId);

            if (basket == null) return NotFound("Sepet bulunamadı");

            _db.BasketItems.RemoveRange(basket.Items);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Sepet temizlendi: {UserId}", userId);
            return Ok();
        }

        // ---- Sepetten tek ürün çıkar ----
        [HttpDelete("{userId}/items/{productId}")]
        public async Task<IActionResult> RemoveItem(string userId, int productId)
        {
            var basket = await _db.Baskets
                .Include(b => b.Items)
                .FirstOrDefaultAsync(b => b.UserId == userId);

            if (basket == null) return NotFound("Sepet bulunamadı");

            var item = basket.Items.FirstOrDefault(i => i.ProductId == productId);
            if (item == null) return NotFound("Ürün sepette yok");

            basket.Items.Remove(item);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Sepetten çıkarıldı: {ProductId} kullanıcı {UserId}", productId, userId);
            return Ok(basket);
        }
    }

    // ProductService'ten dönen ürünü karşılamak için minimal DTO
    public class ProductDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public int Stock { get; set; }
    }
}