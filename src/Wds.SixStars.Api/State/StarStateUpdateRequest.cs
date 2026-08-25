using System;
using System.Collections.Generic;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.State
{
    public sealed class StarStateUpdateRequest
    {
        public string SessionId { get; set; } = "local-dev";
        public string StarId { get; set; } = string.Empty;
        public string RequestType { get; set; } = string.Empty;
        public DateTimeOffset TurnStartedAt { get; set; }
        public string PlayerText { get; set; } = string.Empty;
        public string StarResponse { get; set; } = string.Empty;
        public string ConversationSummary { get; set; } = string.Empty;
        public string ChangesSummary { get; set; } = string.Empty;
        public string TenTurnConversationSummary { get; set; } = string.Empty;
        public string HundredTurnConversationSummary { get; set; } = string.Empty;
        public RelationshipDelta ActualDelta { get; set; } = RelationshipDelta.Zero();
        public RelationshipDelta SuggestedDelta { get; set; } = RelationshipDelta.Zero();
        public string EmotionalEffectVector { get; set; } = string.Empty;
        public string NegativeEmotionVector { get; set; } = string.Empty;
        public string DeterministicEmotionSummary { get; set; } = string.Empty;
        public List<string> AcceptedPositiveSignals { get; set; } = new List<string>();
        public List<string> AcceptedNegativeSignals { get; set; } = new List<string>();
        public List<string> AcceptedCombos { get; set; } = new List<string>();
        public Dictionary<string, EmotionScore> EmotionScores { get; set; } = new Dictionary<string, EmotionScore>();
        public List<string> ImportantPlayerQuotes { get; set; } = new List<string>();
        public List<string> MemoryNotes { get; set; } = new List<string>();
        public List<string> ShortTermMemoryItems { get; set; } = new List<string>();
        public bool SextingDetected { get; set; }
        public List<string> ThingsLearnedOrSuggestedByPlayer { get; set; } = new List<string>();
        public string IncomingUnresolvedVector { get; set; } = string.Empty;
        public string OutgoingUnresolvedVector { get; set; } = string.Empty;
        public string UnresolvedResolutionVector { get; set; } = string.Empty;
        public List<StarRememberedMarker> RememberedMarkers { get; set; } = new List<StarRememberedMarker>();
        public List<string> ReferencedMemoryMarkerIds { get; set; } = new List<string>();
        public List<string> SecretsUnlocked { get; set; } = new List<string>();
        public List<string> DetectedCombos { get; set; } = new List<string>();
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
        public double WisdomScore { get; set; }
        public string StoryId { get; set; } = string.Empty;
        public string StoryTitle { get; set; } = string.Empty;
        public string StorySummary { get; set; } = string.Empty;
        public bool StoryFinished { get; set; }
        public string StorySeed { get; set; } = string.Empty;

        /// <summary>
        /// True for hidden background/system updates such as interstar micro-ripples. These may update
        /// relationship state, but they should not look like direct player conversation turns.
        /// </summary>
        public bool IsInternalBackgroundUpdate { get; set; }

        /// <summary>When true, do not replace the prompt-facing ConversationSummary with this update.</summary>
        public bool SuppressConversationSummaryUpdate { get; set; }

        /// <summary>When true, do not append this update to the direct player-facing MemoryLog.</summary>
        public bool SuppressConversationLog { get; set; }
    }
}
