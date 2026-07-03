namespace Wds.SixStars.Api.Models
{
    public sealed class EmotionScore
    {
        public double Score { get; set; }
        public double Confidence { get; set; }
        public string? Reason { get; set; }
    }
}
