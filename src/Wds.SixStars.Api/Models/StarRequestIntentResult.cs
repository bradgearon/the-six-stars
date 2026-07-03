namespace Wds.SixStars.Api.Models
{
    public sealed class StarRequestIntentResult
    {
        public bool IsStoryRequest { get; set; }
        public double Confidence { get; set; }
        public string StorySeed { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }
}
