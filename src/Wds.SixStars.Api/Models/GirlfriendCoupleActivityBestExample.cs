using System;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Best/highest-scoring Last Cat wording example for a couple activity, retained so girlfriend prompts
    /// can show the Star what kind of affection has worked between them.
    /// </summary>
    public sealed class GirlfriendCoupleActivityBestExample
    {
        public string ActivityId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public double WeightedScore { get; set; }
        public double Score { get; set; }
        public double Confidence { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string PlayerText { get; set; } = string.Empty;
        public string StarResponse { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
    }
}
