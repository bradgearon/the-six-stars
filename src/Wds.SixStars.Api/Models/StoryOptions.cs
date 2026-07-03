namespace Wds.SixStars.Api.Models
{
    public sealed class StoryOptions
    {
        public string Length { get; set; } = "medium";
        public string Tone { get; set; } = "comforting";
        public string CanonStrictness { get; set; } = "high";
        public bool UseUnlockedSecrets { get; set; } = true;
        public bool AllowPoetry { get; set; } = true;
        public bool AskFollowupAfter { get; set; }

        /// <summary>
        /// Target spoken length for a new automatically generated serial story.
        /// Runtime fills this with a random value from 2 to 45 minutes when the player asks for a story.
        /// </summary>
        public int TargetMinutes { get; set; }

        /// <summary>
        /// Target number of one-or-two-sentence markdown chunks to plan for the story.
        /// The host speaks chunk 0 from the LLM response, then auto-plays the remaining cached markdown chunks locally without asking the LLM again.
        /// </summary>
        public int TargetChunkCount { get; set; }

        /// <summary>
        /// Maximum number of sentences allowed per story speech chunk. Keep at 2 for TTS.
        /// </summary>
        public int MaxSentencesPerChunk { get; set; } = 2;

        /// <summary>
        /// Optional player-authored seed for story mode. The UI uses this when the player clicks
        /// Tell Me A Story and wants to suggest what the story should be about.
        /// </summary>
        public string PlayerSuggestion { get; set; } = string.Empty;

        /// <summary>
        /// Legacy compatibility flag. New desktop story autoplay does not use LLM continuation calls;
        /// it reads the next chunk directly from ActiveStoryMarkdown unless a custom host still uses this field.
        /// </summary>
        public bool ContinueActiveStory { get; set; }

        public string ActiveStoryId { get; set; } = string.Empty;
        public string ActiveStoryTitle { get; set; } = string.Empty;
        public string ActiveStoryMarkdown { get; set; } = string.Empty;
        public string ActiveStorySummary { get; set; } = string.Empty;
        public string StoryProgressSoFar { get; set; } = string.Empty;
        public string LastStorySpokenText { get; set; } = string.Empty;

        /// <summary>
        /// Legacy compatibility field for hosts that still ask an LLM to continue. The desktop host now
        /// narrates the next cached markdown chunk locally instead of sending it back to the LLM.
        /// </summary>
        public string NextStoryChunkMarkdown { get; set; } = string.Empty;

        public int NextStoryPartIndex { get; set; }

        /// <summary>
        /// Summary/title list of stories already told by this Star so she can always pick a new one.
        /// </summary>
        public System.Collections.Generic.List<string> StoriesAlreadyTold { get; set; } = new System.Collections.Generic.List<string>();
    }
}
