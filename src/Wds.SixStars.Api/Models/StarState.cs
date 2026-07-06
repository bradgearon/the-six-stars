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

        /// <summary>True once the game/story has introduced this Star to the player. Elusiaa defaults true; other Stars default false until met in-game.</summary>
        public bool IsMet { get; set; }

        /// <summary>True once the game/story has made this Star an active ally. Ally status normally implies met. Elusiaa defaults true; other Stars default false until unlocked by story/gameplay.</summary>
        public bool IsAlly { get; set; }

        /// <summary>Deterministic gameplay modifiers currently changing relationship state updates over time.</summary>
        public List<StarRelationshipEffector> ActiveEffectors { get; set; } = new List<StarRelationshipEffector>();

        /// <summary>Authored game dialogue lines or beats this Star has finished, newest first.</summary>
        public List<StarDialogueMemoryEntry> GameDialogueHistory { get; set; } = new List<StarDialogueMemoryEntry>();

        /// <summary>Current authored dialogue/script progress if Unity wants the Star to know exactly where she is in written dialogue.</summary>
        public StarDialogueProgressState CurrentDialogueState { get; set; } = new StarDialogueProgressState();

        /// <summary>How strict the deterministic romance/connection gate should be for this Star.</summary>
        public string EmotionalConnectionSkillLevel { get; set; } = "interested_private_potential_partner";

        /// <summary>0-100 deeper emotional connection meter. This rises only after ally unlock plus repeated personality-matched intimate/charismatic/high-safety signals.</summary>
        public double EmotionalConnectionPercent { get; set; }

        /// <summary>Last deterministic percentage-point change applied to EmotionalConnectionPercent.</summary>
        public double LastEmotionalConnectionPercentDelta { get; set; }

        /// <summary>True when ally status, connection percent, and relationship point gates indicate she is ready to become the player's girlfriend if the story allows it.</summary>
        public bool IsGirlfriendReady { get; set; }

        public DateTimeOffset? GirlfriendReadySince { get; set; }
        public string GirlfriendReadinessSummary { get; set; } = string.Empty;

        public string LastEmotionalEffectVector { get; set; } = string.Empty;
        public string LastNegativeEmotionVector { get; set; } = string.Empty;
        public string LastDeterministicEmotionSummary { get; set; } = string.Empty;

        /// <summary>Top three strongest emotion vector entries from the last settled background update.</summary>
        public List<TopAffectedEmotion> LastTopAffectedEmotions { get; set; } = new List<TopAffectedEmotion>();

        public int InteractionCount { get; set; }
        public DateTimeOffset? LastInteractionAt { get; set; }
        public string CurrentConversationId { get; set; } = string.Empty;
        public string ConversationSummary { get; set; } = string.Empty;

        /// <summary>Small distilled prompt-facing quote list. Keep this bounded so the ongoing conversation context never grows huge.</summary>
        public List<string> ImportantPlayerQuotes { get; set; } = new List<string>();

        /// <summary>Durable source archive of important player quotes for nostalgia / old-memory retrieval. The prompt only receives selected slices.</summary>
        public List<StarImportantQuoteRecord> AllImportantPlayerQuotes { get; set; } = new List<StarImportantQuoteRecord>();

        public List<string> MemoryNotes { get; set; } = new List<string>();

        /// <summary>Exact things the player explicitly asked this Star to remember: coordinates, location markers, descriptions, and named points of interest.</summary>
        public List<StarRememberedMarker> RememberedMarkers { get; set; } = new List<StarRememberedMarker>();

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

        /// <summary>Recent active conversation turns only. Older turns are moved to OldConversationArchives after the configured age window.</summary>
        public List<ConversationLogEntry> MemoryLog { get; set; } = new List<ConversationLogEntry>();

        /// <summary>Summarized archived conversation blocks with source entries retained for explicit old-memory retrieval.</summary>
        public List<StarConversationArchiveEntry> OldConversationArchives { get; set; } = new List<StarConversationArchiveEntry>();

        /// <summary>Recent deterministic emotion updates used to prevent repeated cheap scoring and to detect longer-term combos.</summary>
        public List<EmotionUpdateHistoryEntry> EmotionUpdateHistory { get; set; } = new List<EmotionUpdateHistoryEntry>();

        /// <summary>High-confidence positive emotion totals earned through deterministic emotion scoring.</summary>
        public List<StarEmotionAccumulatedScore> PositiveEmotionTotals { get; set; } = new List<StarEmotionAccumulatedScore>();

        /// <summary>High-confidence bad/boundary emotion totals earned through deterministic emotion scoring.</summary>
        public List<StarEmotionAccumulatedScore> NegativeEmotionTotals { get; set; } = new List<StarEmotionAccumulatedScore>();

        /// <summary>
        /// Summaries of long stories this Star has already told, used to avoid repeating stories.
        /// </summary>
        public List<StarStoryHistoryEntry> StoryHistory { get; set; } = new List<StarStoryHistoryEntry>();
    }
}
