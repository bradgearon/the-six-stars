using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Deterministic relationship modifier applied over time by host/gameplay code.
    /// Unity can add these when story pressure, jealousy, danger, curse effects, romance-rival pressure,
    /// settlement outcomes, or special scenes should temporarily change how a Star's state updates.
    /// </summary>
    public sealed class StarRelationshipEffector
    {
        public string Id { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ExpiresAt { get; set; }

        /// <summary>Optional duration in seconds. If set by an API caller and ExpiresAt is empty, the memory API converts it to ExpiresAt.</summary>
        public int DurationSeconds { get; set; }

        /// <summary>Optional turn limit. Zero means time-based/indefinite until removed.</summary>
        public int RemainingTurns { get; set; }

        /// <summary>How many deterministic state updates have used this effector.</summary>
        public int AppliedTurns { get; set; }

        /// <summary>Multiplies positive EmotionalConnectionPercent deltas. 1.0 means unchanged.</summary>
        public double ConnectionGainMultiplier { get; set; } = 1.0;

        /// <summary>Multiplies negative EmotionalConnectionPercent deltas. 1.0 means unchanged.</summary>
        public double ConnectionLossMultiplier { get; set; } = 1.0;

        /// <summary>When true, small passive attention gains are suppressed, but large earned gains can still pass.</summary>
        public bool SuppressPassiveConnectionGain { get; set; }

        public int HeartDeltaPerTurn { get; set; }
        public int TrustDeltaPerTurn { get; set; }
        public int RomanceDeltaPerTurn { get; set; }
        public int BenaveilInfluenceDeltaPerTurn { get; set; }

        public List<string> Tags { get; set; } = new List<string>();

        public bool IsExpired(DateTimeOffset now)
        {
            if (!IsActive) return true;
            if (ExpiresAt.HasValue && ExpiresAt.Value <= now) return true;
            if (RemainingTurns > 0 && AppliedTurns >= RemainingTurns) return true;
            return false;
        }
    }
}
