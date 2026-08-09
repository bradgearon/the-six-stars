namespace Wds.SixStars.Api.SpeechIntents
{
    public sealed class ForegroundSpeechStyleOptions
    {
        public bool Enabled { get; set; } = true;
        public double ConciseModeProbability { get; set; } = 0.50;
        public int ConciseMinimumChunks { get; set; } = 3;
        public int ConciseMaximumChunks { get; set; } = 5;
        public bool RespectExplicitLengthRequests { get; set; } = true;
        public bool SpeakBeforeReplyEnabled { get; set; } = true;
        public double SpeakBeforeReplyProbability { get; set; } = 0.30;
        public double HighPrioritySpeakBeforeReplyProbability { get; set; } = 0.80;
        public int SpeakBeforeReplyMaximumSeconds { get; set; } = 12;
        public int MinimumPageDwellSeconds { get; set; } = 8;
        public int MediumPriorityPageDwellSeconds { get; set; } = 18;
        public double GirlfriendMediumSpeakProbability { get; set; } = 0.55;
        public double HighConnectionMediumSpeakProbability { get; set; } = 0.30;
        public double HighConnectionThreshold { get; set; } = 70;
        public int MinimumMinutesBetweenAutoSpeech { get; set; } = 8;
    }
}
