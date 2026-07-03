using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Ai
{
    public sealed class StarStructuredModelResponse
    {
        public string StarId { get; set; } = string.Empty;
        [JsonPropertyName("spokenText")]
        [JsonConverter(typeof(SpokenTextPartsJsonConverter))]
        public List<string> SpokenParts { get; set; } = new List<string>();

        [JsonIgnore]
        public string SpokenText
        {
            get => string.Join(" ", SpokenParts.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
            set
            {
                SpokenParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(value)) SpokenParts.Add(value.Trim());
            }
        }
        public string? StoryTitle { get; set; }
        public StoryKind StoryKind { get; set; } = StoryKind.None;
        public string StoryId { get; set; } = string.Empty;
        public string StoryMarkdown { get; set; } = string.Empty;
        public string StorySummary { get; set; } = string.Empty;
        public bool StoryIsFinished { get; set; } = true;
        public int StoryPartIndex { get; set; }
        public List<string> CanonSourcesUsed { get; set; } = new List<string>();
        public string EmotionalPurpose { get; set; } = string.Empty;

        public VoiceDirective VoiceDirective { get; set; } = VoiceDirective.Empty();
        [JsonIgnore]
        public Dictionary<string, EmotionScore> EmotionScores { get; set; } = new Dictionary<string, EmotionScore>();
        public List<string> DetectedCombos { get; set; } = new List<string>();
        public RelationshipDelta SuggestedStateDeltas { get; set; } = RelationshipDelta.Zero();
        public string ConversationSummary { get; set; } = string.Empty;
        public string ChangesSummary { get; set; } = string.Empty;
        public List<string> ImportantPlayerQuotes { get; set; } = new List<string>();
        public List<string> MemoryNotes { get; set; } = new List<string>();
        public List<string> SecretsUnlocked { get; set; } = new List<string>();
        public List<StarLearnedFunction> LearnedFunctions { get; set; } = new List<StarLearnedFunction>();
        public List<string> RelationshipEvents { get; set; } = new List<string>();
        public List<string> GirlfriendHistoryUpdates { get; set; } = new List<string>();
        public List<string> PetNamesPlayerUsesForStar { get; set; } = new List<string>();
        public List<string> PetNamesStarUsesForPlayer { get; set; } = new List<string>();
        public List<string> LoreUsed { get; set; } = new List<string>();
        public List<string> RiskFlags { get; set; } = new List<string>();
        public double WisdomScore { get; set; }
    }
}
