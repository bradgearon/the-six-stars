using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Durable girlfriend-mode history entry for generated desired needs/wants and speech hooks.
    /// These entries persist across refreshes so the current prompt can stay small while the
    /// API still has a trace of what she has wanted, needed, or wanted to say since becoming girlfriend.
    /// </summary>
    public sealed class GirlfriendVectorHistoryEntry
    {
        public string Id { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string SlotId { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string Summary { get; set; } = string.Empty;
        public string NeedSummary { get; set; } = string.Empty;
        public string WantSummary { get; set; } = string.Empty;
        public List<GirlfriendVectorItem> NeedVector { get; set; } = new List<GirlfriendVectorItem>();
        public List<GirlfriendVectorItem> WantVector { get; set; } = new List<GirlfriendVectorItem>();
        public List<string> ThingsSheWantsToSay { get; set; } = new List<string>();
        public List<string> CuteThingsSheWantsToSay { get; set; } = new List<string>();
        public double OverallConfidence { get; set; }
        public double OverallScore { get; set; }
    }
}
