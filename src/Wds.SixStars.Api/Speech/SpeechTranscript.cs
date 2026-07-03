using System;

namespace Wds.SixStars.Api.Speech
{
    public sealed class SpeechTranscript
    {
        public string SegmentId { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Language { get; set; } = "en";
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset EndedAt { get; set; }
        public double AudioDurationMs { get; set; }
        public string Source { get; set; } = "whisper";
        public string RawJson { get; set; } = string.Empty;

        /// <summary>
        /// True when the lightweight audio hotword model accepted the utterance before Whisper was called.
        /// </summary>
        public bool WakeWordDetected { get; set; }

        /// <summary>
        /// Star selected by the hotword model or by active conversation context.
        /// </summary>
        public string SuggestedStarId { get; set; } = string.Empty;

        public string ListenMode { get; set; } = string.Empty;
        public double HotwordConfidence { get; set; }
    }
}
