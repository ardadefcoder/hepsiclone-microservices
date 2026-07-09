using System.Text;
using Swashbuckle.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using UserService.Api.Data;
using UserService.Api.Middleware;
using UserService.Api.Services;
using Serilog;
using Serilog.Sinks.Elasticsearch;

var builder = WebApplication.CreateBuilder(args);

var esUrl = builder.Configuration["ServiceUrls:Elasticsearch"];

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Async(a => a.Console())
    .WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(esUrl!))
    {
        AutoRegisterTemplate = true,
        IndexFormat = "userservice-{0:yyyy.MM.dd}",
        BatchPostingLimit = 50,
        Period = TimeSpan.FromSeconds(2)
    })
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddDbContext<UserDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// TokenService'i DI'a kaydet
builder.Services.AddScoped<TokenService>();

// 🔐 JWT Authentication kurulumu
var jwtKey = builder.Configuration["Jwt:Key"]!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,               // süresi dolmuş token reddedilsin
            ValidateIssuerSigningKey = true,        // imza doğru mu kontrol et
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

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseCors("Frontend");

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseAuthentication();    // 🔐 SIRA ÖNEMLİ: önce "sen kimsin" (authentication)
app.UseAuthorization();     //              sonra "yetkin var mı" (authorization)

app.MapControllers();

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}