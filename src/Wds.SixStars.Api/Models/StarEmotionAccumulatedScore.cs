using System;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Durable per-Star cumulative emotion totals. Kept simple/public-property based so Unity can
    /// serialize it into save data. Scores are accumulated only when deterministic emotion signals
    /// clear confidence/score thresholds.
    /// </summary>
    public sealed class StarEmotionAccumulatedScore
    {
        public string EmotionId { get; set; } = string.Empty;
        public string Valence { get; set; } = string.Empty;
        public double TotalScore { get; set; }
        public double TotalWeightedScore { get; set; }
        public int Count { get; set; }
        public double LastConfidence { get; set; }
        public double LastScore { get; set; }
        public string LastReason { get; set; } = string.Empty;
        public DateTimeOffset? FirstSeenAt { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }
    }
}
