namespace UserService.Api.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; } = "";
        public string Email { get; set; } = "";
        public string FullName { get; set; } = "";
        public string PasswordHash { get; set; } = "";    // 🔐 şifrenin KENDİSİ değil, HASH'i
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}