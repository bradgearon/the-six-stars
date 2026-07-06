using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// LLM-authored content for the per-day Star state XML. The API writes the XML itself
    /// so the saved file is always valid and easy for Unity/game code to consume.
    /// </summary>
    public sealed class StarDailyStateModelResponse
    {
        public string StarId { get; set; } = string.Empty;
        public string SituationSummary { get; set; } = string.Empty;
        public string FeelingsSummary { get; set; } = string.Empty;
        public string WhatSheIsDoingToday { get; set; } = string.Empty;
        public string CurrentDesires { get; set; } = string.Empty;
        public string RelationshipPosture { get; set; } = string.Empty;
        public string RecentPeopleSeenSummary { get; set; } = string.Empty;
        public string RecentEventsSummary { get; set; } = string.Empty;
        public string PrivateSecretsOrHooks { get; set; } = string.Empty;
        public string PromptUseSummary { get; set; } = string.Empty;
        public List<string> MoodTags { get; set; } = new List<string>();
        public List<string> RecentPeopleSeen { get; set; } = new List<string>();
        public List<string> RecentEvents { get; set; } = new List<string>();
        public List<string> TodayIntentions { get; set; } = new List<string>();
        public List<string> ConversationHooks { get; set; } = new List<string>();
    }
}
