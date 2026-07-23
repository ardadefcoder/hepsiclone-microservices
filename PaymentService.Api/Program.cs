using Microsoft.EntityFrameworkCore;
using PaymentService.Api.Data;
using PaymentService.Api.Middleware;
using Serilog;
using Serilog.Sinks.Elasticsearch;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

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
var jwtKey = builder.Configuration["Jwt:Key"]!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
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
builder.Services.AddSwaggerGen(options =>
{
    // Swagger UI'da "Authorize" butonu — korumalı endpoint'leri token ile test etmek için
    var scheme = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "JWT token'ı gir (Bearer öneki olmadan)",
        Reference = new Microsoft.OpenApi.Models.OpenApiReference
        {
            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
            Id = JwtBearerDefaults.AuthenticationScheme
        }
    };
    options.AddSecurityDefinition(JwtBearerDefaults.AuthenticationScheme, scheme);
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        { scheme, Array.Empty<string>() }
    });
});

var app = builder.Build();

// Container ortamında DB'yi oluştur + migration'ları uygula.
// RunMigrations yalnızca docker-compose'da set edilir; lokal geliştirme etkilenmez.
if (builder.Configuration.GetValue<bool>("RunMigrations"))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Database.Migrate();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseCors("Frontend");

app.UseSwagger();
app.UseSwaggerUI();


app.UseAuthentication();   // �nce
app.UseAuthorization();    // sonra


app.UseHttpsRedirection();

app.MapControllers();

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}