using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Host-authored full dialogue/script/progress state. Use this when Unity has a whole authored dialogue tree
    /// or needs the Star to know exactly where she is in a written scene.
    /// </summary>
    public sealed class StarDialogueProgressState
    {
        public string DialogId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string SceneId { get; set; } = string.Empty;
        public string Area { get; set; } = string.Empty;
        public string QuestId { get; set; } = string.Empty;
        public string CurrentNodeId { get; set; } = string.Empty;
        public int CurrentLineIndex { get; set; }
        public string CurrentStage { get; set; } = string.Empty;
        public string SummarySoFar { get; set; } = string.Empty;
        public string LastAuthoredLine { get; set; } = string.Empty;
        public string NextExpectedBeat { get; set; } = string.Empty;
        public string FullScriptText { get; set; } = string.Empty;
        public string FullScriptJson { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public bool IsComplete { get; set; }
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public List<string> Tags { get; set; } = new List<string>();
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }
}
