using System.Collections.Generic;

namespace Wds.SixStars.Api.Stars
{
    /// <summary>
    /// Authored Star-specific emotional combo. The deterministic layer scans recent stored vectors for these patterns.
    /// </summary>
    public sealed class StarEmotionComboRule
    {
        public string ComboId { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int WithinTurns { get; set; } = 4;
        public int MinimumDistinctEmotions { get; set; } = 3;
        public List<StarEmotionComboRequirement> Requires { get; set; } = new List<StarEmotionComboRequirement>();
        public double BonusMultiplier { get; set; } = 1.25;
        public Dictionary<string, double> StatWeights { get; set; } = new Dictionary<string, double>();
        public int CooldownTurns { get; set; } = 3;
        public List<string> Tags { get; set; } = new List<string>();
    }
}
