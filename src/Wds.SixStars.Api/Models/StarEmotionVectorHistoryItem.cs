namespace Wds.SixStars.Api.Models
{
    public sealed class StarEmotionVectorHistoryItem
    {
        public string Emotion { get; set; } = string.Empty;
        public string Polarity { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public double Score { get; set; }
        public double WeightedScore { get; set; }
        public double AffectsStar { get; set; }
        public bool Accepted { get; set; }
        public double HeartScore { get; set; }
        public double TrustScore { get; set; }
        public double RomanceScore { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}
