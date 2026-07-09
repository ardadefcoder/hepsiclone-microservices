using System.Net.Http.Headers;
using BasketService.Api.Data;
using BasketService.Api.Dtos;
using BasketService.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BasketService.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BasketController : ControllerBase
    {
        private readonly BasketDbContext _db;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<BasketController> _logger;

        public BasketController(BasketDbContext db,IHttpClientFactory httpClientFactory,ILogger<BasketController> logger)
        {
            _db = db;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        
        [HttpGet("{userId}")]
        public async Task<IActionResult> GetBasket(string userId)
        {
            var basket = await _db.Baskets
                .Include(b => b.Items)                    
                .FirstOrDefaultAsync(b => b.UserId == userId);

            if (basket == null)
                return Ok(new Basket { UserId = userId }); 

            return Ok(basket);
        }

   
        [HttpPost("items")]
        public async Task<IActionResult> AddItem(AddItemRequest request)
        {
            
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
                    return NotFound("Ürün bulunamadı");           // 404: ürün yok (servis çalışıyor)
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
                return StatusCode(503, "Ürün servisi şu an yanıt vermiyor");   // gerçek bağlantı hatası
            }

            if (product == null)
                return NotFound("Ürün bulunamadı");

            if (product.Stock < request.Quantity)
            {
                _logger.LogWarning("Yetersiz stok: {ProductId} istenen {Q}, mevcut {S}",
                    request.ProductId, request.Quantity, product.Stock);
                return BadRequest($"Yeterli stok yok. Mevcut: {product.Stock}");
            }

            
            var basket = await _db.Baskets
                .Include(b => b.Items)
                .FirstOrDefaultAsync(b => b.UserId == request.UserId);

            if (basket == null)
            {
                basket = new Basket { UserId = request.UserId };
                _db.Baskets.Add(basket);
            }

            var existing = basket.Items.FirstOrDefault(i => i.ProductId == request.ProductId);
            if (existing != null)
            {
                existing.Quantity += request.Quantity;
            }
            else
            {
                basket.Items.Add(new BasketItem
                {
                    ProductId = product.Id,
                    ProductName = product.Name,       
                    Price = product.Price,
                    Quantity = request.Quantity
                });
            }

            await _db.SaveChangesAsync();

            _logger.LogInformation("Sepete eklendi: {Product} x{Q} (kullanıcı: {User})",
                product.Name, request.Quantity, request.UserId);

            return Ok(basket);
        }


        [HttpDelete("{userId}/clear")]
        public async Task<IActionResult> ClearBasket(string userId)
        {
            var basket = await _db.Baskets
                .Include(b => b.Items)
                .FirstOrDefaultAsync(b => b.UserId == userId);

            if (basket == null) return NotFound("Sepet bulunamadı");

            _db.BasketItems.RemoveRange(basket.Items);   // tüm satırları sil
            await _db.SaveChangesAsync();

            _logger.LogInformation("Sepet temizlendi: {UserId}", userId);
            return Ok();
        }

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

            _logger.LogInformation("Sepetten çıkarıldı: {ProductId} (kullanıcı: {User})", productId, userId);
            return Ok(basket);
        }
    }
            
    
    public class ProductDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public int Stock { get; set; }
    }
}