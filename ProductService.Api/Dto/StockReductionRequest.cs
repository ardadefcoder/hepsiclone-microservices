namespace ProductService.Api.Dtos
{
    public class StockReductionRequest
    {
        public List<StockItem> Items { get; set; } = new();
    }

    public class StockItem
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
    }
}