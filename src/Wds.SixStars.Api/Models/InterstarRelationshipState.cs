using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Durable one-way relationship view from the owning Star toward another Star.
    /// This is separate from the Last Cat relationship meters.
    /// </summary>
    public sealed class InterstarRelationshipState
    {
        public string StarId { get; set; } = string.Empty;
        public string StarName { get; set; } = string.Empty;
        public bool IsMet { get; set; }
        public bool IsAlly { get; set; }
        public bool IsGirlfriend { get; set; }
        public int TrustScore { get; set; }
        public int HeartScore { get; set; }
        public string LastPositiveEmotionVector { get; set; } = string.Empty;
        public string LastNegativeEmotionVector { get; set; } = string.Empty;
        public DateTimeOffset? LastUpdatedAt { get; set; }
        public List<InterstarRelationshipHistoryEntry> History { get; set; } = new List<InterstarRelationshipHistoryEntry>();
    }

    public sealed class InterstarRelationshipHistoryEntry
    {
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string SourceStarId { get; set; } = string.Empty;
        public string AffectedStarId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string PositiveEmotionVector { get; set; } = string.Empty;
        public string NegativeEmotionVector { get; set; } = string.Empty;
        public int HeartDelta { get; set; }
        public int TrustDelta { get; set; }
        public int HeartAfter { get; set; }
        public int TrustAfter { get; set; }
        public string Summary { get; set; } = string.Empty;
    }

    public sealed class InterstarEmotionVectorResult
    {
        public string StarId { get; set; } = string.Empty;
        public string PositiveEmotionVector { get; set; } = string.Empty;
        public string NegativeEmotionVector { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class InterstarRelationshipDeltaResult
    {
        public string OwnerStarId { get; set; } = string.Empty;
        public string AffectedStarId { get; set; } = string.Empty;
        public int HeartDelta { get; set; }
        public int TrustDelta { get; set; }
        public int HeartAfter { get; set; }
        public int TrustAfter { get; set; }
        public string PositiveEmotionVector { get; set; } = string.Empty;
        public string NegativeEmotionVector { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
    }
}
