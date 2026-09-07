using System;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Temporary continuity memory for immediate activities, plans, and scene context.
    /// The host timestamps these deterministically and removes them after the configured TTL.
    /// </summary>
    public sealed class StarShortTermMemoryEntry
    {
        public string Id { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Source { get; set; } = "background-review";
    }
}
