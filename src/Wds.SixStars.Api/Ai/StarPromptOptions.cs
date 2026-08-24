namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Controls how much durable Star memory is injected into each LLM prompt.
    /// Keep defaults compact enough for local models, but large enough for continuity.
    /// </summary>
    public sealed class StarPromptOptions
    {
        /// <summary>
        /// Number of recent player/star turns included every prompt. Default: last 10.
        /// </summary>
        public int RecentHistoryCount { get; set; } = 10;

        public int ImportantQuoteCount { get; set; } = 24;
        public int MemoryNoteCount { get; set; } = 40;

        /// <summary>Only short-term memory newer than this many milliseconds is injected into prompts.</summary>
        public int ShortTermMemoryWindowMs { get; set; } = 1800000;
        public int RelationshipEventCount { get; set; } = 30;
        public int GirlfriendHistoryCount { get; set; } = 20;
        public int LearnedFunctionCount { get; set; } = 25;
        public int SecretStateCount { get; set; } = 30;
        public int RelationshipComboCount { get; set; } = 20;
        public int ScreenCaptureCount { get; set; } = 5;

        /// <summary>Compact host-supplied description of the active Star roster for story/world prompts. Empty means no roster summary is injected.</summary>
        public string ConfiguredStarSummary { get; set; } = string.Empty;

        /// <summary>When connection reaches this percent, foreground prompts encourage occasional active flirting even outside committed girlfriend mode.</summary>
        public double ActiveFlirtConnectionPercent { get; set; } = 65;

        /// <summary>When Romance reaches this score, foreground prompts encourage occasional active flirting even outside committed girlfriend mode.</summary>
        public int ActiveFlirtRomanceScore { get; set; } = 10;

        /// <summary>When connection reaches this percent, foreground prompts encourage stronger active flirting and magic/domain acting-out.</summary>
        public double StrongActiveFlirtConnectionPercent { get; set; } = 85;

        /// <summary>When Romance reaches this score, foreground prompts encourage stronger active flirting and magic/domain acting-out.</summary>
        public int StrongActiveFlirtRomanceScore { get; set; } = 25;
    }
}
