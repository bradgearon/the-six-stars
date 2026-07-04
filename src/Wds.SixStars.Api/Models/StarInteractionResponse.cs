using System.Collections.Generic;
using System.Linq;

namespace Wds.SixStars.Api.Models
{
    public sealed class StarInteractionResponse
    {
        public string StarId { get; set; } = string.Empty;
        private string _spokenText = string.Empty;

        /// <summary>
        /// Ordered TTS-ready speech chunks. The LLM is instructed to produce this as spokenText: [..]
        /// with one or two natural sentences per part, so the TTS server does not need to guess splits.
        /// </summary>
        public List<string> SpokenParts { get; set; } = new List<string>();

        /// <summary>
        /// Backward-compatible joined text for UI display, memory logs, and older integrations.
        /// Prefer SpokenParts for TTS / Unity audio chunk generation.
        /// </summary>
        public string SpokenText
        {
            get
            {
                if (SpokenParts != null && SpokenParts.Count > 0)
                {
                    return string.Join(" ", SpokenParts.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
                }

                return _spokenText ?? string.Empty;
            }
            set
            {
                _spokenText = value ?? string.Empty;
                SpokenParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(_spokenText)) SpokenParts.Add(_spokenText.Trim());
            }
        }

        public void SetSpokenParts(IEnumerable<string>? parts)
        {
            SpokenParts = (parts ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();
            _spokenText = string.Join(" ", SpokenParts);
        }
        public string? StoryTitle { get; set; }
        public StoryKind StoryKind { get; set; } = StoryKind.None;

        /// <summary>
        /// For long story mode, the first foreground reply may include the whole planned story in markdown.
        /// Hosts can keep this and auto-play later chunks locally without asking the LLM for continuation.
        /// </summary>
        public string StoryId { get; set; } = string.Empty;
        public string StoryMarkdown { get; set; } = string.Empty;
        public string StorySummary { get; set; } = string.Empty;
        public bool StoryIsFinished { get; set; } = true;
        public int StoryPartIndex { get; set; }
        public List<string> CanonSourcesUsed { get; set; } = new List<string>();
        public string EmotionalPurpose { get; set; } = string.Empty;

        /// <summary>
        /// Compact emotion hint returned by the fast foreground LLM pass for experimentation.
        /// Format: confidence:score;confidence:score;... in the canonical StarEmotionMatrix.StandardEmotionIds order.
        /// This tells the host how the player's latest statement made the Star feel without requesting the heavy
        /// emotionScores object before voice generation.
        /// </summary>
        public string EmotionalEffectVector { get; set; } = string.Empty;

        /// <summary>Seven-entry compact negative vector: Irritated, Disrespected, Pressured, CreepedOut, Bored, Hurt, Unsafe.</summary>
        public string NegativeEmotionVector { get; set; } = string.Empty;

        public string DeterministicEmotionUpdateSummary { get; set; } = string.Empty;
        public double EmotionalConnectionPercentBefore { get; set; }
        public double EmotionalConnectionPercentAfter { get; set; }
        public bool IsGirlfriendReadyAfter { get; set; }
        public string GirlfriendReadinessSummary { get; set; } = string.Empty;
        public List<string> AcceptedPositiveEmotionSignals { get; set; } = new List<string>();
        public List<string> AcceptedNegativeEmotionSignals { get; set; } = new List<string>();
        public List<string> AcceptedRelationshipCombos { get; set; } = new List<string>();

        public VoiceDirective VoiceDirective { get; set; } = VoiceDirective.Empty();
        public Dictionary<string, EmotionScore> EmotionScores { get; set; } = new Dictionary<string, EmotionScore>();
        public List<string> DetectedCombos { get; set; } = new List<string>();

        /// <summary>
        /// Model-recommended deltas. The runtime may use deterministic rules to produce StateDeltas,
        /// but the suggestion is retained for debugging and design tuning.
        /// </summary>
        public RelationshipDelta SuggestedStateDeltas { get; set; } = RelationshipDelta.Zero();
        public RelationshipDelta StateDeltas { get; set; } = RelationshipDelta.Zero();

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

        /// <summary>
        /// Runtime coordination id for the deferred background emotion/memory review.
        /// Voice systems should call SixStarsRuntime.StartBackgroundAnalysisAfterVoiceReady
        /// with this id after TTS generation has completed so speech generation gets priority.
        /// </summary>
        public string BackgroundAnalysisId { get; set; } = string.Empty;

        public List<UnityEventEnvelope> UnityEvents { get; set; } = new List<UnityEventEnvelope>();
        public DebugInfo Debug { get; set; } = new DebugInfo();
    }
}
