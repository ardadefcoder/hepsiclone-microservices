namespace PaymentService.Api.Models
{
    public class Order
    {
        public int Id { get; set; }
        public string UserId { get; set; } = "";
        public decimal TotalAmount { get; set; }
        public string Status { get; set; } = "";              // "Paid", "Failed"
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public List<OrderItem> Items { get; set; } = new();    // siparişteki ürünler (snapshot)
    }
}