using System.Collections.Generic;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.State
{
    public sealed class StarStateUpdateRequest
    {
        public string SessionId { get; set; } = "local-dev";
        public string StarId { get; set; } = string.Empty;
        public string RequestType { get; set; } = string.Empty;
        public string PlayerText { get; set; } = string.Empty;
        public string StarResponse { get; set; } = string.Empty;
        public string ConversationSummary { get; set; } = string.Empty;
        public string ChangesSummary { get; set; } = string.Empty;
        public RelationshipDelta ActualDelta { get; set; } = RelationshipDelta.Zero();
        public RelationshipDelta SuggestedDelta { get; set; } = RelationshipDelta.Zero();
        public Dictionary<string, EmotionScore> EmotionScores { get; set; } = new Dictionary<string, EmotionScore>();
        public List<string> ImportantPlayerQuotes { get; set; } = new List<string>();
        public List<string> MemoryNotes { get; set; } = new List<string>();
        public List<string> SecretsUnlocked { get; set; } = new List<string>();
        public List<string> DetectedCombos { get; set; } = new List<string>();
        public List<StarLearnedFunction> LearnedFunctions { get; set; } = new List<StarLearnedFunction>();
        public List<string> RelationshipEvents { get; set; } = new List<string>();
        public List<string> GirlfriendHistoryUpdates { get; set; } = new List<string>();
        public List<string> PetNamesPlayerUsesForStar { get; set; } = new List<string>();
        public List<string> PetNamesStarUsesForPlayer { get; set; } = new List<string>();
        public double WisdomScore { get; set; }
        public string StoryId { get; set; } = string.Empty;
        public string StoryTitle { get; set; } = string.Empty;
        public string StorySummary { get; set; } = string.Empty;
        public bool StoryFinished { get; set; }
        public string StorySeed { get; set; } = string.Empty;
    }
}
