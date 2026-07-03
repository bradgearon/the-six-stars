using System;

namespace Wds.SixStars.Api.Voice
{
    public sealed class Qwen3TtsOptions
    {
        public string EndpointUrl { get; set; } = "http://localhost:8091/v1/audio/speech";
        public string Model { get; set; } = "Qwen/Qwen3-TTS-12Hz-1.7B-Base";
        public string? ApiKey { get; set; }
        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>
        /// OpenAiSpeech sends { model, input, voice, instructions, response_format }.
        /// QwenSimple sends { inputs, voice, instructions, language }.
        /// </summary>
        public string PayloadShape { get; set; } = "OpenAiSpeech";

        public string DefaultLanguage { get; set; } = "English";
        public string ResponseFormat { get; set; } = "wav";
        public string? TaskType { get; set; } = "VoiceClone";
        public bool IncludeVoiceDesignInInstructions { get; set; } = true;

        public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Max(5, TimeoutSeconds));
    }
}
