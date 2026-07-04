using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    public sealed class EmotionUpdateHistoryEntry
    {
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string RequestType { get; set; } = string.Empty;
        public string PlayerText { get; set; } = string.Empty;
        public string EmotionalEffectVector { get; set; } = string.Empty;
        public string NegativeEmotionVector { get; set; } = string.Empty;
        public List<string> AcceptedPositiveSignals { get; set; } = new List<string>();
        public List<string> AcceptedNegativeSignals { get; set; } = new List<string>();
        public List<string> AcceptedCombos { get; set; } = new List<string>();
        public RelationshipDelta SuggestedDelta { get; set; } = RelationshipDelta.Zero();
        public RelationshipDelta ActualDelta { get; set; } = RelationshipDelta.Zero();
        public double EmotionalConnectionPercentBefore { get; set; }
        public double EmotionalConnectionPercentAfter { get; set; }
        public bool IsGirlfriendReadyAfter { get; set; }
        public string Summary { get; set; } = string.Empty;
    }
}
