using System;

namespace Wds.SixStars.Api.Speech
{
    public sealed class SpeechInputLevelEventArgs : EventArgs
    {
        public SpeechInputLevelEventArgs(
            double rms,
            double threshold,
            bool isSpeech,
            bool isListening,
            bool isSpeechActive,
            bool isStarSpeaking,
            SpeechListenMode listenMode,
            string source,
            double chunkDurationMs,
            DateTimeOffset capturedAt)
        {
            Rms = rms;
            Threshold = threshold;
            IsSpeech = isSpeech;
            IsListening = isListening;
            IsSpeechActive = isSpeechActive;
            IsStarSpeaking = isStarSpeaking;
            ListenMode = listenMode;
            Source = source ?? string.Empty;
            ChunkDurationMs = chunkDurationMs;
            CapturedAt = capturedAt;
        }

        public double Rms { get; }
        public double Threshold { get; }
        public bool IsSpeech { get; }
        public bool IsListening { get; }
        public bool IsSpeechActive { get; }
        public bool IsStarSpeaking { get; }
        public SpeechListenMode ListenMode { get; }
        public string Source { get; }
        public double ChunkDurationMs { get; }
        public DateTimeOffset CapturedAt { get; }

        public double LevelPercent
        {
            get
            {
                double percent = Rms <= 0 ? 0 : Rms * 1000.0;
                if (percent < 0) return 0;
                if (percent > 100) return 100;
                return percent;
            }
        }
    }
}
