namespace PaymentService.Api.Dtos
{
    public class RefundRequest
    {
        public int OrderId { get; set; }
        public string UserId { get; set; } = "";   // sadece kendi siparişini iade edebilsin
    }
}