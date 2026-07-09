namespace PaymentService.Api.Dtos
{
    public class BasketDto
    {
        public string UserId { get; set; } = "";
        public List<BasketItemDto> Items { get; set; } = new();
    }

    public class BasketItemDto
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = "";
        public decimal Price { get; set; }
        public int Quantity { get; set; }
    }
}