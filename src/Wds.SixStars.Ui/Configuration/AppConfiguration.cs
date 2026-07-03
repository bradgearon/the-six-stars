using System.Collections.Generic;

namespace Wds.SixStars.Ui.Configuration
{
    public sealed class AppConfiguration
    {
        public LocalModelsConfig LocalModels { get; set; } = new LocalModelsConfig();
        public OpenWakeWordConfig OpenWakeWord { get; set; } = new OpenWakeWordConfig();
        public VoiceInputConfig VoiceInput { get; set; } = new VoiceInputConfig();
        public LlmConfig LLM { get; set; } = new LlmConfig();
        public WhisperConfig Whisper { get; set; } = new WhisperConfig();
        public TtsConfig TTS { get; set; } = new TtsConfig();
        public WebSocketTtsConfig WebSocketTTS { get; set; } = new WebSocketTtsConfig();
        public VoiceOutputConfig VoiceOutput { get; set; } = new VoiceOutputConfig();
        public CompanionConfig Companion { get; set; } = new CompanionConfig();
        public MemoryConfig Memory { get; set; } = new MemoryConfig();
        public ScreenLlmConfig ScreenLLM { get; set; } = new ScreenLlmConfig();
    }


    public sealed class LocalModelsConfig
    {
        /// <summary>Repository-local models folder shared by LLM, faster-whisper, and Qwen TTS.</summary>
        public string Root { get; set; } = "models";
        public string HuggingFaceHome { get; set; } = "models/hf";
        public string HuggingFaceHubCache { get; set; } = "models/hf/hub";

        /// <summary>Local OpenAI-compatible LLM endpoint launched by scripts/local_llm_openai_server.py.</summary>
        public string LlmEndpointUrl { get; set; } = "http://localhost:8200/v1/chat/completions";
        public string LlmModel { get; set; } = "google/gemma-4-E2B-it";
        public string LlmLocalPath { get; set; } = string.Empty;

        public string WhisperEndpointUrl { get; set; } = "http://localhost:8000/v1/audio/transcriptions";
        public string WhisperModel { get; set; } = "tiny.en";
        public string WhisperLocalPath { get; set; } = string.Empty;

        public string QwenTtsWebSocketUrl { get; set; } = "ws://localhost:8100/tts/stream";
        public string QwenTtsModel { get; set; } = "Qwen/Qwen3-TTS-12Hz-0.6B-Base";
        public string QwenTtsLocalPath { get; set; } = string.Empty;
    }

    public sealed class OpenWakeWordConfig
    {
        public string Provider { get; set; } = "openWakeWord";
        public string EndpointUrl { get; set; } = "http://localhost:9000/detect";
        public string? ApiKey { get; set; }
        public int TimeoutSeconds { get; set; } = 10;
        public string PayloadShape { get; set; } = "MultipartWav";
        public List<string> Models { get; set; } = new List<string>();
        public double Threshold { get; set; } = 0.5;
        public Dictionary<string, string> CustomWakeWords { get; set; } = new Dictionary<string, string>();
    }

    public sealed class VoiceInputConfig
    {
        /// <summary>When true, Start Listen is automatically invoked when the WPF harness opens.</summary>
        public bool AlwaysListeningOnStartup { get; set; } = false;

        /// <summary>When false, every amplitude-VAD utterance is transcribed. When true, Whisper still runs but text must include a Star wake alias.</summary>
        public bool RequireWakeWord { get; set; } = false;

        /// <summary>How long RMS speech must remain above threshold before an utterance starts.</summary>
        public int MinSoundMs { get; set; } = 120;

        /// <summary>How much silence closes the utterance and submits it to Whisper.</summary>
        public int SilenceEndMs { get; set; } = 850;

        public int PreRollMs { get; set; } = 450;
        public int MaxSegmentMs { get; set; } = 30000;
        public double RmsSpeechThreshold { get; set; } = 0.018;
        public bool MuteInputWhileStarSpeaking { get; set; } = true;
    }

    public sealed class LlmConfig
    {
        public string EndpointUrl { get; set; } = "http://localhost:8200/v1/chat/completions";
        public string Model { get; set; } = "google/gemma-4-E2B-it";
        public string? ApiKey { get; set; }
        public bool UseJsonSchemaResponseFormat { get; set; } = true;

        /// <summary>
        /// Local models can take a while after the backend reports 100% generation because
        /// the server still has to finish/serialize the JSON response. Keep this high for
        /// large structured Star replies. Override with SIX_STARS_LLM_TIMEOUT_SECONDS.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 600;

        /// <summary>0 means do not send max_tokens; let the local backend/model limit apply. Override with SIX_STARS_LLM_MAX_TOKENS.</summary>
        public int MaxTokens { get; set; } = 0;

        /// <summary>0 means do not send per-pass max_tokens. The schema/prompt controls output size instead.</summary>
        public int SpeechMaxTokens { get; set; } = 0;
        public int ReviewMaxTokens { get; set; } = 0;

        public double Temperature { get; set; } = 0.65;
    }

    public sealed class WhisperConfig
    {
        public string Provider { get; set; } = "faster-whisper";
        public string EndpointUrl { get; set; } = "http://localhost:8000/v1/audio/transcriptions";
        public string ModelPath { get; set; } = string.Empty;
        public string? ApiKey { get; set; }
        public int TimeoutSeconds { get; set; } = 60;
        public string Language { get; set; } = "en";
    }

    public sealed class TtsConfig
    {
        public string EndpointUrl { get; set; } = string.Empty;
        public string Model { get; set; } = "Qwen/Qwen3-TTS-12Hz-0.6B-Base";
        public string? ApiKey { get; set; }
        public string PayloadShape { get; set; } = "OpenAiSpeech";
        public string TaskType { get; set; } = "VoiceClone";
        public string DefaultLanguage { get; set; } = "English";
    }

    public sealed class WebSocketTtsConfig
    {
        /// <summary>
        /// Streaming TTS is the default desktop voice path. Set Enabled=false or SIX_STARS_USE_STREAMING_TTS=false
        /// to fall back to the normal HTTP /v1/audio/speech endpoint and NAudio WAV playback.
        /// </summary>
        public bool Enabled { get; set; } = true;
        public string WebSocketUrl { get; set; } = "ws://localhost:8100/tts/stream";
        public string Model { get; set; } = "Qwen/Qwen3-TTS-12Hz-0.6B-Base";
        public string DefaultLanguage { get; set; } = "English";
        public string ResponseFormat { get; set; } = "wav";
        public string TaskType { get; set; } = "VoiceClone";
        public int MaxWordsPerChunk { get; set; } = 30;
        public int MaxSentencesPerChunk { get; set; } = 2;
        public string ChunkGenerationMode { get; set; } = "first_then_prefetch";
        public int MaxParallelChunks { get; set; } = 1;
    }

    public sealed class VoiceOutputConfig
    {
        /// <summary>
        /// Master switch for writing generated Star voice WAVs to disk. Set false to play
        /// generated audio without archiving every spoken line. Override with
        /// SIX_STARS_SAVE_GENERATED_WAVS or legacy SIX_STARS_SAVE_VOICE_WAVS.
        /// </summary>
        public bool SaveGeneratedWavs { get; set; } = false;

        /// <summary>
        /// Default directory is the process working directory, usually the folder you launched dotnet run from.
        /// Override with GeneratedWavDirectory or SIX_STARS_GENERATED_WAV_DIR.
        /// </summary>
        public string GeneratedWavDirectory { get; set; } = string.Empty;

        /// <summary>
        /// Desktop playback backend. DirectSound is the default requested by the desktop harness for
        /// forgiving game-style playback. WASAPI and WaveOut remain selectable fallbacks.
        /// Valid values: wasapi, waveout, directsound, auto. Override with SIX_STARS_AUDIO_BACKEND.
        /// </summary>
        public string AudioBackend { get; set; } = "directsound";

        public bool SaveMetadataText { get; set; } = true;
    }


    public sealed class CompanionConfig
    {
        /// <summary>
        /// Default Star selected by the UI, speech loop, and conversation API wrapper. Override
        /// with SIX_STARS_DEFAULT_STAR_ID when testing a specific companion.
        /// </summary>
        public string DefaultStarId { get; set; } = "Elusiaa";
    }

    public sealed class MemoryConfig
    {
        public int RecentHistoryForPrompt { get; set; } = 10;
        public int MaxMemoryLogEntries { get; set; } = 160;
        public int MaxImportantQuotes { get; set; } = 80;
        public int MaxMemoryNotes { get; set; } = 160;
        public int ImportantQuotesForPrompt { get; set; } = 24;
        public int MemoryNotesForPrompt { get; set; } = 40;
        public int RelationshipEventsForPrompt { get; set; } = 30;
        public int GirlfriendHistoryForPrompt { get; set; } = 20;
        public int LearnedFunctionsForPrompt { get; set; } = 25;
        public int SecretStatesForPrompt { get; set; } = 30;
        public int RelationshipCombosForPrompt { get; set; } = 20;
        public int ScreenCapturesForPrompt { get; set; } = 5;
    }

    public sealed class ScreenLlmConfig
    {
        public string EndpointUrl { get; set; } = "http://localhost:8200/v1/chat/completions";
        public string Model { get; set; } = "google/gemma-4-E2B-it";
        public string? ApiKey { get; set; }
        public bool UseJsonSchemaResponseFormat { get; set; } = true;
    }
}
