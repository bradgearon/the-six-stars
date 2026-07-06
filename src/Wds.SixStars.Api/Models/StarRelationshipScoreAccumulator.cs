using System;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Durable deterministic score remainder for Heart/Trust/Romance. Small emotion-vector turns can add up over time
    /// without the LLM directly awarding relationship points.
    /// </summary>
    public sealed class StarRelationshipScoreAccumulator
    {
        public string Stat { get; set; } = string.Empty;
        public double Score { get; set; }
        public double LastScoreAdded { get; set; }
        public int TotalPositivePoints { get; set; }
        public int TotalNegativePoints { get; set; }
        public DateTimeOffset? LastUpdatedAt { get; set; }
        public string LastReason { get; set; } = string.Empty;
    }
}
