using System;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>Settings for the local qwentts.cpp HTTP server.</summary>
    public sealed class QwenCppHttpTtsOptions
    {
        public string BaseUrl { get; set; } = "http://localhost:8101";
        public string Model { get; set; } = "qwen3-tts-base";
        public string DefaultLanguage { get; set; } = "English";
        public string ResponseFormat { get; set; } = "pcm";
        public int PcmPacketMilliseconds { get; set; } = 240;
        public string TaskType { get; set; } = "VoiceClone";
        public int MaxWordsPerChunk { get; set; } = 30;
        public int MaxSentencesPerChunk { get; set; } = 2;
        public int TimeoutSeconds { get; set; } = 180;

        public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Max(5, TimeoutSeconds));
    }
}
