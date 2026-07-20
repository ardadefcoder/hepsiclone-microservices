using Microsoft.EntityFrameworkCore;
using PaymentService.Api.Data;
using PaymentService.Api.Middleware;
using Serilog;
using Serilog.Sinks.Elasticsearch;

var builder = WebApplication.CreateBuilder(args);

var esUrl = builder.Configuration["ServiceUrls:Elasticsearch"];
var basketServiceUrl = builder.Configuration["ServiceUrls:BasketService"];

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Async(a => a.Console())
    .WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(esUrl!))
    {
        AutoRegisterTemplate = true,
        IndexFormat = "paymentservice-{0:yyyy.MM.dd}",
        BatchPostingLimit = 50,
        Period = TimeSpan.FromSeconds(2)
    })
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// BasketService'e gidecek HttpClient
builder.Services.AddHttpClient("BasketService", c =>
{
    c.BaseAddress = new Uri(basketServiceUrl!);
    c.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddHttpClient("ProductService", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["ServiceUrls:ProductService"]!);
    c.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseCors("Frontend");

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}