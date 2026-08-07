using System;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Compact UI/API/debug view of the strongest entries in the latest deterministic emotion vectors.
    /// This is computed after the background state update so front ends can show what the latest turn
    /// actually affected without parsing the raw compact vectors.
    /// </summary>
    public sealed class TopAffectedEmotion
    {
        public string EmotionId { get; set; } = string.Empty;
        public string Valence { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public double Score { get; set; }
        public double WeightedScore { get; set; }
        public bool AcceptedForStateUpdate { get; set; }
        public string Summary { get; set; } = string.Empty;

        public override string ToString()
        {
            string sign = string.Equals(Valence, "negative", StringComparison.OrdinalIgnoreCase) ? "-" : "+";
            return EmotionId + " " + sign + Score.ToString("0") + " @ " + Confidence.ToString("0.00");
        }
    }
}
