using System;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Compact foreground hint from the Star when she needs more state, old conversation, lore,
    /// markers, bestiary, or character data before she can answer well. Unity-compatible POCO.
    /// </summary>
    public sealed class InformationNeedVectorItem
    {
        public string DataType { get; set; } = string.Empty;
        public string Subject { get; set; } = "self";
        public double Confidence { get; set; }
        public double Score { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}
