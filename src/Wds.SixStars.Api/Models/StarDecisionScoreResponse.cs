namespace Wds.SixStars.Api.Models
{
    public sealed class StarDecisionScoreResponse
    {
        public string StarId { get; set; } = string.Empty;
        public string SceneKey { get; set; } = string.Empty;
        public string ScoreOrder { get; set; } = string.Empty;
        public string Scores { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string ReasonCode { get; set; } = string.Empty;
        public string ShortExplanation { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }
}
