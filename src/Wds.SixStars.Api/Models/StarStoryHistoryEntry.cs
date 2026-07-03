using System;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Durable memory of a long-form story a Star has told. Stores title/summary, not necessarily
    /// the whole story text, so future story prompts can avoid repeats and build continuity.
    /// </summary>
    public sealed class StarStoryHistoryEntry
    {
        public string StoryId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string Seed { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAt { get; set; }
        public bool Finished { get; set; }
    }
}
