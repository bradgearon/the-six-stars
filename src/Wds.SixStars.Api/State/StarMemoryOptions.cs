namespace Wds.SixStars.Api.State
{
    public sealed class StarMemoryOptions
    {
        public bool AcceptModelSuggestedDeltas { get; set; }
        public bool AutoCaptureHighImpactPlayerQuotes { get; set; } = true;
        public int MaxMemoryLogEntries { get; set; } = 160;
        public int MaxImportantQuotes { get; set; } = 80;
        public int MaxMemoryNotes { get; set; } = 160;
        public int MaxRelationshipEvents { get; set; } = 100;
        public int MaxGirlfriendHistoryEntries { get; set; } = 80;
        public int MaxLearnedFunctions { get; set; } = 80;
        public int MaxSecrets { get; set; } = 120;
        public int MaxRecentHistoryForPrompt { get; set; } = 10;
    }
}
