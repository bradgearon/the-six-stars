namespace Wds.SixStars.Api.Speech
{
    public sealed class RecognizedSpeech
    {
        public SpeechTranscript Transcript { get; set; } = new SpeechTranscript();
        public string TargetStarId { get; set; } = string.Empty;
        public bool WakeWordDetected { get; set; }
        public bool WasIgnored { get; set; }
        public string IgnoreReason { get; set; } = string.Empty;
    }
}
