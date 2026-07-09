namespace Wds.SixStars.Api.State
{
    public sealed class StarMemoryOptions
    {
        public bool AcceptModelSuggestedDeltas { get; set; }
        public bool AutoCaptureHighImpactPlayerQuotes { get; set; } = true;
        public double AutoImportantQuoteMinEmotionConfidence { get; set; } = 0.15;
        public double AutoImportantQuoteMinEmotionScore { get; set; } = 30;
        public double AutoImportantQuoteMinWeightedEmotionScore { get; set; } = 4.5;
        public int AutoImportantQuoteMaxLength { get; set; } = 700;
        public int MaxMemoryLogEntries { get; set; } = 80;
        public int MaxImportantQuotes { get; set; } = 128;
        public int MaxMemoryNotes { get; set; } = 160;
        public int MaxRelationshipEvents { get; set; } = 100;
        public int MaxGirlfriendHistoryEntries { get; set; } = 80;
        public int MaxLearnedFunctions { get; set; } = 80;
        public int MaxSecrets { get; set; } = 120;
        public int MaxRememberedMarkers { get; set; } = 2048;
        public int MaxRecentHistoryForPrompt { get; set; } = 10;
        public int MaxGameDialogueHistory { get; set; } = 240;
        public int MaxAllImportantQuoteRecords { get; set; } = 4096;
        public int ArchiveConversationAfterMinutes { get; set; } = 120;
        public int MaxOldConversationArchives { get; set; } = 80;
        public int MaxArchivedTurnsPerArchive { get; set; } = 240;
    }
}
