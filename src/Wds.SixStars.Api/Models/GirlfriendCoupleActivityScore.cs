using System;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Durable girlfriend-mode running score for a couple activity such as hand-holding, kissing, cuddling,
    /// petting, stroking, or deeper romantic/textual intimacy. Score is accumulated deterministically from
    /// compact vectors using score * confidence.
    /// </summary>
    public sealed class GirlfriendCoupleActivityScore
    {
        public string ActivityId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public double TotalScore { get; set; }
        public double HighestSingleScore { get; set; }
        public double HighestConfidence { get; set; }
        public double LastScoreAdded { get; set; }
        public string LastReason { get; set; } = string.Empty;
        public DateTimeOffset? LastUpdatedAt { get; set; }
        public int Count { get; set; }
    }
}
