namespace Shop.Api.Models
{
    /// <summary>주문 취소 요청 본문</summary>
    public class CancelRequest
    {
        public string Reason { get; set; }
    }
}
