using System.Collections.Generic;

namespace Wds.SixStars.Api.Stars
{
    /// <summary>
    /// Star-specific deterministic meaning for one emotion vector slot.
    /// Polarity may be positive, negative, or contextual. StatWeights maps heart/trust/romance to score multipliers.
    /// </summary>
    public sealed class StarEmotionInfluenceRule
    {
        public string Emotion { get; set; } = string.Empty;
        public string Polarity { get; set; } = "positive";
        public double AffectsStar { get; set; } = 1.0;
        public Dictionary<string, double> StatWeights { get; set; } = new Dictionary<string, double>();
        public List<string> Tags { get; set; } = new List<string>();
    }
}
