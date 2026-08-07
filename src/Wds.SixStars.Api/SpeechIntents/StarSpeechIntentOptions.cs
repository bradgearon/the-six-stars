namespace Wds.SixStars.Api.SpeechIntents
{
    public sealed class StarSpeechIntentOptions
    {
        public bool Enabled { get; set; } = true;
        public string StateRoot { get; set; } = "state/star-speech-intents";
        public int MaximumStoredIntents { get; set; } = 800;
        public double AgingPointsPerHour { get; set; } = 1.5;
        public double MaximumAgeBoost { get; set; } = 18;
        public double HighPriorityPreparationThreshold { get; set; } = 75;
        public double MediumPriorityPreparationThreshold { get; set; } = 55;
        public double HighPriorityAutoSpeakThreshold { get; set; } = 78;
        public double MediumPriorityAutoSpeakThreshold { get; set; } = 55;
        public int MaximumPreparedItemsPerStar { get; set; } = 2;
        public int MaximumPrepareAttempts { get; set; } = 5;
        public int PreparationTimeoutMinutes { get; set; } = 10;
        public int PrepareRetryDelaySeconds { get; set; } = 15;
        public int LeaseMinutes { get; set; } = 5;
        public int SpokenArchiveDays { get; set; } = 30;
        public double MinimumRepeatHours { get; set; } = 12;
    }
}
