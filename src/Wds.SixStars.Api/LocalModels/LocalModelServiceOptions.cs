using Wds.SixStars.Api.Ai;
using Wds.SixStars.Api.Speech;
using Wds.SixStars.Api.Voice;

namespace Wds.SixStars.Api.LocalModels
{
    /// <summary>
    /// One reusable service description for the local AI stack. The WPF harness and Unity can both
    /// consume this instead of each feature inventing its own endpoint/model path rules.
    /// </summary>
    public sealed class LocalModelServiceOptions
    {
        public string ModelsRoot { get; set; } = "models";
        public string HuggingFaceHome { get; set; } = "models/hf";
        public string HuggingFaceHubCache { get; set; } = "models/hf/hub";
        public LocalChatModelOptions Chat { get; set; } = new LocalChatModelOptions
        {
            EndpointUrl = "http://localhost:8080/v1/chat/completions",
            Model = "local-model",
            TimeoutSeconds = 600,
            MaxTokens = 0,
            Temperature = 0.65,
            UseJsonSchemaResponseFormat = false,
            DisableReasoning = true,
            ReasoningBudget = 0,
            ReasoningFormat = "none",
            SendReasoningControlFields = true
        };
        public WhisperStreamEndpointOptions Whisper { get; set; } = new WhisperStreamEndpointOptions
        {
            Provider = "faster-whisper",
            EndpointUrl = "http://localhost:8000/v1/audio/transcriptions",
            ModelPath = "models/asr/faster-whisper-tiny.en",
            Language = "en",
            TimeoutSeconds = 60
        };
        public WebSocketTtsOptions Tts { get; set; } = new WebSocketTtsOptions
        {
            WebSocketUrl = "ws://localhost:8100/tts/stream",
            Model = "Qwen/Qwen3-TTS-12Hz-0.6B-Base",
            DefaultLanguage = "English",
            ResponseFormat = "wav",
            TaskType = "VoiceClone",
            ChunkGenerationMode = "first_then_prefetch",
            MaxParallelChunks = 1
        };
    }
}
