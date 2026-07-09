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

        /// <summary>At-rest initiative only: true when the Star has a state-backed thing she wants to say now. False means no speech should be displayed or played.</summary>
        public bool InitiativeShouldSpeak { get; set; } = true;

        /// <summary>At-rest initiative only: 0-1 priority returned by the LLM for the selected state-backed topic.</summary>
        public double InitiativePriority { get; set; }

        /// <summary>At-rest initiative only: one score/confidence item for the single speak-now option.</summary>
        public List<GirlfriendVectorItem> InitiativeSpeechDecisionVector { get; set; } = new List<GirlfriendVectorItem>();

        /// <summary>At-rest initiative only: randomized runtime threshold used to accept or suppress the LLM speak-now option.</summary>
        public double InitiativeSpeechAcceptThreshold { get; set; }

        /// <summary>At-rest initiative only: effective runtime score after confidence and girlfriend-state bias are applied.</summary>
        public double InitiativeSpeechEffectiveScore { get; set; }

        /// <summary>At-rest initiative only: whether the randomized score/confidence gate accepted the line after the LLM returned it.</summary>
        public bool InitiativeSpeechScoreAccepted { get; set; }

        /// <summary>At-rest initiative only: short state-backed topic or hook the Star chose.</summary>
        public string InitiativeTopic { get; set; } = string.Empty;

        /// <summary>At-rest initiative only: why she did or did not choose to speak.</summary>
        public string InitiativeReason { get; set; } = string.Empty;

        /// <summary>True when the runtime skipped the LLM because daily/emotion/interstar state did not contain enough reason to ask.</summary>
        public bool InitiativeSkippedBeforeLlm { get; set; }

        /// <summary>Debug score from the deterministic state pre-check that decides whether asking the initiative LLM is worthwhile.</summary>
        public double InitiativeStateScore { get; set; }

        /// <summary>True when the foreground Star says she needs old history, quotes, markers, lore, bestiary, character, or world data.</summary>
        public bool NeedsMoreInformation { get; set; }
        public List<InformationNeedVectorItem> InformationNeeds { get; set; } = new List<InformationNeedVectorItem>();
        public bool InformationFollowupAttempted { get; set; }
        public bool InformationFollowupSucceeded { get; set; }
        public string InformationFollowupContextSummary { get; set; } = string.Empty;

        /// <summary>Full optional foreground information-needs vector returned by the Star before deterministic selection.</summary>
        public InformationNeedSelectionResult InformationNeedSelection { get; set; } = new InformationNeedSelectionResult();
        public List<InformationNeedAvailabilityItem> InformationNeedAvailability { get; set; } = new List<InformationNeedAvailabilityItem>();

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

        /// <summary>Top three strongest entries from the latest emotion vectors after deterministic background scoring.</summary>
        public List<TopAffectedEmotion> TopAffectedEmotions { get; set; } = new List<TopAffectedEmotion>();

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
        /// <summary>Interstar target reaction only: how this Star feels toward the source Star who told her the relay.</summary>
        public string SourceStarEmotionalEffectVector { get; set; } = string.Empty;
        public string SourceStarNegativeEmotionVector { get; set; } = string.Empty;

        /// <summary>Interstar mode only: one or more per-Star vectors used to update durable Star-to-Star Heart/Trust state.</summary>
        public List<InterstarEmotionVectorResult> InterstarEmotionVectors { get; set; } = new List<InterstarEmotionVectorResult>();

        /// <summary>Interstar mode only: deterministic deltas applied to Star-to-Star relationships.</summary>
        public List<InterstarRelationshipDeltaResult> InterstarRelationshipDeltas { get; set; } = new List<InterstarRelationshipDeltaResult>();

        /// <summary>Interstar mode only: one sentence this Star would say back to the source Star.</summary>
        public string InterstarReplyToSourceStar { get; set; } = string.Empty;
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
        public List<GirlfriendVectorItem> GirlfriendCoupleActivityItems { get; set; } = new List<GirlfriendVectorItem>();

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
