using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>Durable master record of a player quote. The prompt uses a small distilled list; this keeps the source archive.</summary>
    public sealed class StarImportantQuoteRecord
    {
        public string Id { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string StarId { get; set; } = string.Empty;
        public string Quote { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string RequestType { get; set; } = string.Empty;
        public string EmotionalEffectVector { get; set; } = string.Empty;
        public string NegativeEmotionVector { get; set; } = string.Empty;
        public double WisdomScore { get; set; }
        public RelationshipDelta ActualDelta { get; set; } = RelationshipDelta.Zero();
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> MarkerIds { get; set; } = new List<string>();
    }
}
