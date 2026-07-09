namespace BasketService.Api.Models
{
    public class BasketItem
    {

        public int Id { get; set; }
        public int ProductId { get; set; }
        public string ProductName { get; set; } = "";
        public decimal Price { get; set; }
        public int Quantity { get; set; }   

        public int BasketId { get; set; }
        public Basket? Basket { get; set; }  // baskete giden yol. EF ilişkisi

    }
}
