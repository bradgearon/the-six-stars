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
        public int RelationshipEventCount { get; set; } = 30;
        public int GirlfriendHistoryCount { get; set; } = 20;
        public int LearnedFunctionCount { get; set; } = 25;
        public int SecretStateCount { get; set; } = 30;
        public int RelationshipComboCount { get; set; } = 20;
        public int ScreenCaptureCount { get; set; } = 5;
    }
}
