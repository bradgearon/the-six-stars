using System.Collections.Generic;
using Wds.SixStars.Api.Stories;
using Wds.SixStars.Api.Writings;
using Wds.SixStars.Api.Actors;

namespace Wds.SixStars.Api.Stars
{
    public sealed class StarProfile
    {
        /// <summary>Whether this Star is part of the active runtime/Forge roster.</summary>
        public bool Active { get; set; } = true;
        /// <summary>Stable configurable roster/display order; lower values appear first.</summary>
        public int SortOrder { get; set; } = 1000;
        /// <summary>Generic actor identity. Stars use actorType=star and normally actorId=StarId.</summary>
        public string ActorType { get; set; } = "star";
        public string ActorId { get; set; } = string.Empty;
        /// <summary>Capability/depth declaration shared with generic souled actors.</summary>
        public ActorSoulConfig Soul { get; set; } = ActorSoulConfig.FullStarSoul();

        public string StarId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Element { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string CoreQuote { get; set; } = string.Empty;
        public string VoiceDirection { get; set; } = string.Empty;
        public string VoiceArchetype { get; set; } = string.Empty;
        public string SpeakingStyle { get; set; } = string.Empty;
        public string PersonalityShort { get; set; } = string.Empty;
        public List<string> NameAliases { get; set; } = new List<string>();
        public string VoiceId { get; set; } = string.Empty;
        public string VoiceDesignPrompt { get; set; } = string.Empty;
        public string VoiceCloneAudioFile { get; set; } = string.Empty;
        public string VoiceCloneRefText { get; set; } = string.Empty;
        public List<string> VoiceStyleRules { get; set; } = new List<string>();
        public List<string> VoiceGenerationRules { get; set; } = new List<string>();
        public Dictionary<string, string> EmotionVoiceHints { get; set; } = new Dictionary<string, string>();
        public string DeepPersonality { get; set; } = string.Empty;
        public string BackstorySummary { get; set; } = string.Empty;
        public string CompleteBackgroundStory { get; set; } = string.Empty;

        /// <summary>Per-Star action/battle appetite used by fast speech and autonomous story writing.</summary>
        public string ActionBattlePreference { get; set; } = string.Empty;

        /// <summary>Version of the compact character attribute schema used by CharacterAttributes.</summary>
        public int CharacterAttributesVersion { get; set; } = StarCharacterAttributeSchema.Version;

        /// <summary>Compact 0-100 character attribute scores in StarCharacterAttributeSchema.AttributeOrder.</summary>
        public string CharacterAttributes { get; set; } = string.Empty;

        /// <summary>Plain-language trait hints used by prompts and tuning tools.</summary>
        public List<string> CharacterAttributeNotes { get; set; } = new List<string>();

        /// <summary>Private attraction hook for the prompt. The Star knows this internally but should not confess it cheaply.</summary>
        public string SecretAttractionHook { get; set; } = string.Empty;

        /// <summary>How intense/private her romantic or sensual interest can become when connection is earned. Keep non-explicit and connection-based.</summary>
        public string RomanticSensualDepth { get; set; } = string.Empty;

        /// <summary>Per-Star flirting instructions loaded from the Star JSON profile.</summary>
        public List<string> FlirtStyle { get; set; } = new List<string>();

        /// <summary>Per-Star signature behaviors applied only while girlfriend cute mode is active.</summary>
        public List<string> CuteModeInstructions { get; set; } = new List<string>();

        /// <summary>Boundaries that keep romance earned, in-character, and safe.</summary>
        public List<string> RomanceBoundaryRules { get; set; } = new List<string>();

        public List<string> FriendsAndContacts { get; set; } = new List<string>();
        public List<string> StarSecrets { get; set; } = new List<string>();
        public List<string> SkillSecrets { get; set; } = new List<string>();
        public List<string> LibraryCanonNotes { get; set; } = new List<string>();
        public List<string> Quotes { get; set; } = new List<string>();
        public List<string> Behaviors { get; set; } = new List<string>();
        public List<string> SpokenResponseRules { get; set; } = new List<string>();
        public List<string> StorytellingRules { get; set; } = new List<string>();
        public List<string> StoryThinkingPhrases { get; set; } = new List<string>();
        public List<string> GirlfriendProposalThinkingPhrases { get; set; } = new List<string>();
        public List<KnownPerson> PeopleKnown { get; set; } = new List<KnownPerson>();
        public List<StarLoreEntry> AngelStories { get; set; } = new List<StarLoreEntry>();
        public List<StarEmotionDefinition> Emotions { get; set; } = new List<StarEmotionDefinition>();

        /// <summary>Deterministic Heart/Trust/Romance scoring thresholds for this Star.</summary>
        public StarRelationshipStatsConfig RelationshipStats { get; set; } = new StarRelationshipStatsConfig();

        /// <summary>Per-emotion deterministic relationship influence map for this Star.</summary>
        public List<StarEmotionInfluenceRule> EmotionInfluence { get; set; } = new List<StarEmotionInfluenceRule>();

        /// <summary>Per-Star deterministic combo rules that scan recent stored emotion vectors.</summary>
        public List<StarEmotionComboRule> ComboRules { get; set; } = new List<StarEmotionComboRule>();

        /// <summary>Rules controlling which other Stars may receive delayed emotional ripple updates from this Star.</summary>
        public List<StarInterstarInfluenceRule> InterstarRules { get; set; } = new List<StarInterstarInfluenceRule>();

        /// <summary>Shared quality consensus required before any writing persistence route can save a candidate.</summary>
        public StarWritingPersistenceQualityGate WritingPersistenceQualityGate { get; set; } = new StarWritingPersistenceQualityGate();

        /// <summary>Per-Star value rules deciding which generated shelf Sparks become durable.</summary>
        public List<StarWritingPersistenceRule> WritingPersistenceRules { get; set; } = new List<StarWritingPersistenceRule>();

        /// <summary>Reader-emotion impact consensus required before a Constellation Story can persist.</summary>
        public StarWritingPersistenceQualityGate ConstellationPersistenceQualityGate { get; set; } = new StarWritingPersistenceQualityGate();

        /// <summary>Per-Star reader-emotion IU routes deciding which completed Constellation Stories become durable.</summary>
        public List<StarWritingPersistenceRule> ConstellationPersistenceRules { get; set; } = new List<StarWritingPersistenceRule>();

        public List<StoryMemoryCard> StoryDeck { get; set; } = new List<StoryMemoryCard>();
        public List<string> SextingModeInstructions { get; set; }
    }
}
