namespace Wds.SixStars.Api.Models
{
    public sealed class GirlfriendVectorItem
    {
        public string ItemId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public double Score { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}
