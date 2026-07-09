namespace BasketService.Api.Dtos
{
    public class AddItemRequest
    {

        public string UserId { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
    }
}
