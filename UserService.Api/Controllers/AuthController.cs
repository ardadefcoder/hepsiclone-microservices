using BCrypt.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UserService.Api.Data;
using UserService.Api.Dtos;
using UserService.Api.Models;
using UserService.Api.Services;

namespace UserService.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly UserDbContext _db;
        private readonly TokenService _tokenService;
        private readonly ILogger<AuthController> _logger;

        public AuthController(UserDbContext db, TokenService tokenService, ILogger<AuthController> logger)
        {
            _db = db;
            _tokenService = tokenService;
            _logger = logger;
        }

        // ---- KAYIT ----
        [HttpPost("register")]
        public async Task<IActionResult> Register(RegisterRequest request)
        {
            // Kullanıcı adı zaten var mı?
            if (await _db.Users.AnyAsync(u => u.Username == request.Username))
            {
                _logger.LogWarning("Kayıt reddedildi, kullanıcı adı alınmış: {Username}", request.Username);
                return BadRequest("Bu kullanıcı adı zaten alınmış");
            }
            if (await _db.Users.AnyAsync(u => u.Email == request.Email))
                return BadRequest("Bu e-posta zaten kayıtlı");

            // 🔐 ŞİFREYİ HASH'LE — düz şifreyi ASLA saklamıyoruz
            var user = new User
            {
                Username = request.Username,
                Email = request.Email,
                FullName = request.FullName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password)
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Yeni kullanıcı kaydedildi: {Username} (id: {Id})", user.Username, user.Id);

            // Kayıttan sonra direkt token ver (kullanıcı tekrar login olmasın)
            var token = _tokenService.CreateToken(user);
            return Ok(new { token, user = ToResponse(user) });
        }

        // ---- GİRİŞ ----
        [HttpPost("login")]
        public async Task<IActionResult> Login(LoginRequest request)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == request.Username);

            // Kullanıcı yok VEYA şifre yanlış → aynı mesaj (güvenlik: hangisi yanlış belli olmasın)
            if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            {
                _logger.LogWarning("Başarısız giriş denemesi: {Username}", request.Username);
                return Unauthorized("Kullanıcı adı veya şifre hatalı");
            }

            _logger.LogInformation("Giriş başarılı: {Username}", user.Username);

            var token = _tokenService.CreateToken(user);
            return Ok(new { token, user = ToResponse(user) });
        }

        // User → UserResponse (PasswordHash'i dışarı sızdırmamak için)
        private static UserResponse ToResponse(User u) => new UserResponse
        {
            Id = u.Id,
            Username = u.Username,
            Email = u.Email,
            FullName = u.FullName
        };
    }
}