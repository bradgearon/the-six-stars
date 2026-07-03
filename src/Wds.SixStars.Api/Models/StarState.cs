using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Serializable relationship/memory state for one Star. Keep this simple/public-property based
    /// so it works cleanly as a Unity save-game chunk and as desktop JSON.
    /// </summary>
    public sealed class StarState
    {
        public string StarId { get; set; } = string.Empty;
        public int Heart { get; set; }
        public int Trust { get; set; }
        public int Romance { get; set; }
        public int BenaveilInfluence { get; set; }
        public int InteractionCount { get; set; }
        public DateTimeOffset? LastInteractionAt { get; set; }
        public string CurrentConversationId { get; set; } = string.Empty;
        public string ConversationSummary { get; set; } = string.Empty;
        public List<string> ImportantPlayerQuotes { get; set; } = new List<string>();
        public List<string> MemoryNotes { get; set; } = new List<string>();
        public List<string> SecretsUnlocked { get; set; } = new List<string>();
        public List<StarSecretState> SecretStates { get; set; } = new List<StarSecretState>();
        public List<StarLearnedFunction> LearnedFunctions { get; set; } = new List<StarLearnedFunction>();
        public List<RelationshipComboState> RelationshipCombos { get; set; } = new List<RelationshipComboState>();
        public List<string> RelationshipEvents { get; set; } = new List<string>();

        public bool IsGirlfriend { get; set; }
        public DateTimeOffset? GirlfriendSince { get; set; }
        public List<string> GirlfriendHistory { get; set; } = new List<string>();
        public List<string> PetNamesPlayerUsesForStar { get; set; } = new List<string>();
        public List<string> PetNamesStarUsesForPlayer { get; set; } = new List<string>();

        public int PositiveSuccessionCount { get; set; }
        public int RomanticSuccessionCount { get; set; }
        public int HurtSuccessionCount { get; set; }
        public DateTimeOffset? LastPositiveInteractionAt { get; set; }
        public DateTimeOffset? LastRomanticInteractionAt { get; set; }
        public DateTimeOffset? LastHurtInteractionAt { get; set; }

        public List<ConversationLogEntry> MemoryLog { get; set; } = new List<ConversationLogEntry>();

        /// <summary>
        /// Summaries of long stories this Star has already told, used to avoid repeating stories.
        /// </summary>
        public List<StarStoryHistoryEntry> StoryHistory { get; set; } = new List<StarStoryHistoryEntry>();
    }
}
