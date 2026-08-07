using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Archived conversation block. Recent turns stay in MemoryLog; older blocks are summarized
    /// but keep their source entries for nostalgia / old-detail retrieval. Unity-compatible POCO.
    /// </summary>
    public sealed class StarConversationArchiveEntry
    {
        public string Id { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset EndedAt { get; set; }
        public DateTimeOffset ArchivedAt { get; set; } = DateTimeOffset.UtcNow;
        public int EntryCount { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<string> TopicTags { get; set; } = new List<string>();
        public List<ConversationLogEntry> Entries { get; set; } = new List<ConversationLogEntry>();
    }
}
