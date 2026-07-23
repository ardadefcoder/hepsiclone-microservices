using Elastic.Clients.Elasticsearch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProductService.Api.Data;  
using ProductService.Api.Dtos;
using ProductService.Api.Models;
using Microsoft.AspNetCore.Authorization;

namespace ProductService.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]

    public class ProductsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ILogger<ProductsController> _logger;
        private readonly ElasticsearchClient _es;

        public ProductsController(AppDbContext db, ILogger<ProductsController> logger, ElasticsearchClient es)
        {
            _db = db;
            _logger = logger;
            _es = es;

        }

        [HttpGet]
        public async Task<ActionResult> GetAll()
        { 
            var products = await _db.Products.ToListAsync();
            return Ok(products);
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult> GetById(int id)
        {
            var product = await _db.Products.FindAsync(id);
            if (product == null)
            {
                _logger.LogWarning("Ürün bulunamadı: {ProductId}", id);
                return NotFound();
            }
            return Ok(product);
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> Create(Product product)
        {
            _db.Products.Add(product);
            await _db.SaveChangesAsync();                    // 1) Önce Postgres — Id burada doğar

            var esResponse = await _es.IndexAsync(product, "products", i => i.Id(product.Id));

            if (esResponse.IsValidResponse)
            {
                _logger.LogInformation("ES'e indekslendi: {ProductId}", product.Id);
            }
            else
            {
                _logger.LogWarning("ES İNDEKSLEME BAŞARISIZ: {Debug}", esResponse.DebugInformation);
            }

            _logger.LogInformation("Yeni ürün oluşturuldu: {ProductId}", product.Id);
            return Ok(product);                              // 3) Cevabı dönmeyi unutma! 👈
        }

        [HttpGet("search")]                                    // 👈 bu satır ŞART
        public async Task<IActionResult> Search([FromQuery] string q)
        {
            if (string.IsNullOrWhiteSpace(q))
                return BadRequest("Arama terimi boş olamaz");

            var results = await _db.Products
                .Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                         || EF.Functions.ILike(p.Description, $"%{q}%"))
                .ToListAsync();

            _logger.LogInformation("Arama yapıldı: {Query} - {Count} sonuç", q, results.Count);
            return Ok(results);
        }


        [HttpGet("search-es")]
        public async Task<ActionResult> SearchInElasticsearch([FromQuery] string q)
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return BadRequest("Arama sorgusu boş olamaz.");
            }

            var response = await _es.SearchAsync<Product>(s => s
                .Index("products")
                .Query(query => query
                    .MultiMatch(m => m
                        .Fields(new[] { "name", "description" })
                        .Query(q)
                        .Fuzziness(new Fuzziness("AUTO"))
                    )
                )
            );

            if (!response.IsValidResponse)
            {
                _logger.LogWarning("ES araması başarısız: {Query} - {Debug}", q, response.DebugInformation);
                return Ok(Array.Empty<Product>());
            }

            var docs = response.Documents ?? Enumerable.Empty<Product>();
            _logger.LogInformation("Elasticsearch'te arama yapıldı: {Query} - {Count}", q, docs.Count());
            return Ok(docs);
        }

        [HttpPost("reduce-stock")]
        public async Task<IActionResult> ReduceStock(StockReductionRequest request)
        {
            // 1) ÖNCE hepsini kontrol et — kısmi düşme olmasın
            foreach (var item in request.Items)
            {
                var p = await _db.Products.FindAsync(item.ProductId);
                if (p == null)
                    return NotFound($"Ürün bulunamadı: {item.ProductId}");
                if (p.Stock < item.Quantity)
                    return BadRequest($"Yetersiz stok: {p.Name} (mevcut: {p.Stock}, istenen: {item.Quantity})");
            }

            // 2) Hepsi uygunsa düş
            foreach (var item in request.Items)
            {
                var p = await _db.Products.FindAsync(item.ProductId);
                p!.Stock -= item.Quantity;

                // ES'i de güncelle — dual-write senkronu
                var esResponse = await _es.IndexAsync(p, "products", i => i.Id(p.Id));
                if (!esResponse.IsValidResponse)
                    _logger.LogWarning("ES stok güncellenemedi: {ProductId}", p.Id);

                _logger.LogInformation("Stok düşürüldü: {ProductId}, {Quantity} adet, kalan {Kalan}",
                    p.Id, item.Quantity, p.Stock);
            }

            await _db.SaveChangesAsync();
            return Ok();
        }

        [HttpPost("restore-stock")]
        public async Task<IActionResult> RestoreStock(StockReductionRequest request)
        {

            if (request.Items.Count == 0) return BadRequest("Stok iadesi için en az bir ürün gerekli.");

            foreach(var item in request.Items)
            {

                if(item.Quantity <= 0)
                {
                    return BadRequest($"Geçersiz iade miktarı: {item.Quantity}");
                }

                var product = await _db.Products.FindAsync(item.ProductId);

                if(product == null)
                {
                    return NotFound($"Ürün bulunamadı: {item.ProductId}");
                }


            }

            foreach(var item in request.Items)
            {

                var product = await _db.Products.FindAsync(item.ProductId);

                product!.Stock += item.Quantity;


                var esResponse = await _es.IndexAsync(
                    product,
                    "products",
                    i => i.Id(product.Id)
                 );

                if (!esResponse.IsValidResponse)
                {
                    _logger.LogWarning(
                        "İade sonrası Elasticsearch stok güncellenemedi: {ProductId}",
                        product.Id
                    );
                }

                _logger.LogInformation(
                    "İade stoğu geri eklendi: {ProductId}, {Quantity} adet, yeni stok: {Stock}",
                    product.Id,
                    item.Quantity,
                    product.Stock
                );

            }

            await _db.SaveChangesAsync();

            return Ok(new {message = "Stoklar geri eklendi."});

        }


        [HttpGet("categories")]
        public async Task<IActionResult> GetAllCategories()
        {
            var categories = await _db.Products
                .Select(p => p.Category)
                .Distinct()
                .Where(c => c != "" && c != null)
                .OrderBy(c => c)
                .ToListAsync();

            return Ok(categories);
        }

        [HttpGet("category/{category}")]
        public async Task<IActionResult> GetByCategory(string category)
        {
            var products = await _db.Products
                .Where(p => p.Category == category)
                .ToListAsync();

            _logger.LogInformation("Kategori filtrelendi: {Category} - {Count} ürün", category, products.Count);

            return Ok(products);
        }

    }
}
