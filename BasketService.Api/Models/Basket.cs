namespace BasketService.Api.Models
{
    public class Basket
    {

        public int Id { get; set; }
        public string UserId { get; set; } = "";

        public List<BasketItem> Items { get; set; } = new();

    }
}
