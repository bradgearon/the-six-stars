using System.Collections.Generic;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>Hosted Alibaba Cloud Model Studio Qwen3-TTS voice-cloning options.</summary>
    public sealed class AlibabaQwenTtsOptions
    {
        public string ApiKey { get; set; } = string.Empty;
        public string EndpointUrl { get; set; } = "https://dashscope-intl.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";
        public string VoiceEnrollmentEndpointUrl { get; set; } = "https://dashscope-intl.aliyuncs.com/api/v1/services/audio/tts/customization";
        public string Model { get; set; } = "qwen3-tts-vc-2026-01-22";
        public string VoiceEnrollmentModel { get; set; } = "qwen-voice-enrollment";
        public string DefaultLanguage { get; set; } = "English";
        public bool AutoCloneVoices { get; set; } = true;
        public string VoiceCachePath { get; set; } = "state/hosted-tts-voices.json";
        public int TimeoutSeconds { get; set; } = 120;
        public Dictionary<string, string> VoiceIds { get; set; } = new Dictionary<string, string>();
    }
}
