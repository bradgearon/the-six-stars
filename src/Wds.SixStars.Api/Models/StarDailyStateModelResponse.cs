using System.Collections.Generic;
using Wds.SixStars.Api.SpeechIntents;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// LLM-authored content for the per-day Star state JSON. The API writes the JSON itself
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
        /// <summary>Specific optional topics or questions this Star wants to bring up with the Last Cat during at-rest initiative checks.</summary>
        public List<string> ThingsSheWantsToTalkAbout { get; set; } = new List<string>();
        /// <summary>Structured durable speech intentions ranked independently of the daily-state JSON.</summary>
        public List<StarSpeechIntentCandidate> ThingsSheWantsToSay { get; set; } = new List<StarSpeechIntentCandidate>();
        public List<string> ConversationHooks { get; set; } = new List<string>();
    }
}
