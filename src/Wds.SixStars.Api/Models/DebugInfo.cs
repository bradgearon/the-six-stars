using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    public sealed class DebugInfo
    {
        public string DetectedIntent { get; set; } = string.Empty;
        public double WisdomScore { get; set; }
        public List<string> RiskFlags { get; set; } = new List<string>();
        public List<string> LoreUsed { get; set; } = new List<string>();
        public List<string> Notes { get; set; } = new List<string>();
    }
}
