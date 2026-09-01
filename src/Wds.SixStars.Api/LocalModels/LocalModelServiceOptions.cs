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
            Model = "six-stars-gemma4",
            TimeoutSeconds = 600,
            MaxTokens = 0,
            Temperature = 0.65,
            UseJsonSchemaResponseFormat = false,
            DisableReasoning = true,
            ReasoningBudget = 0,
            ReasoningFormat = "none",
            SendReasoningControlFields = false
        };
        public WhisperStreamEndpointOptions Whisper { get; set; } = new WhisperStreamEndpointOptions
        {
            Provider = "whispercpp",
            EndpointUrl = "http://localhost:8000/inference",
            ModelPath = "models/asr/ggml-large-v3-turbo.bin",
            Language = "en",
            TimeoutSeconds = 60
        };
        public QwenCppHttpTtsOptions Tts { get; set; } = new QwenCppHttpTtsOptions
        {
            BaseUrl = "http://localhost:8101",
            Model = "qwen3-tts-base",
            DefaultLanguage = "English",
            ResponseFormat = "pcm",
            PcmFirstChunkMilliseconds = 480,
            PcmChunkMilliseconds = 960,
            TaskType = "VoiceClone",
            ChunkGenerationMode = "first_then_prefetch",
            MaxParallelChunks = 1
        };
    }
}
