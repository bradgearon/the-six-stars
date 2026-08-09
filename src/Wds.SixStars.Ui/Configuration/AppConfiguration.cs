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
        public MobileBridgeConfig MobileBridge { get; set; } = new MobileBridgeConfig();
        public ConversationArchiveConfig ConversationArchive { get; set; } = new ConversationArchiveConfig();
        public DailyStarStateConfig DailyStarState { get; set; } = new DailyStarStateConfig();
        public WorldEventsConfig WorldEvents { get; set; } = new WorldEventsConfig();
        public StarSpeechIntentConfig StarSpeechIntents { get; set; } = new StarSpeechIntentConfig();
        public PreparedSpeechConfig PreparedSpeech { get; set; } = new PreparedSpeechConfig();
        public ForegroundSpeechStyleConfig ForegroundSpeechStyle { get; set; } = new ForegroundSpeechStyleConfig();
        public WritingPassesConfig WritingPasses { get; set; } = new WritingPassesConfig();
        public XPostingConfig XPosting { get; set; } = new XPostingConfig();
    }


    public sealed class MobileBridgeConfig
    {
        /// <summary>Starts the LAN bridge when the WPF app opens so the Android client can connect.</summary>
        public bool EnabledOnStartup { get; set; } = true;

        /// <summary>Friendly Windows/network name the Android client defaults to. Change to your PC name or use the phone app URL field.</summary>
        public string HostName { get; set; } = "grasshoppah";

        /// <summary>TCP port listened on all local interfaces. Open Windows Firewall for this port for phone access.</summary>
        public int Port { get; set; } = 8066;

        /// <summary>When true, Android requests also play the Star response through the desktop TTS/audio pipeline.</summary>
        public bool SpeakResponsesOnDesktop { get; set; } = false;
    }


    public sealed class ConversationArchiveConfig
    {
        /// <summary>When true, the desktop LAN host also serves the local relationship/conversation review website.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>URL prefix for the static archive website. Defaults to /archive on the same port as the Android bridge.</summary>
        public string PathPrefix { get; set; } = "/archive";

        /// <summary>Default session shown when the archive opens. Use android-phone-prod for the phone companion history.</summary>
        public string DefaultSessionId { get; set; } = "android-phone-prod";

        /// <summary>Maximum markdown trace files returned to the browser in one refresh.</summary>
        public int MaxLogs { get; set; } = 160;
    }

    public sealed class WorldEventsConfig
    {
        public bool Enabled { get; set; } = true;
        public string StateRoot { get; set; } = "state/world-events";
        public int MaximumStoredEvents { get; set; } = 2000;
        public int PromptEventCount { get; set; } = 32;
        public int RefreshDebounceSeconds { get; set; } = 8;
        public string ApiKey { get; set; } = string.Empty;
        public List<WorldEventFeedConfig> Feeds { get; set; } = new List<WorldEventFeedConfig>();
    }

    public sealed class WorldEventFeedConfig
    {
        public string FeedId { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public int PollSeconds { get; set; } = 20;
        public string ApiKey { get; set; } = string.Empty;
    }

    public sealed class StarSpeechIntentConfig
    {
        public bool Enabled { get; set; } = true;
        public string StateRoot { get; set; } = "state/star-speech-intents";
        public int MaximumStoredIntents { get; set; } = 800;
        public double AgingPointsPerHour { get; set; } = 1.5;
        public double MaximumAgeBoost { get; set; } = 18;
        public double HighPriorityPreparationThreshold { get; set; } = 75;
        public double MediumPriorityPreparationThreshold { get; set; } = 55;
        public double HighPriorityAutoSpeakThreshold { get; set; } = 78;
        public double MediumPriorityAutoSpeakThreshold { get; set; } = 55;
        public int MaximumPreparedItemsPerStar { get; set; } = 2;
        public int MaximumPrepareAttempts { get; set; } = 5;
        public int PreparationTimeoutMinutes { get; set; } = 10;
        public int PrepareRetryDelaySeconds { get; set; } = 15;
        public int LeaseMinutes { get; set; } = 5;
        public int SpokenArchiveDays { get; set; } = 30;
        public double MinimumRepeatHours { get; set; } = 12;
    }

    public sealed class PreparedSpeechConfig
    {
        public bool Enabled { get; set; } = true;
        public string StateRoot { get; set; } = "state/prepared-star-speech";
        public int MaximumArtifactsPerStar { get; set; } = 3;
        public int MaximumArtifactAgeHours { get; set; } = 72;
    }

    public sealed class ForegroundSpeechStyleConfig
    {
        public bool Enabled { get; set; } = true;
        public double ConciseModeProbability { get; set; } = 0.50;
        public int ConciseMinimumChunks { get; set; } = 3;
        public int ConciseMaximumChunks { get; set; } = 5;
        public bool RespectExplicitLengthRequests { get; set; } = true;
        public bool SpeakBeforeReplyEnabled { get; set; } = true;
        public double SpeakBeforeReplyProbability { get; set; } = 0.30;
        public double HighPrioritySpeakBeforeReplyProbability { get; set; } = 0.80;
        public int SpeakBeforeReplyMaximumSeconds { get; set; } = 12;
        public int MinimumPageDwellSeconds { get; set; } = 8;
        public int MediumPriorityPageDwellSeconds { get; set; } = 18;
        public double GirlfriendMediumSpeakProbability { get; set; } = 0.55;
        public double HighConnectionMediumSpeakProbability { get; set; } = 0.30;
        public double HighConnectionThreshold { get; set; } = 70;
        public int MinimumMinutesBetweenAutoSpeech { get; set; } = 8;
    }

    public sealed class WritingPassesConfig
    {
        /// <summary>When false, the writing shelf runs only its initial word-pair/composition pass.</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>When true, each writing cycle receives 100 optional random noun/adjective/verb ideas.</summary>
        public bool SeedWordsEnabled { get; set; } = true;
        public int MinimumPasses { get; set; } = 1;
        public int MaximumPasses { get; set; } = 6;
        public double OnePassWeight { get; set; } = 0.50;
        public double TwoToThreePassWeight { get; set; } = 0.30;
        public double FourOrMorePassWeight { get; set; } = 0.20;
    }

    public sealed class XPostingConfig
    {
        /// <summary>Enables the four-times-daily scheduler. Manual Post Now remains available when false.</summary>
        public bool Enabled { get; set; } = false;
        public string StateRoot { get; set; } = "state/x-posting";
        public int CandidateLimit { get; set; } = 500;
        public int DelayBetweenPostsSeconds { get; set; } = 120;
        public int RequestTimeoutSeconds { get; set; } = 45;
        public int PollSeconds { get; set; } = 60;
        public int RetryDelaySeconds { get; set; } = 300;
        public List<XPostingSlotConfig> RefreshSlots { get; set; } = new List<XPostingSlotConfig>
        {
            new XPostingSlotConfig { SlotId = "overnight", StartsAt = "00:00", RefreshBefore = "06:00" },
            new XPostingSlotConfig { SlotId = "morning", StartsAt = "06:00", RefreshBefore = "12:00" },
            new XPostingSlotConfig { SlotId = "afternoon", StartsAt = "12:00", RefreshBefore = "18:00" },
            new XPostingSlotConfig { SlotId = "evening", StartsAt = "18:00", RefreshBefore = "00:00" }
        };
        public List<XPostingStarAccountConfig> StarAccounts { get; set; } = CreateDefaultAccounts();

        private static List<XPostingStarAccountConfig> CreateDefaultAccounts()
        {
            return new List<XPostingStarAccountConfig>
            {
                new XPostingStarAccountConfig { Id = "elusiaa" },
                new XPostingStarAccountConfig { Id = "kaelia" },
                new XPostingStarAccountConfig { Id = "maera" },
                new XPostingStarAccountConfig { Id = "khavia" },
                new XPostingStarAccountConfig { Id = "auerlis" },
                new XPostingStarAccountConfig { Id = "nyxari" }
            };
        }
    }

    public sealed class XPostingSlotConfig
    {
        public string SlotId { get; set; } = string.Empty;
        public string StartsAt { get; set; } = string.Empty;
        public string RefreshBefore { get; set; } = string.Empty;
    }

    public sealed class XPostingStarAccountConfig
    {
        /// <summary>Lower-case Star id used to choose this account.</summary>
        public string Id { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string ApiKey { get; set; } = string.Empty;
        public string ApiSecret { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public string AccessTokenSecret { get; set; } = string.Empty;
    }

    public sealed class DailyStarStateConfig
    {
        public bool Enabled { get; set; } = true;
        public bool GenerateForAllStars { get; set; } = true;
        public bool RefreshSelectedStarOnRequest { get; set; } = true;
        public string StateRoot { get; set; } = "state/daily-star-state";
        public int PollSeconds { get; set; } = 60;
        public int MaxXmlCharsForPrompt { get; set; } = 12000;
        public int MaxJsonCharsForPrompt { get; set; } = 12000;
        public List<string> StarIds { get; set; } = new List<string>();
        public List<DailyStarStateSlotConfig> RefreshSlots { get; set; } = new List<DailyStarStateSlotConfig>
        {
            new DailyStarStateSlotConfig { SlotId = "overnight", StartsAt = "00:00", RefreshBefore = "06:00" },
            new DailyStarStateSlotConfig { SlotId = "morning", StartsAt = "06:00", RefreshBefore = "12:00" },
            new DailyStarStateSlotConfig { SlotId = "afternoon", StartsAt = "12:00", RefreshBefore = "18:00" },
            new DailyStarStateSlotConfig { SlotId = "evening", StartsAt = "18:00", RefreshBefore = "00:00" }
        };
    }

    public sealed class DailyStarStateSlotConfig
    {
        public string SlotId { get; set; } = string.Empty;
        public string StartsAt { get; set; } = string.Empty;
        public string RefreshBefore { get; set; } = string.Empty;
    }

    public sealed class LocalModelsConfig
    {
        /// <summary>Repository-local models folder shared by LLM, faster-whisper, and Qwen TTS.</summary>
        public string Root { get; set; } = "models";
        public string HuggingFaceHome { get; set; } = "models/hf";
        public string HuggingFaceHubCache { get; set; } = "models/hf/hub";

        /// <summary>OpenAI-compatible LLM endpoint. Defaults to LM Studio for speed; the Python Gemma server is optional.</summary>
        public string LlmEndpointUrl { get; set; } = "http://localhost:8080/v1/chat/completions";
        public string LlmModel { get; set; } = "local-model";
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
        public string EndpointUrl { get; set; } = "http://localhost:8080/v1/chat/completions";
        public string Model { get; set; } = "local-model";
        public string? ApiKey { get; set; }
        public bool UseJsonSchemaResponseFormat { get; set; } = false;
        public bool DisableReasoning { get; set; } = true;
        public int ReasoningBudget { get; set; } = 0;
        public string ReasoningFormat { get; set; } = "none";
        public bool SendReasoningControlFields { get; set; } = true;

        /// <summary>
        /// Local models can take a while after the backend reports 100% generation because
        /// the server still has to finish/serialize the JSON response. Keep this high for
        /// large structured Star replies. Override with SIX_STARS_LLM_TIMEOUT_SECONDS.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 600;

        /// <summary>Default chat max_tokens sent to the local backend. Override with SIX_STARS_LLM_MAX_TOKENS. 0 means do not send max_tokens.</summary>
        public int MaxTokens { get; set; } = 0;

        /// <summary>Per-pass max_tokens for the fast spoken pass and background review. Override with SIX_STARS_LLM_SPEECH_MAX_TOKENS / SIX_STARS_LLM_REVIEW_MAX_TOKENS. 0 means not sent.</summary>
        public int SpeechMaxTokens { get; set; } = 0;
        public int ReviewMaxTokens { get; set; } = 0;

        /// <summary>When true, every local LLM request and response is written to JSONL/text files for prompt analysis.</summary>
        public bool LogExchangesToDisk { get; set; } = true;
        public string ExchangeLogDirectory { get; set; } = "logs/llm-exchanges";
        public int ExchangeLogMaxTextChars { get; set; } = 2000000;

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
        public double ActiveFlirtConnectionPercent { get; set; } = 65;
        public int ActiveFlirtRomanceScore { get; set; } = 10;
        public double StrongActiveFlirtConnectionPercent { get; set; } = 85;
        public int StrongActiveFlirtRomanceScore { get; set; } = 25;
    }

    public sealed class ScreenLlmConfig
    {
        public bool Enabled { get; set; } = true;
        public string EndpointUrl { get; set; } = "http://localhost:8080/v1/chat/completions";
        public string Model { get; set; } = "local-model";
        public string? ApiKey { get; set; }
        public bool UseJsonSchemaResponseFormat { get; set; } = false;
        public bool DisableReasoning { get; set; } = true;
        public int ReasoningBudget { get; set; } = 0;
        public string ReasoningFormat { get; set; } = "none";
        public bool SendReasoningControlFields { get; set; } = true;
        public int MaxTokens { get; set; } = 0;
        public int TimeoutSeconds { get; set; } = 120;
        public bool LogExchangesToDisk { get; set; } = true;
        public string ExchangeLogDirectory { get; set; } = "logs/llm-exchanges";
        public int ExchangeLogMaxTextChars { get; set; } = 2000000;
        public string PayloadShape { get; set; } = "OpenAiImageUrl";
    }
}
