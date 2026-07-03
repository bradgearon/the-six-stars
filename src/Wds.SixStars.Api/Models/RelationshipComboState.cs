using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Tracks repeated emotional/social combo patterns such as affection_plus_wisdom,
    /// cruel_escalation, successful_story_comfort, or flirtation_plus_respect.
    /// </summary>
    public sealed class RelationshipComboState
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public DateTimeOffset LastTriggeredAt { get; set; } = DateTimeOffset.UtcNow;
        public string Summary { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new List<string>();
    }
}
