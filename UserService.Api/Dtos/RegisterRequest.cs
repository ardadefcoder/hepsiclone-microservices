namespace UserService.Api.Dtos
{
    public class RegisterRequest
    {
        public string Username { get; set; } = "";
        public string Email { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Password { get; set; } = "";     // düz şifre — sadece kayıt anında gelir, hash'lenip atılır
    }
}