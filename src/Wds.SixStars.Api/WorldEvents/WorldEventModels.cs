using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.WorldEvents
{
    /// <summary>
    /// One normalized message or event delivered by The Last Cat game, Bradmageddon,
    /// a father-form feed, or another configured world source.
    /// </summary>
    public sealed class WorldEventRecord
    {
        public long Sequence { get; set; }
        public string EventId { get; set; } = string.Empty;
        public string Source { get; set; } = "manual";
        public string SourceEventId { get; set; } = string.Empty;
        public string Kind { get; set; } = "message";
        public DateTimeOffset OccurredAt { get; set; }
        public DateTimeOffset ReceivedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public int Importance { get; set; } = 50;
        public int Authority { get; set; }
        public string Scope { get; set; } = "world";
        public List<string> TargetStars { get; set; } = new List<string>();
        public string Location { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
        public bool RequiresStateRefresh { get; set; } = true;
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class WorldEventBatch
    {
        public bool Ok { get; set; } = true;
        public long AfterSequence { get; set; }
        public long LatestSequence { get; set; }
        public List<WorldEventRecord> Events { get; set; } = new List<WorldEventRecord>();
    }

    public sealed class WorldEventInsertResult
    {
        public bool Ok { get; set; }
        public int AcceptedCount { get; set; }
        public int DuplicateCount { get; set; }
        public long LatestSequence { get; set; }
        public bool StateRefreshQueued { get; set; }
        public List<string> AffectedStars { get; set; } = new List<string>();
        public List<WorldEventRecord> AcceptedEvents { get; set; } = new List<WorldEventRecord>();
        public string Error { get; set; } = string.Empty;
    }

    public sealed class WorldEventAddedEventArgs : EventArgs
    {
        public WorldEventRecord Event { get; set; } = new WorldEventRecord();
    }
}
