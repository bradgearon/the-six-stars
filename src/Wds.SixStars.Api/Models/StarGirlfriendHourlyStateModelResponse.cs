using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    public sealed class StarGirlfriendHourlyStateModelResponse
    {
        public string StarId { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string RelationshipPosture { get; set; } = string.Empty;
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
