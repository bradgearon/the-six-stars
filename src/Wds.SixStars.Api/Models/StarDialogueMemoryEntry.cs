using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// A durable line/beat of authored in-game dialogue that this Star has completed or should remember.
    /// Unity can append these after cutscenes, companion banter, settlement scenes, or Star-specific dialogue nodes.
    /// </summary>
    public sealed class StarDialogueMemoryEntry
    {
        public string Id { get; set; } = string.Empty;
        public string DialogId { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Speaker { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string SceneId { get; set; } = string.Empty;
        public string Area { get; set; } = string.Empty;
        public string QuestId { get; set; } = string.Empty;
        public int SequenceIndex { get; set; }
        public bool IsComplete { get; set; } = true;
        public string Source { get; set; } = "game";
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAt { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }
}
