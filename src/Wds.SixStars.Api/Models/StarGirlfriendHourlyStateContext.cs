using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    public sealed class StarGirlfriendHourlyStateContext
    {
        public bool IsAvailable { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string SlotId { get; set; } = string.Empty;
        public DateTimeOffset? GeneratedAt { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string RelationshipPosture { get; set; } = string.Empty;
        public string NeedSummary { get; set; } = string.Empty;
        public string WantSummary { get; set; } = string.Empty;
        public double OverallConfidence { get; set; }
        public double OverallScore { get; set; }
        public string Json { get; set; } = string.Empty;
        public List<GirlfriendVectorItem> NeedVector { get; set; } = new List<GirlfriendVectorItem>();
        public List<GirlfriendVectorItem> WantVector { get; set; } = new List<GirlfriendVectorItem>();
        public List<string> ThingsSheWantsToSay { get; set; } = new List<string>();
        public List<string> CuteThingsSheWantsToSay { get; set; } = new List<string>();
    }
}
