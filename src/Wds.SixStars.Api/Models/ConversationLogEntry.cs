using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    public sealed class ConversationLogEntry
    {
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string RequestType { get; set; } = string.Empty;
        public string PlayerText { get; set; } = string.Empty;
        public string StarResponse { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public RelationshipDelta ActualDelta { get; set; } = RelationshipDelta.Zero();
        public RelationshipDelta SuggestedDelta { get; set; } = RelationshipDelta.Zero();
        public double WisdomScore { get; set; }
        public List<string> ImportantQuotes { get; set; } = new List<string>();
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> RelationshipEvents { get; set; } = new List<string>();
        public List<string> LearnedFunctionIds { get; set; } = new List<string>();
        public List<string> SecretsUnlocked { get; set; } = new List<string>();
    }
}
