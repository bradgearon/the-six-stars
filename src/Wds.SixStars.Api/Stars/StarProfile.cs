using System.Collections.Generic;
using Wds.SixStars.Api.Stories;

namespace Wds.SixStars.Api.Stars
{
    public sealed class StarProfile
    {
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
        public string SpiritualFather { get; set; } = string.Empty;
        public string SpiritualFatherForm { get; set; } = string.Empty;
        public string CompleteBackgroundStory { get; set; } = string.Empty;

        /// <summary>Private attraction hook for the prompt. The Star knows this internally but should not confess it cheaply.</summary>
        public string SecretAttractionHook { get; set; } = string.Empty;

        /// <summary>How intense/private her romantic or sensual interest can become when connection is earned. Keep non-explicit and consent-based.</summary>
        public string RomanticSensualDepth { get; set; } = string.Empty;

        /// <summary>How her flirting should sound as connection grows.</summary>
        public string FlirtStyle { get; set; } = string.Empty;

        /// <summary>Boundaries that keep romance earned, in-character, and safe.</summary>
        public List<string> RomanceBoundaryRules { get; set; } = new List<string>();

        public List<string> FriendsAndContacts { get; set; } = new List<string>();
        public List<string> BenaveilFormInteractions { get; set; } = new List<string>();
        public List<string> StarSecrets { get; set; } = new List<string>();
        public List<string> SkillSecrets { get; set; } = new List<string>();
        public List<string> LibraryCanonNotes { get; set; } = new List<string>();
        public List<string> Quotes { get; set; } = new List<string>();
        public List<string> Behaviors { get; set; } = new List<string>();
        public List<string> SpokenResponseRules { get; set; } = new List<string>();
        public List<string> StorytellingRules { get; set; } = new List<string>();
        public List<string> StoryThinkingPhrases { get; set; } = new List<string>();
        public List<KnownPerson> PeopleKnown { get; set; } = new List<KnownPerson>();
        public List<StarLoreEntry> AngelStories { get; set; } = new List<StarLoreEntry>();
        public List<StarEmotionDefinition> Emotions { get; set; } = new List<StarEmotionDefinition>();
        public List<StoryMemoryCard> StoryDeck { get; set; } = new List<StoryMemoryCard>();
    }
}
