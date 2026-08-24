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
        public string EmotionalEffectVector { get; set; } = string.Empty;
        public string NegativeEmotionVector { get; set; } = string.Empty;
        public bool NeedsMoreInformation { get; set; }
        public List<InformationNeedVectorItem> InformationNeeds { get; set; } = new List<InformationNeedVectorItem>();
        public List<string> ThingsLearnedOrSuggestedByPlayer { get; set; } = new List<string>();

        /// <summary>Temporary current-context items such as what the Last Cat is doing now, the shared task, or an immediate plan. Host timestamps and expires these.</summary>
        public List<string> ShortTermMemoryItems { get; set; } = new List<string>();

        /// <summary>Compact questions/topics raised by the Last Cat that remain open for the Star.</summary>
        [JsonPropertyName("questionsFromLastCatVector")]
        public string QuestionsFromLastCatVector { get; set; } = string.Empty;

        /// <summary>Compact questions/open thoughts the Star intentionally leaves with the Last Cat.</summary>
        [JsonPropertyName("questionsToLastCatVector")]
        public string QuestionsToLastCatVector { get; set; } = string.Empty;

        /// <summary>Compact question-resolution events detected this turn.</summary>
        [JsonPropertyName("questionResolutionVector")]
        public string QuestionResolutionVector { get; set; } = string.Empty;

        /// <summary>Optional high-emotion one-shot animation selected by the private background review.</summary>
        [JsonPropertyName("emotionAnimation")]
        public string EmotionAnimation { get; set; } = string.Empty;

        /// <summary>Compact missing-reference requests: source^subject^score, separated by ~.</summary>
        [JsonPropertyName("informationNeedsVector")]
        public string InformationNeedsVector { get; set; } = string.Empty;

        public VoiceDirective VoiceDirective { get; set; } = VoiceDirective.Empty();
        [JsonIgnore]
        public Dictionary<string, EmotionScore> EmotionScores { get; set; } = new Dictionary<string, EmotionScore>();
        public List<string> DetectedCombos { get; set; } = new List<string>();
        public RelationshipDelta SuggestedStateDeltas { get; set; } = RelationshipDelta.Zero();
        public string ConversationSummary { get; set; } = string.Empty;
        public string ChangesSummary { get; set; } = string.Empty;
        public string TenTurnConversationSummary { get; set; } = string.Empty;
        public string HundredTurnConversationSummary { get; set; } = string.Empty;
        /// <summary>Interstar target reaction only: how this Star feels toward the source Star after hearing the relay from her.</summary>
        public string SourceStarEmotionalEffectVector { get; set; } = string.Empty;
        public string SourceStarNegativeEmotionVector { get; set; } = string.Empty;

        /// <summary>Interstar mode only: one or more per-Star vectors for Star-to-Star relationship updates.</summary>
        public List<InterstarEmotionVectorResult> InterstarEmotionVectors { get; set; } = new List<InterstarEmotionVectorResult>();

        /// <summary>Interstar mode only: what this Star would say back to the source Star after hearing the relay.</summary>
        public string ReplyToSourceStar { get; set; } = string.Empty;
        public List<string> ImportantPlayerQuotes { get; set; } = new List<string>();
        public List<string> MemoryNotes { get; set; } = new List<string>();
        public List<StarRememberedMarker> RememberedMarkers { get; set; } = new List<StarRememberedMarker>();
        public List<string> ReferencedMemoryMarkerIds { get; set; } = new List<string>();
        public List<string> SecretsUnlocked { get; set; } = new List<string>();
        public List<StarLearnedFunction> LearnedFunctions { get; set; } = new List<StarLearnedFunction>();
        public List<string> RelationshipEvents { get; set; } = new List<string>();
        public List<string> GirlfriendHistoryUpdates { get; set; } = new List<string>();
        public List<string> PetNamesPlayerUsesForStar { get; set; } = new List<string>();
        public List<string> PetNamesStarUsesForPlayer { get; set; } = new List<string>();
        public string GirlfriendNeedsMetSummary { get; set; } = string.Empty;
        public List<GirlfriendVectorItem> GirlfriendNeedsMetVector { get; set; } = new List<GirlfriendVectorItem>();
        public List<GirlfriendVectorItem> GirlfriendWantsMetVector { get; set; } = new List<GirlfriendVectorItem>();
        public string GirlfriendCoupleActivitySummary { get; set; } = string.Empty;
        public string GirlfriendCoupleActivityVector { get; set; } = string.Empty;
        [JsonPropertyName("requestToShowBoobs")]
        public bool RequestToShowBoobs { get; set; }
        public List<string> LoreUsed { get; set; } = new List<string>();
        public List<string> RiskFlags { get; set; } = new List<string>();
        public double WisdomScore { get; set; }
    }
}
