using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using PaymentService.Api.Controllers;
using PaymentService.Api.Data;
using PaymentService.Api.Dtos;
using PaymentService.Api.Models;

namespace PaymentService.Tests
{
    public class PaymentControllerTests
    {
        // Aynı test içinde birden çok context açabilmek için sabit DB adı
        private readonly string _dbName = Guid.NewGuid().ToString();

        // Her çağrı için TAZE context (gerçek hayatta her istek kendi scope'unda)
        private PaymentDbContext NewDb()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;
            return new PaymentDbContext(options);
        }

        // Basket/Product servislerini taklit eden sahte HttpClient
        // (verilen status + body'yi her çağrıda döner)
        private IHttpClientFactory FakeHttpService(HttpStatusCode status, object? body)
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
                                Encoding.UTF8, "application/json")
                    });

                return new HttpClient(handler.Object) { BaseAddress = new Uri("https://fake-service") };
            });
            return factory.Object;
        }

        // HTTP hiç kullanılmayan testler için yeterli (çağrılırsa 200 döner ama çağrılmaz)
        private IHttpClientFactory NoHttp() => FakeHttpService(HttpStatusCode.OK, null);

        private PaymentController NewController(PaymentDbContext db, IHttpClientFactory factory)
        {
            var logger = new Mock<ILogger<PaymentController>>();
            return new PaymentController(db, factory, logger.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        // Veritabanına hazır bir sipariş ekler
        private async Task SeedOrderAsync(int id, string userId, string status, decimal total = 1000m)
        {
            using var db = NewDb();
            db.Orders.Add(new Order
            {
                Id = id,
                UserId = userId,
                TotalAmount = total,
                Status = status,
                Items = new List<OrderItem>
                {
                    new OrderItem { ProductId = 1, ProductName = "Laptop", Price = total, Quantity = 1 }
                }
            });
            await db.SaveChangesAsync();
        }

        // ==================== RequestRefund ====================

        // ---------- TEST 1: Geçersiz sipariş numarası ----------
        [Fact]
        public async Task RequestRefund_GecersizOrderId_BadRequest()
        {
            using var db = NewDb();
            var controller = NewController(db, NoHttp());

            var result = await controller.RequestRefund(new RefundRequest { OrderId = 0, UserId = "arda" });

            Assert.IsType<BadRequestObjectResult>(result);
        }

        // ---------- TEST 2: Sipariş yok ----------
        [Fact]
        public async Task RequestRefund_SiparisYoksa_NotFound()
        {
            using var db = NewDb();
            var controller = NewController(db, NoHttp());

            var result = await controller.RequestRefund(new RefundRequest { OrderId = 999, UserId = "arda" });

            Assert.IsType<NotFoundObjectResult>(result);
        }

        // ---------- TEST 3: Başkasının siparişi → 403 Forbid ----------
        [Fact]
        public async Task RequestRefund_BaskasininSiparisi_Forbid()
        {
            await SeedOrderAsync(1, userId: "ali", status: "Paid");

            using var db = NewDb();
            var controller = NewController(db, NoHttp());

            // arda, ali'nin siparişini iade etmeye çalışıyor
            var result = await controller.RequestRefund(new RefundRequest { OrderId = 1, UserId = "arda" });

            Assert.IsType<ForbidResult>(result);
        }

        // ---------- TEST 4: Ödenmemiş sipariş iade edilemez ----------
        [Fact]
        public async Task RequestRefund_OdenmemisSiparis_BadRequest()
        {
            await SeedOrderAsync(1, userId: "arda", status: "Failed");

            using var db = NewDb();
            var controller = NewController(db, NoHttp());

            var result = await controller.RequestRefund(new RefundRequest { OrderId = 1, UserId = "arda" });

            Assert.IsType<BadRequestObjectResult>(result);
        }

        // ---------- TEST 5: Geçerli talep → Ok + durum RefundRequested ----------
        [Fact]
        public async Task RequestRefund_GecerliTalep_DurumRefundRequested()
        {
            await SeedOrderAsync(1, userId: "arda", status: "Paid");

            using var db = NewDb();
            var controller = NewController(db, NoHttp());

            var result = await controller.RequestRefund(new RefundRequest { OrderId = 1, UserId = "arda" });

            Assert.IsType<OkObjectResult>(result);

            using var check = NewDb();
            var order = await check.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("RefundRequested", order.Status);
        }

        // ==================== DecideRefund ====================

        // ---------- TEST 6: Sipariş yok ----------
        [Fact]
        public async Task DecideRefund_SiparisYoksa_NotFound()
        {
            using var db = NewDb();
            var controller = NewController(db, NoHttp());

            var result = await controller.DecideRefund(new RefundDecisionRequest { OrderId = 999, Approve = true });

            Assert.IsType<NotFoundObjectResult>(result);
        }

        // ---------- TEST 7: Bekleyen talep yoksa reddet ----------
        [Fact]
        public async Task DecideRefund_BekleyenTalepYoksa_BadRequest()
        {
            await SeedOrderAsync(1, userId: "arda", status: "Paid");   // RefundRequested değil

            using var db = NewDb();
            var controller = NewController(db, NoHttp());

            var result = await controller.DecideRefund(new RefundDecisionRequest { OrderId = 1, Approve = true });

            Assert.IsType<BadRequestObjectResult>(result);
        }

        // ---------- TEST 8: Talep reddedilirse durum Paid'e döner ----------
        [Fact]
        public async Task DecideRefund_Reddedilirse_DurumPaid()
        {
            await SeedOrderAsync(1, userId: "arda", status: "RefundRequested");

            using var db = NewDb();
            var controller = NewController(db, NoHttp());

            var result = await controller.DecideRefund(new RefundDecisionRequest { OrderId = 1, Approve = false });

            Assert.IsType<OkObjectResult>(result);

            using var check = NewDb();
            var order = await check.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("Paid", order.Status);
        }

        // ---------- TEST 9: Onaylanır + stok servisi OK → durum Refunded ----------
        [Fact]
        public async Task DecideRefund_Onaylanir_StokServisiOk_DurumRefunded()
        {
            await SeedOrderAsync(1, userId: "arda", status: "RefundRequested");

            using var db = NewDb();
            // ProductService restore-stock çağrısı 200 döner
            var controller = NewController(db, FakeHttpService(HttpStatusCode.OK, new { message = "ok" }));

            var result = await controller.DecideRefund(new RefundDecisionRequest { OrderId = 1, Approve = true });

            Assert.IsType<OkObjectResult>(result);

            using var check = NewDb();
            var order = await check.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("Refunded", order.Status);
        }

        // ---------- TEST 10: Onaylanır ama stok servisi çökmüş → 503 ----------
        [Fact]
        public async Task DecideRefund_Onaylanir_StokServisiCokerse_503()
        {
            await SeedOrderAsync(1, userId: "arda", status: "RefundRequested");

            using var db = NewDb();
            // ProductService 500 döner → iade işlenemez
            var controller = NewController(db, FakeHttpService(HttpStatusCode.InternalServerError, null));

            var result = await controller.DecideRefund(new RefundDecisionRequest { OrderId = 1, Approve = true });

            var status = Assert.IsType<ObjectResult>(result);
            Assert.Equal(503, status.StatusCode);

            // durum hâlâ RefundRequested (Refunded'a geçmedi)
            using var check = NewDb();
            var order = await check.Orders.FirstAsync(o => o.Id == 1);
            Assert.Equal("RefundRequested", order.Status);
        }

        // ==================== Checkout ====================

        // ---------- TEST 11: Boş sepetle ödeme → BadRequest ----------
        [Fact]
        public async Task Checkout_BosSepet_BadRequest()
        {
            using var db = NewDb();
            // BasketService boş sepet döner
            var emptyBasket = new { userId = "arda", items = Array.Empty<object>() };
            var controller = NewController(db, FakeHttpService(HttpStatusCode.OK, emptyBasket));

            var result = await controller.Checkout(new CheckoutRequest { UserId = "arda" });

            Assert.IsType<BadRequestObjectResult>(result);
        }

        // ---------- TEST 12: Sepet servisi çökmüş → 503 ----------
        [Fact]
        public async Task Checkout_SepetServisiCokmus_503()
        {
            using var db = NewDb();
            // BasketService 500 döner → GetFromJsonAsync patlar → 503
            var controller = NewController(db, FakeHttpService(HttpStatusCode.InternalServerError, null));

            var result = await controller.Checkout(new CheckoutRequest { UserId = "arda" });

            var status = Assert.IsType<ObjectResult>(result);
            Assert.Equal(503, status.StatusCode);
        }
    }
}
