using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProductService.Api.Data;  
using ProductService.Api.Models;
using Elastic.Clients.Elasticsearch;

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

        [HttpGet("{id}")]
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
                        .Fields(new[] {"name","description"})
                        .Query(q)
                        .Fuzziness(new Fuzziness("AUTO")
                        )
                    )
                )
            );

            _logger.LogInformation("Elasticsearch'te arama yapıldı: {Query}  - {Count}", q, response.Documents.Count);
            return Ok(response.Documents);
        }
    }
}
