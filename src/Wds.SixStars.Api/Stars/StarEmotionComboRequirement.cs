namespace Wds.SixStars.Api.Stars
{
    public sealed class StarEmotionComboRequirement
    {
        public string Emotion { get; set; } = string.Empty;
        public string Polarity { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;
        public double MinConfidence { get; set; } = 0.70;
        public double MinScore { get; set; } = 40;
    }
}
