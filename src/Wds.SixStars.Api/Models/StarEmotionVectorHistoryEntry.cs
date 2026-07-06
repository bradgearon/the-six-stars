using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Full deterministic emotion-vector history entry for one Star. Stored in StarState and kept bounded.
    /// The prompt does not receive this whole history; the deterministic layer uses it for combo/relationship scoring.
    /// </summary>
    public sealed class StarEmotionVectorHistoryEntry
    {
        public string TurnId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string RequestType { get; set; } = string.Empty;
        public string PlayerText { get; set; } = string.Empty;
        public List<StarEmotionVectorHistoryItem> Entries { get; set; } = new List<StarEmotionVectorHistoryItem>();
        public List<string> ComboHits { get; set; } = new List<string>();
        public string Summary { get; set; } = string.Empty;
    }
}
