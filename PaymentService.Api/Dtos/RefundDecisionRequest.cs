namespace PaymentService.Api.Dtos
{
    public class RefundDecisionRequest
    {
        public int OrderId { get; set; }
        public bool Approve { get; set; }   // true = onayla, false = reddet
    }
}