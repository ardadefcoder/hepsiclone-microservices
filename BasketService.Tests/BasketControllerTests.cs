using System.Net;
using BasketService.Api.Controllers;
using BasketService.Api.Data;
using BasketService.Api.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using System.Text.Json;

namespace BasketService.Tests
{
    public class BasketControllerTests
    {
        // Aynı test içinde birden çok context açabilmek için sabit DB adı
        private readonly string _dbName = Guid.NewGuid().ToString();

        // Her çağrı için TAZE context (gerçek hayatta her istek kendi scope'unda)
        private BasketDbContext NewDb()
        {
            var options = new DbContextOptionsBuilder<BasketDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;
            return new BasketDbContext(options);
        }

        // ProductService'i taklit eden sahte HttpClient
        private IHttpClientFactory FakeProductService(HttpStatusCode status, object? body)
        {
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() =>
            {
                var handler = new Mock<HttpMessageHandler>();
                handler.Protected()
                    .Setup<Task<HttpResponseMessage>>("SendAsync",
                        ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync(() => new HttpResponseMessage
                    {
                        StatusCode = status,
                        Content = body == null
                            ? new StringContent("")
                            : new StringContent(JsonSerializer.Serialize(body),
                                System.Text.Encoding.UTF8, "application/json")
                    });

                return new HttpClient(handler.Object) { BaseAddress = new Uri("https://fake-product") };
            });
            return factory.Object;
        }

        private BasketController NewController(BasketDbContext db, IHttpClientFactory factory)
        {
            var logger = new Mock<ILogger<BasketController>>();
            return new BasketController(db, factory, logger.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        // Tek satırda "sepete ekle" çağrısı (taze context + taze controller)
        private async Task<IActionResult> AddItemAsync(object fakeProduct, HttpStatusCode status,
            string userId, int productId, int quantity)
        {
            using var db = NewDb();
            var controller = NewController(db, FakeProductService(status, fakeProduct));
            return await controller.AddItem(new AddItemRequest
            {
                UserId = userId,
                ProductId = productId,
                Quantity = quantity
            });
        }

        // ---------- TEST 1: Mutlu yol ----------
        [Fact]
        public async Task AddItem_StokYeterliyse_SepeteEkler()
        {
            var product = new { id = 1, name = "Laptop", price = 1000m, stock = 10 };

            var result = await AddItemAsync(product, HttpStatusCode.OK, "arda", 1, 2);

            Assert.IsType<OkObjectResult>(result);

            using var check = NewDb();
            var basket = await check.Baskets.Include(b => b.Items).FirstAsync();
            Assert.Single(basket.Items);
            Assert.Equal(2, basket.Items[0].Quantity);
            Assert.Equal("Laptop", basket.Items[0].ProductName);   // snapshot çalışıyor
            Assert.Equal(1000m, basket.Items[0].Price);
        }

        // ---------- TEST 2: Stok yetersiz ----------
        [Fact]
        public async Task AddItem_StokYetersizse_BadRequest()
        {
            var product = new { id = 1, name = "Laptop", price = 1000m, stock = 3 };

            var result = await AddItemAsync(product, HttpStatusCode.OK, "arda", 1, 5);   // 5 > 3

            Assert.IsType<BadRequestObjectResult>(result);

            using var check = NewDb();
            var basket = await check.Baskets.Include(b => b.Items).FirstOrDefaultAsync();
            Assert.True(basket == null || basket.Items.Count == 0);   // hiçbir şey eklenmedi
        }

        // ---------- TEST 3: BİRİKİMLİ stok kontrolü ----------
        [Fact]
        public async Task AddItem_SepettekiyleBirlikteStokAsarsa_BadRequest()
        {
            var product = new { id = 1, name = "Laptop", price = 1000m, stock = 3 };

            // 1. ekleme: 2 adet → başarılı
            var first = await AddItemAsync(product, HttpStatusCode.OK, "arda", 1, 2);
            Assert.IsType<OkObjectResult>(first);

            // 2. ekleme: 2 daha → toplam 4 > stok 3 → REDDEDİLMELİ
            var second = await AddItemAsync(product, HttpStatusCode.OK, "arda", 1, 2);
            Assert.IsType<BadRequestObjectResult>(second);

            using var check = NewDb();
            var basket = await check.Baskets.Include(b => b.Items).FirstAsync();
            Assert.Equal(2, basket.Items[0].Quantity);   // hâlâ 2, artmamış
        }

        // ---------- TEST 4: Aynı ürün tekrar eklenince adet artar ----------
        [Fact]
        public async Task AddItem_AyniUrunTekrarEklenirse_AdetArtar()
        {
            var product = new { id = 1, name = "Laptop", price = 1000m, stock = 10 };

            await AddItemAsync(product, HttpStatusCode.OK, "arda", 1, 2);
            await AddItemAsync(product, HttpStatusCode.OK, "arda", 1, 3);

            using var check = NewDb();
            var basket = await check.Baskets.Include(b => b.Items).FirstAsync();
            Assert.Single(basket.Items);                 // iki ayrı satır DEĞİL
            Assert.Equal(5, basket.Items[0].Quantity);   // 2 + 3
        }

        // ---------- TEST 5: Ürün yok ----------
        [Fact]
        public async Task AddItem_UrunYoksa_NotFound()
        {
            var result = await AddItemAsync(null, HttpStatusCode.NotFound, "arda", 999, 1);
            Assert.IsType<NotFoundObjectResult>(result);
        }

        // ---------- TEST 6: ProductService çökmüş ----------
        [Fact]
        public async Task AddItem_ProductServiceCokmusse_503()
        {
            var result = await AddItemAsync(null, HttpStatusCode.InternalServerError, "arda", 1, 1);

            var status = Assert.IsType<ObjectResult>(result);
            Assert.Equal(503, status.StatusCode);
        }
    }
}