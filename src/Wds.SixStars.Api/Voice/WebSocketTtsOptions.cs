using System;

namespace Wds.SixStars.Api.Voice
{
    public sealed class WebSocketTtsOptions
    {
        public string WebSocketUrl { get; set; } = "ws://localhost:8100/tts/stream";
        public string? Model { get; set; } = "Qwen/Qwen3-TTS-12Hz-0.6B-Base";
        public string? DefaultLanguage { get; set; } = "Auto";
        public string? ResponseFormat { get; set; } = "wav";
        public string? TaskType { get; set; } = "VoiceClone";
        public int MaxWordsPerChunk { get; set; } = 90;
        public int MaxSentencesPerChunk { get; set; } = 4;
        public string ChunkGenerationMode { get; set; } = "first_then_prefetch";
        public int MaxParallelChunks { get; set; } = 1;
        public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);
        public int MaxReconnectAttempts { get; set; } = 3;
    }
}
