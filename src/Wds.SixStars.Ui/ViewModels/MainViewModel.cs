using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Wds.SixStars.Api.Agents;
using Wds.SixStars.Api.Ai;
using Wds.SixStars.Api.DailyState;
using Wds.SixStars.Api.Audio;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Mobile;
using Wds.SixStars.Api.LocalModels;
using Wds.SixStars.Api.Runtime;
using Wds.SixStars.Api.Screen;
using Wds.SixStars.Api.Speech;
using Wds.SixStars.Api.Stars;
using Wds.SixStars.Api.Stories;
using Wds.SixStars.Api.State;
using Wds.SixStars.Api.Voice;
using Wds.SixStars.Ui.Audio;
using Wds.SixStars.Ui.Configuration;
using Wds.SixStars.Ui.Infrastructure;
using Wds.SixStars.Ui.Screen;

namespace Wds.SixStars.Ui.ViewModels;

public sealed partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private const string DefaultSessionId = "desktop-local-dev";

    private readonly SixStarsRuntime _runtime;
    private readonly InMemoryScreenContextStore _screenContextStore;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly VoiceCaptureSettings _voiceCaptureSettings;
    private readonly WhisperSpeechInputPipeline _speechPipeline;
    private readonly SpeechToStarCoordinator _speechCoordinator;
    private readonly SpeechToStarOptions _speechOptions;
    private readonly StarVoiceResponsePipeline? _voiceResponsePipeline;
    private readonly StreamingStarVoiceResponsePipeline? _streamingVoiceResponsePipeline;
    private readonly IWavePlaybackEndpoint _playbackEndpoint;
    private readonly IStarVoiceGenerator? _storyAutoplayPrefetchVoiceGenerator;
    private readonly IStreamingVoiceGenerator? _storyAutoplayPrefetchStreamingGenerator;
    private readonly NaudioMicrophoneSpeechSource _microphoneSpeechSource;
    private readonly ScreenAwarenessLoop _screenAwarenessLoop;
    private readonly StateChangeTracker _stateChangeTracker;
    private readonly AppConfiguration _appConfig;
    private readonly SpeechInterruptionManager _interruptionManager;
    private readonly SixStarsRuntimeOperationController _operationController;
    private readonly MobileBridgeDispatcher _mobileBridgeDispatcher = new MobileBridgeDispatcher();
    private readonly StarStorySpeechAdapter _storySpeechAdapter = new StarStorySpeechAdapter();
    private readonly bool _useStreamingTts;
    private readonly StringBuilder _llmDebugLog = new StringBuilder();

    private string _selectedStarId = "Elusiaa";
    private RequestType _selectedRequestType = RequestType.Chat;
    private string _playerText = string.Empty;
    private string _storySuggestion = string.Empty;
    private string _area = "VirethornCaves";
    private string _quest = "DesktopDevHarness";
    private bool _romanceMode = true;
    private string _starOutput = string.Empty;
    private string _debugJson = string.Empty;
    private string _status = "Ready.";
    private string _topAffectedEmotionsText = "Top emotions: none yet.";
    private string _lastEmotionVectorsText = "Last emotional vectors: none yet.";
    private string _interstarStatusText = "Interstar state: idle.";
    private string _todayStarStateText = "Today: no daily Star state yet.";
    private string _speechStatus = "Voice input stopped. Click Start Listen to enable amplitude VAD and real Whisper transcription.";
    private string _voiceInputStatus = "Amplitude VAD idle. No audio captured yet.";
    private string _microphoneCaptureStatus = "Microphone not started.";
    private string _incomingVoiceDebug = "RMS 0.0000 / threshold pending";
    private string _lastVoiceSegmentDetails = "No completed voice segment yet.";
    private double _incomingVoiceLevelPercent;
    private string _lastTranscript = string.Empty;
    private bool _speechRequireWakeWord = false;
    private string _screenStatus = "Screen awareness stopped. It will capture and summarize the desktop every 5 minutes when started.";
    private double _screenCaptureIntervalMinutes = 5;
    private string _selectedAudioBackend = "directsound";
    private string _recordButtonText = "Start Recording (Win+Ctrl+E)";
    private string _cancelStopButtonText = "Cancel";
    private bool _isRecording;
    private bool _isProcessingRuntime;
    private bool _voiceConversationLoopActive;
    private bool _savedRequireWakeWordBeforeManualRecording = true;
    private int _savedSilenceEndMs = 900;
    private bool _activeStoryAutoContinue;
    private string _activeStoryStarId = string.Empty;
    private string _activeStoryId = string.Empty;
    private string _activeStoryTitle = string.Empty;
    private string _activeStoryMarkdown = string.Empty;
    private string _activeStorySummary = string.Empty;
    private StoryKind _activeStoryKind = StoryKind.None;
    private VoiceDirective _activeStoryVoiceDirective = VoiceDirective.Empty();
    private readonly StringBuilder _activeStoryProgress = new StringBuilder();
    private string _lastStorySpokenText = string.Empty;
    private int _nextStoryPartIndex;
    private bool _suppressStoryContinuationOnce;
    private string _pendingBackgroundAnalysisId = string.Empty;
    private bool _selectedStarIsMet = true;
    private bool _selectedStarIsAlly = true;
    private bool _isLoadingSelectedStarState;
    private bool _suppressMobileBridgeSelectedStarBroadcast;

    public MainViewModel()
    {
        _stateChangeTracker = new StateChangeTracker();
        _appConfig = LoadConfiguration();
        ConfigureLocalModelEnvironment();
        _selectedStarId = ResolveDefaultStarId(Environment.GetEnvironmentVariable("SIX_STARS_DEFAULT_STAR_ID") ?? _appConfig.Companion.DefaultStarId);
        _playerText = string.Empty;
        _storySuggestion = string.Empty;
        _operationController = new SixStarsRuntimeOperationController();
        _selectedAudioBackend = NormalizeAudioBackend(
            Environment.GetEnvironmentVariable("SIX_STARS_AUDIO_BACKEND") ?? _appConfig.VoiceOutput.AudioBackend);
        Environment.SetEnvironmentVariable("SIX_STARS_AUDIO_BACKEND", _selectedAudioBackend);

        // Initialize speech interruption manager
        _interruptionManager = new SpeechInterruptionManager(new SpeechInterruptionOptions
        {
            MinInterruptionDurationMs = 650,  // User must speak clearly for 650ms to interrupt
            InterruptionDelayMs = 350,        // Avoid accidental cancellation from room noise
            EnableInterruption = true,
            CancelGenerationOnInterruption = false, // Do not cancel Qwen/LLM generation just because the mic hears noise
            CancelPlaybackOnInterruption = true
        });

        IStarProfileProvider profiles = FileStarProfileProvider.TryCreateDefaultOrFallback();
        _screenContextStore = new InMemoryScreenContextStore(5);
        int recentHistoryForPrompt = GetIntSetting(
            Environment.GetEnvironmentVariable("SIX_STARS_RECENT_HISTORY_FOR_PROMPT"),
            _appConfig.Memory.RecentHistoryForPrompt);

        JsonStarStateStore stateStore = JsonStarStateStore.CreateDefaultLocalDevStore();
        System.Diagnostics.Debug.WriteLine("[Memory] Local state file: " + stateStore.FilePath);

        _runtime = new SixStarsRuntime(
            profiles,
            CreateStarAgent(),
            new RelationshipDeltaCalculator(),
            stateStore,
            _screenContextStore,
            recentScreenCount: GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_CAPTURES_FOR_PROMPT"), _appConfig.Memory.ScreenCapturesForPrompt),
            recentHistoryCount: recentHistoryForPrompt,
            maxMemoryLogEntries: GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_MAX_MEMORY_LOG_ENTRIES"), _appConfig.Memory.MaxMemoryLogEntries),
            maxImportantQuotes: GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_MAX_IMPORTANT_QUOTES"), _appConfig.Memory.MaxImportantQuotes),
            maxMemoryNotes: GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_MAX_MEMORY_NOTES"), _appConfig.Memory.MaxMemoryNotes),
            dailyStateOptions: CreateDailyStarStateOptions());

        System.Diagnostics.Debug.WriteLine($"[Memory] RecentHistoryForPrompt: {recentHistoryForPrompt}");
        _mobileBridgeDispatcher.RegisterCommandHandler(HandleMobileBridgeCommandAsync);
        _mobileBridgeDispatcher.EventPublished += (_, evt) => OnUiThread(() => AppendMobileBridgeVoiceDebug("Mobile bridge event", evt.EventType + " #" + evt.Sequence + " " + evt.StarId + " selected=" + evt.SelectedStarId + " met=" + evt.IsMet + " ally=" + evt.IsAlly));
        _ = RefreshSelectedStarStateForUiAsync("startup");

        _speechRequireWakeWord = GetBooleanSetting(
            Environment.GetEnvironmentVariable("SIX_STARS_REQUIRE_WAKE_WORD"),
            _appConfig.VoiceInput.RequireWakeWord);
        _voiceCaptureSettings = CreateVoiceCaptureSettings();
        _speechPipeline = new WhisperSpeechInputPipeline(
            CreateWhisperStreamer(),
            new NoAudioHotwordDetector(),
            new RmsSpeechActivityDetector(),
            _voiceCaptureSettings,
            new WhisperTranscriptionOptions
            {
                Language = Environment.GetEnvironmentVariable("SIX_STARS_WHISPER_LANGUAGE") ?? _appConfig.Whisper.Language
            });

        // Determine if we should use streaming TTS. WebSocket streaming is the default desktop voice path
        // so spoken replies begin playing chunk-by-chunk. The old HTTP /v1/audio/speech path is
        // intentionally disabled in this harness; non-streaming falls back to a mock generator.
        string? streamingOverride = Environment.GetEnvironmentVariable("SIX_STARS_USE_STREAMING_TTS");
        _useStreamingTts = !string.IsNullOrWhiteSpace(streamingOverride)
            ? IsTruthy(streamingOverride)
            : _appConfig.WebSocketTTS.Enabled;
        System.Diagnostics.Debug.WriteLine($"[TTS] Using streaming WebSocket TTS: {_useStreamingTts}");

        IWavePlaybackEndpoint playbackEndpoint = CreatePlaybackEndpoint();
        _playbackEndpoint = playbackEndpoint;

        if (_useStreamingTts)
        {
            _streamingVoiceResponsePipeline = new StreamingStarVoiceResponsePipeline(
                CreateStreamingVoiceGenerator(),
                playbackEndpoint,
                _speechPipeline,
                _interruptionManager);

            // Dedicated generator for story autoplay prefetch. It must be separate from
            // the active streaming pipeline so it can generate the next story chunk while
            // the current one is still playing.
            _storyAutoplayPrefetchStreamingGenerator = CreateStreamingVoiceGenerator();
        }
        else
        {
            _voiceResponsePipeline = new StarVoiceResponsePipeline(
                CreateVoiceGenerator(),
                playbackEndpoint,
                _speechPipeline,
                _interruptionManager);

            // Dedicated non-streaming generator for cached story autoplay prefetch.
            _storyAutoplayPrefetchVoiceGenerator = CreateVoiceGenerator();
        }

        _operationController.RegisterCancelAction("streaming-tts", () => _streamingVoiceResponsePipeline?.CancelCurrentSpeech());
        _operationController.RegisterCancelAction("story-prefetch-tts", () => _storyAutoplayPrefetchStreamingGenerator?.CancelGeneration());
        _operationController.RegisterCancelAction("runtime-background-analysis", () => _runtime.CancelBackgroundAnalysis("Runtime cancel requested."));
        _operationController.RegisterCancelAction("speech-input", () => _ = CancelListeningWithoutSubmittingAsync());
        _operationController.RuntimeOperationCancelled += (_, args) => OnUiThread(() =>
        {
            IsRecording = false;
            IsProcessingRuntime = false;
            _voiceConversationLoopActive = false;
            _pendingBackgroundAnalysisId = string.Empty;
            RecordButtonText = "Start Recording (Win+Ctrl+E)";
            CancelStopButtonText = "Cancel";
            Status = "Runtime operations cancelled: " + args.Reason;
            SpeechStatus = "Cancelled input/LLM/TTS runtime operation.";
        });

        _runtime.BackgroundAnalysisCompleted += (_, response) => OnUiThread(() =>
        {
            _stateChangeTracker.RecordStateChange(
                response.StarId,
                response.StateDeltas.Heart,
                response.StateDeltas.Trust,
                response.StateDeltas.Romance,
                response.StateDeltas.BenaveilInfluence,
                response.StateDeltas.EmotionalConnectionPercent);
            TopAffectedEmotionsText = FormatTopAffectedEmotions(response.TopAffectedEmotions);
            LastEmotionVectorsText = FormatEmotionVectorsForResponse(response);
            Status = "Background emotion/memory update settled for " + response.StarId + ": Heart Δ " + response.StateDeltas.Heart + ", Trust Δ " + response.StateDeltas.Trust + ", Romance Δ " + response.StateDeltas.Romance + ", Connection Δ " + response.StateDeltas.EmotionalConnectionPercent.ToString("0.00") + " -> " + response.EmotionalConnectionPercentAfter.ToString("0.0") + "%.";
            PublishMobileBridgeTopEmotionUpdate(response);
            DebugJson = _llmDebugLog.ToString()
                + "\n================ BACKGROUND DETERMINISTIC EMOTION UPDATE ================\n"
                + CreateEnhancedDebugOutput(new { backgroundUpdate = true, starId = response.StarId }, response);
        });
        _runtime.BackgroundAnalysisFailed += (_, ex) => OnUiThread(() =>
        {
            Status = "Background emotion/memory update failed: " + ex.Message;
        });
        _runtime.InterstarUpdateStatusChanged += (_, args) => OnUiThread(() =>
        {
            InterstarStatusText = "Interstar state: " + args.Phase + " — " + args.Message;
            if (!string.Equals(args.Phase, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                Status = args.Message;
            }
            PublishMobileBridgeInterstarStatus(args);
        });
        _runtime.InterstarMicroUpdateCompleted += (_, response) => OnUiThread(() =>
        {
            InterstarStatusText = "Interstar state: micro-update settled for " + response.StarId + " — " + FormatTopAffectedEmotions(response.TopAffectedEmotions);
            PublishMobileBridgeTopEmotionUpdate(response, "InterstarEmotionUpdateSettled");
            DebugJson = _llmDebugLog.ToString()
                + "\n================ INTERSTAR MICRO UPDATE ================\n"
                + CreateEnhancedDebugOutput(new { interstarMicroUpdate = true, starId = response.StarId }, response);
        });
        _runtime.InterstarUpdateCycleCompleted += (_, result) => OnUiThread(() =>
        {
            InterstarStatusText = "Interstar state: " + result.Message;
            Status = result.Message;
        });
        _runtime.InterstarUpdateFailed += (_, ex) => OnUiThread(() =>
        {
            InterstarStatusText = "Interstar state: failed — " + ex.Message;
        });
        _runtime.StoryGenerationCompleted += (_, response) => _ = HandleGeneratedStoryResponseAsync(response);
        _runtime.GirlfriendProposalCompleted += (_, response) => _ = HandleGeneratedGirlfriendProposalResponseAsync(response);
        _runtime.StoryGenerationFailed += (_, ex) => OnUiThread(() =>
        {
            Status = "Story generation failed after thinking phrase: " + ex.Message;
        });
        _runtime.GirlfriendProposalFailed += (_, ex) => OnUiThread(() =>
        {
            Status = "Girlfriend proposal response failed after thinking phrase: " + ex.Message;
        });
        _runtime.DailyStateUpdated += (_, args) => OnUiThread(() =>
        {
            TodayStarStateText = FormatTodayStarState(args.Record.SlotId, args.Record.Summary);
            Status = "Daily Star state refreshed for " + args.Record.StarId + " (" + args.Record.SlotId + ").";
            _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
            {
                EventType = "DailyStarStateUpdated",
                SessionId = args.Record.SessionId,
                StarId = args.Record.StarId,
                SelectedStarId = SelectedStarId,
                Message = args.Record.Summary,
                DailyStateSlotId = args.Record.SlotId,
                DailyStateLocalDate = args.Record.LocalDate,
                DailyStateGeneratedAt = args.Record.GeneratedAt,
                DailyStateSummary = args.Record.Summary,
                DailyStateXml = args.Record.XmlContent
            });
        });
        _runtime.DailyStateUpdateFailed += (_, ex) => OnUiThread(() =>
        {
            Status = "Daily Star state refresh failed: " + ex.Message;
        });
        _runtime.StartDailyStateBackgroundUpdates(DefaultSessionId);

        _speechOptions = new SpeechToStarOptions
        {
            SessionId = DefaultSessionId,
            Speaker = "LastCat",
            DefaultStarId = SelectedStarId,
            RequestType = RequestType.Chat,
            RequireWakeWord = SpeechRequireWakeWord,
            WakeAliases = CreateDefaultWakeAliases(),
            GameContext = CreateGameContext("Voice input pending")
        };

        _speechCoordinator = new SpeechToStarCoordinator(_runtime, new FuzzyWakeWordDetector(), _speechOptions);
        _microphoneSpeechSource = new NaudioMicrophoneSpeechSource(_speechPipeline);

        _screenAwarenessLoop = new ScreenAwarenessLoop(
            DefaultSessionId,
            _screenContextStore,
            new DesktopScreenCaptureService(),
            CreateScreenSummarizer(),
            new ScreenAwarenessOptions
            {
                CaptureInterval = TimeSpan.FromMinutes(_screenCaptureIntervalMinutes),
                CaptureImmediatelyOnStart = true,
                StoreRawImageBase64 = false
            });

        WireSpeechEvents();
        WireVoiceEvents();
        WireMicrophoneEvents();
        WireScreenEvents();

        StarIds = new ObservableCollection<string> { "Elusiaa", "Kaelia", "Maera", "Khavia", "Auerlis", "Nyxari" };
        AudioBackends = new ObservableCollection<string> { "directsound", "wasapi", "waveout", "auto" };
        RequestTypes = new ObservableCollection<RequestType>
        {
            RequestType.Chat,
            RequestType.Story,
            RequestType.Letter,
            RequestType.BattleUpdate,
            RequestType.Debug
        };

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        SendCommand = new RelayCommand(async _ => await SendAsync());
        SendMessageCommand = new RelayCommand(async _ => await SendMessageAsync());
        TellStoryCommand = new RelayCommand(async _ => await TellStoryAsync());
        InterruptStoryCommand = new RelayCommand(_ => InterruptStory("User clicked Interrupt Story."));
        GoblinHarpiesCommand = new RelayCommand(async _ => await QuickSendAsync(RequestType.Chat, "Elusiaa, maybe we should ask why the harpies were smiling before we punish the goblin at the bridge."));
        CruelEscalationCommand = new RelayCommand(async _ => await QuickSendAsync(RequestType.Chat, "Cast holy harder on the goblin and make him pay."));
        KindCompanyCommand = new RelayCommand(async _ => await QuickSendAsync(RequestType.Chat, SelectedStarId + ", stay with me and keep me company while I work. I trust you."));
        StartListeningCommand = new RelayCommand(async _ => await StartListeningAsync());
        StopListeningCommand = new RelayCommand(async _ => await StopListeningAsync());
        ToggleRecordingCommand = new RelayCommand(async _ => await ToggleRecordingAsync());
        CancelOrStopRuntimeCommand = new RelayCommand(_ => CancelAllRuntimeOperations("User clicked cancel/stop."));
        ClearSelectedStarHistoryCommand = new RelayCommand(async _ => await ClearSelectedStarHistoryAsync());
        ClearAllStarDataCommand = new RelayCommand(async _ => await ClearAllStarDataAsync());
        SimulateSpeechCommand = new RelayCommand(async _ => await SimulateSpeechAsync());
        StartScreenAwarenessCommand = new RelayCommand(async _ => await StartScreenAwarenessAsync());
        StopScreenAwarenessCommand = new RelayCommand(async _ => await StopScreenAwarenessAsync());
        CaptureScreenNowCommand = new RelayCommand(async _ => await CaptureScreenNowAsync());
        PlayElusiaaCloneWavCommand = new RelayCommand(async _ => await PlayElusiaaCloneWavAsync());
        InitializeMobileBridgeCommandsAndMaybeStart();

        if (GetBooleanSetting(Environment.GetEnvironmentVariable("SIX_STARS_ALWAYS_LISTENING"), _appConfig.VoiceInput.AlwaysListeningOnStartup))
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(async () => await StartListeningAsync()));
        }
    }

    public ObservableCollection<string> StarIds { get; }
    public ObservableCollection<string> AudioBackends { get; }
    public ObservableCollection<RequestType> RequestTypes { get; }

    public ICommand SendCommand { get; }
    public ICommand SendMessageCommand { get; }
    public ICommand TellStoryCommand { get; }
    public ICommand InterruptStoryCommand { get; }
    public ICommand GoblinHarpiesCommand { get; }
    public ICommand CruelEscalationCommand { get; }
    public ICommand KindCompanyCommand { get; }
    public ICommand StartListeningCommand { get; }
    public ICommand StopListeningCommand { get; }
    public ICommand ToggleRecordingCommand { get; }
    public ICommand CancelOrStopRuntimeCommand { get; }
    public ICommand ClearSelectedStarHistoryCommand { get; }
    public ICommand ClearAllStarDataCommand { get; }
    public ICommand SimulateSpeechCommand { get; }
    public ICommand StartScreenAwarenessCommand { get; }
    public ICommand StopScreenAwarenessCommand { get; }
    public ICommand CaptureScreenNowCommand { get; }
    public ICommand PlayElusiaaCloneWavCommand { get; }

    public string SelectedStarId
    {
        get => _selectedStarId;
        set
        {
            string normalized = ResolveDefaultStarId(value);
            if (SetProperty(ref _selectedStarId, normalized))
            {
                if (_activeStoryAutoContinue && !string.Equals(_activeStoryStarId, _selectedStarId, StringComparison.OrdinalIgnoreCase))
                {
                    InterruptStory("Selected Star changed.");
                }
                UpdateSpeechOptions();
                _ = RefreshSelectedStarStateForUiAsync("selected star changed");
                if (!_suppressMobileBridgeSelectedStarBroadcast)
                {
                    _ = PublishMobileSelectedStarUpdateAsync("desktop selected star changed");
                }
            }
        }
    }

    public RequestType SelectedRequestType
    {
        get => _selectedRequestType;
        set => SetProperty(ref _selectedRequestType, value);
    }

    public string PlayerText
    {
        get => _playerText;
        set => SetProperty(ref _playerText, value);
    }

    public string StorySuggestion
    {
        get => _storySuggestion;
        set => SetProperty(ref _storySuggestion, value);
    }

    public string Area
    {
        get => _area;
        set => SetProperty(ref _area, value);
    }

    public string Quest
    {
        get => _quest;
        set => SetProperty(ref _quest, value);
    }

    public bool RomanceMode
    {
        get => _romanceMode;
        set => SetProperty(ref _romanceMode, value);
    }

    public bool SelectedStarIsMet
    {
        get => _selectedStarIsMet;
        set
        {
            if (SetProperty(ref _selectedStarIsMet, value) && !_isLoadingSelectedStarState)
            {
                _ = SetSelectedStarMetAsync(value);
            }
        }
    }

    public bool SelectedStarIsAlly
    {
        get => _selectedStarIsAlly;
        set
        {
            if (SetProperty(ref _selectedStarIsAlly, value) && !_isLoadingSelectedStarState)
            {
                _ = SetSelectedStarAllyAsync(value);
            }
        }
    }

    public bool SpeechRequireWakeWord
    {
        get => _speechRequireWakeWord;
        set
        {
            if (SetProperty(ref _speechRequireWakeWord, value))
            {
                UpdateSpeechOptions();
            }
        }
    }

    public string SpeechStatus
    {
        get => _speechStatus;
        set => SetProperty(ref _speechStatus, value);
    }

    public string VoiceInputStatus
    {
        get => _voiceInputStatus;
        set => SetProperty(ref _voiceInputStatus, value);
    }

    public string MicrophoneCaptureStatus
    {
        get => _microphoneCaptureStatus;
        set => SetProperty(ref _microphoneCaptureStatus, value);
    }

    public string IncomingVoiceDebug
    {
        get => _incomingVoiceDebug;
        set => SetProperty(ref _incomingVoiceDebug, value);
    }

    public string LastVoiceSegmentDetails
    {
        get => _lastVoiceSegmentDetails;
        set => SetProperty(ref _lastVoiceSegmentDetails, value);
    }

    public double IncomingVoiceLevelPercent
    {
        get => _incomingVoiceLevelPercent;
        set => SetProperty(ref _incomingVoiceLevelPercent, Math.Max(0, Math.Min(100, value)));
    }

    public string LastTranscript
    {
        get => _lastTranscript;
        set => SetProperty(ref _lastTranscript, value);
    }

    public string ScreenStatus
    {
        get => _screenStatus;
        set => SetProperty(ref _screenStatus, value);
    }

    public double ScreenCaptureIntervalMinutes
    {
        get => _screenCaptureIntervalMinutes;
        set => SetProperty(ref _screenCaptureIntervalMinutes, Math.Max(0.25, value));
    }

    public string SelectedAudioBackend
    {
        get => _selectedAudioBackend;
        set
        {
            string normalized = NormalizeAudioBackend(value);
            if (SetProperty(ref _selectedAudioBackend, normalized))
            {
                Environment.SetEnvironmentVariable("SIX_STARS_AUDIO_BACKEND", normalized);
                Status = $"Audio backend set to {normalized}. Future playback will use this NAudio backend.";
                System.Diagnostics.Debug.WriteLine($"[Audio] SIX_STARS_AUDIO_BACKEND set from UI: {normalized}");
            }
        }
    }

    public string RecordButtonText
    {
        get => _recordButtonText;
        set => SetProperty(ref _recordButtonText, value);
    }

    public string CancelStopButtonText
    {
        get => _cancelStopButtonText;
        set => SetProperty(ref _cancelStopButtonText, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        set => SetProperty(ref _isRecording, value);
    }

    public bool IsProcessingRuntime
    {
        get => _isProcessingRuntime;
        set => SetProperty(ref _isProcessingRuntime, value);
    }

    public string StarOutput
    {
        get => _starOutput;
        set => SetProperty(ref _starOutput, value);
    }

    public string DebugJson
    {
        get => _debugJson;
        set => SetProperty(ref _debugJson, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string TopAffectedEmotionsText
    {
        get => _topAffectedEmotionsText;
        set => SetProperty(ref _topAffectedEmotionsText, value);
    }

    public string LastEmotionVectorsText
    {
        get => _lastEmotionVectorsText;
        set => SetProperty(ref _lastEmotionVectorsText, value);
    }

    public string InterstarStatusText
    {
        get => _interstarStatusText;
        set => SetProperty(ref _interstarStatusText, value);
    }

    public string TodayStarStateText
    {
        get => _todayStarStateText;
        set => SetProperty(ref _todayStarStateText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void ConfigureLocalModelEnvironment()
    {
        LocalModelServiceOptions options = LocalModelPaths.BuildDefaultServiceOptions(_appConfig.LocalModels.Root);
        if (!string.IsNullOrWhiteSpace(_appConfig.LocalModels.HuggingFaceHome))
        {
            options.HuggingFaceHome = Path.IsPathRooted(_appConfig.LocalModels.HuggingFaceHome)
                ? _appConfig.LocalModels.HuggingFaceHome
                : Path.Combine(options.ModelsRoot, "hf");
        }
        if (!string.IsNullOrWhiteSpace(_appConfig.LocalModels.HuggingFaceHubCache))
        {
            options.HuggingFaceHubCache = Path.IsPathRooted(_appConfig.LocalModels.HuggingFaceHubCache)
                ? _appConfig.LocalModels.HuggingFaceHubCache
                : Path.Combine(options.HuggingFaceHome, "hub");
        }

        LocalModelPaths.ApplyEnvironment(options);
        System.Diagnostics.Debug.WriteLine("[LocalModels] Models root: " + options.ModelsRoot);
        System.Diagnostics.Debug.WriteLine("[LocalModels] HF_HOME: " + options.HuggingFaceHome);
        System.Diagnostics.Debug.WriteLine("[LocalModels] HF_HUB_CACHE: " + options.HuggingFaceHubCache);
    }

    private LocalModelServiceOptions CreateLocalModelServiceOptions(LocalChatModelOptions chatOptions)
    {
        LocalModelServiceOptions options = LocalModelPaths.BuildDefaultServiceOptions(_appConfig.LocalModels.Root);
        options.Chat = chatOptions;
        options.Chat.EndpointUrl = string.IsNullOrWhiteSpace(options.Chat.EndpointUrl) ? _appConfig.LocalModels.LlmEndpointUrl : options.Chat.EndpointUrl;
        options.Chat.Model = string.IsNullOrWhiteSpace(options.Chat.Model) ? _appConfig.LocalModels.LlmModel : options.Chat.Model;
        options.Whisper.EndpointUrl = Environment.GetEnvironmentVariable("SIX_STARS_WHISPER_ENDPOINT") ?? _appConfig.Whisper.EndpointUrl ?? _appConfig.LocalModels.WhisperEndpointUrl;
        options.Whisper.ModelPath = Environment.GetEnvironmentVariable("SIX_STARS_WHISPER_MODEL_PATH") ?? _appConfig.Whisper.ModelPath ?? _appConfig.LocalModels.WhisperLocalPath;
        options.Tts.WebSocketUrl = Environment.GetEnvironmentVariable("SIX_STARS_WEBSOCKET_TTS_URL") ?? _appConfig.WebSocketTTS.WebSocketUrl ?? _appConfig.LocalModels.QwenTtsWebSocketUrl;
        options.Tts.Model = Environment.GetEnvironmentVariable("SIX_STARS_WEBSOCKET_TTS_MODEL") ?? _appConfig.WebSocketTTS.Model ?? _appConfig.LocalModels.QwenTtsModel;
        LocalModelPaths.ApplyEnvironment(options);
        return options;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? GetLlmApiKey(string? configuredApiKey)
    {
        // LM Studio's newer developer server can require Authorization: Bearer <token>.
        // Support both the Six Stars env var and LM Studio's own LM_API_TOKEN name so the
        // WPF app works whether it is launched from Visual Studio, launch.cmd, or a terminal.
        return FirstNonEmpty(
            Environment.GetEnvironmentVariable("SIX_STARS_LLM_API_KEY"),
            Environment.GetEnvironmentVariable("LM_API_TOKEN"),
            Environment.GetEnvironmentVariable("LM_STUDIO_API_KEY"),
            configuredApiKey);
    }

    private static string? GetScreenLlmApiKey(string? configuredApiKey)
    {
        return FirstNonEmpty(
            Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_LLM_API_KEY"),
            Environment.GetEnvironmentVariable("SIX_STARS_LLM_API_KEY"),
            Environment.GetEnvironmentVariable("LM_API_TOKEN"),
            Environment.GetEnvironmentVariable("LM_STUDIO_API_KEY"),
            configuredApiKey);
    }

    private DailyStarStateOptions CreateDailyStarStateOptions()
    {
        DailyStarStateConfig cfg = _appConfig.DailyStarState ?? new DailyStarStateConfig();
        DailyStarStateOptions options = new DailyStarStateOptions
        {
            Enabled = cfg.Enabled,
            GenerateForAllStars = cfg.GenerateForAllStars,
            RefreshSelectedStarOnRequest = cfg.RefreshSelectedStarOnRequest,
            StateRoot = string.IsNullOrWhiteSpace(cfg.StateRoot) ? "state/daily-star-state" : cfg.StateRoot,
            PollSeconds = GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_DAILY_STATE_POLL_SECONDS"), cfg.PollSeconds),
            MaxXmlCharsForPrompt = GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_DAILY_STATE_MAX_XML_CHARS"), cfg.MaxXmlCharsForPrompt),
            StarIds = cfg.StarIds == null ? new List<string>() : cfg.StarIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList(),
            RefreshSlots = cfg.RefreshSlots == null || cfg.RefreshSlots.Count == 0
                ? DailyStarStateOptions.CreateDefaultSlots()
                : cfg.RefreshSlots.Select(x => new DailyStarStateScheduleSlot
                {
                    SlotId = x.SlotId,
                    StartsAt = x.StartsAt,
                    RefreshBefore = x.RefreshBefore
                }).ToList()
        };
        return options;
    }

    private IStarAgent CreateStarAgent()
    {
        string? endpoint = Environment.GetEnvironmentVariable("SIX_STARS_LLM_ENDPOINT")
            ?? _appConfig.LLM.EndpointUrl
            ?? _appConfig.LocalModels.LlmEndpointUrl;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            System.Diagnostics.Debug.WriteLine("[LLM] No endpoint configured. Using MockStarAgent.");
            return new MockStarAgent();
        }

        string contentRoot = InstructionLibrary.FindDefaultContentRoot();
        InstructionLibrary library = new InstructionLibrary(contentRoot);
        LocalChatModelOptions options = new LocalChatModelOptions
        {
            EndpointUrl = endpoint,
            Model = Environment.GetEnvironmentVariable("SIX_STARS_LLM_MODEL")
                ?? _appConfig.LLM.Model
                ?? _appConfig.LocalModels.LlmModel,
            ApiKey = GetLlmApiKey(_appConfig.LLM.ApiKey),
            UseJsonSchemaResponseFormat = GetBooleanSetting(
                Environment.GetEnvironmentVariable("SIX_STARS_LLM_USE_JSON_SCHEMA"),
                _appConfig.LLM.UseJsonSchemaResponseFormat),
            DisableReasoning = GetBooleanSetting(
                Environment.GetEnvironmentVariable("SIX_STARS_LLM_DISABLE_REASONING"),
                _appConfig.LLM.DisableReasoning),
            ReasoningBudget = GetIntSetting(
                Environment.GetEnvironmentVariable("SIX_STARS_LLM_REASONING_BUDGET"),
                _appConfig.LLM.ReasoningBudget),
            ReasoningFormat = Environment.GetEnvironmentVariable("SIX_STARS_LLM_REASONING_FORMAT")
                ?? _appConfig.LLM.ReasoningFormat,
            SendReasoningControlFields = GetBooleanSetting(
                Environment.GetEnvironmentVariable("SIX_STARS_LLM_SEND_REASONING_CONTROL_FIELDS"),
                _appConfig.LLM.SendReasoningControlFields),
            TimeoutSeconds = GetIntSetting(
                Environment.GetEnvironmentVariable("SIX_STARS_LLM_TIMEOUT_SECONDS"),
                _appConfig.LLM.TimeoutSeconds),
            MaxTokens = GetIntSettingAllowZero(
                Environment.GetEnvironmentVariable("SIX_STARS_LLM_MAX_TOKENS"),
                _appConfig.LLM.MaxTokens),
            Temperature = GetDoubleSetting(
                Environment.GetEnvironmentVariable("SIX_STARS_LLM_TEMPERATURE"),
                _appConfig.LLM.Temperature)
        };

        System.Diagnostics.Debug.WriteLine($"[LLM] Configured endpoint: {options.EndpointUrl}");
        System.Diagnostics.Debug.WriteLine($"[LLM] Model: {options.Model}");
        System.Diagnostics.Debug.WriteLine($"[LLM] TimeoutSeconds: {options.TimeoutSeconds}, MaxTokens: {(options.MaxTokens > 0 ? options.MaxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture) : "not sent")}, Temperature: {options.Temperature}, JsonSchema: {options.UseJsonSchemaResponseFormat}, DisableReasoning: {options.DisableReasoning}, ReasoningBudget: {options.ReasoningBudget}, ReasoningFormat: {options.ReasoningFormat}");

        StarPromptOptions promptOptions = new StarPromptOptions
        {
            RecentHistoryCount = GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_RECENT_HISTORY_FOR_PROMPT"), _appConfig.Memory.RecentHistoryForPrompt),
            ImportantQuoteCount = GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_IMPORTANT_QUOTES_FOR_PROMPT"), _appConfig.Memory.ImportantQuotesForPrompt),
            MemoryNoteCount = GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_MEMORY_NOTES_FOR_PROMPT"), _appConfig.Memory.MemoryNotesForPrompt),
            RelationshipEventCount = GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_RELATIONSHIP_EVENTS_FOR_PROMPT"), _appConfig.Memory.RelationshipEventsForPrompt),
            GirlfriendHistoryCount = GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_GIRLFRIEND_HISTORY_FOR_PROMPT"), _appConfig.Memory.GirlfriendHistoryForPrompt),
            LearnedFunctionCount = GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_LEARNED_FUNCTIONS_FOR_PROMPT"), _appConfig.Memory.LearnedFunctionsForPrompt),
            SecretStateCount = GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_SECRET_STATES_FOR_PROMPT"), _appConfig.Memory.SecretStatesForPrompt),
            RelationshipComboCount = GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_RELATIONSHIP_COMBOS_FOR_PROMPT"), _appConfig.Memory.RelationshipCombosForPrompt),
            ScreenCaptureCount = GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_CAPTURES_FOR_PROMPT"), _appConfig.Memory.ScreenCapturesForPrompt)
        };

        System.Diagnostics.Debug.WriteLine($"[PromptMemory] RecentHistoryCount: {promptOptions.RecentHistoryCount}, ImportantQuotes: {promptOptions.ImportantQuoteCount}, MemoryNotes: {promptOptions.MemoryNoteCount}");

        LocalModelServiceOptions localOptions = CreateLocalModelServiceOptions(options);
        LocalAiClient client = new LocalAiClient(localOptions);
        client.ExchangeLogged += (_, args) => AppendLlmDebugLog(args.FormattedText);

        // When an LLM endpoint is configured, do not silently fall back to mock Star data.
        // If the local model returns imperfect JSON, StructuredStarAgent now salvages the
        // real raw response into spokenText and memory fields. If the endpoint is actually
        // down, the UI should show the real error instead of pretending a mock reply happened.
        return new StructuredStarAgent(
            client,
            library.LoadMainInstructions(),
            promptOptions,
            GetIntSettingAllowZero(Environment.GetEnvironmentVariable("SIX_STARS_LLM_SPEECH_MAX_TOKENS"), _appConfig.LLM.SpeechMaxTokens),
            GetIntSettingAllowZero(Environment.GetEnvironmentVariable("SIX_STARS_LLM_REVIEW_MAX_TOKENS"), _appConfig.LLM.ReviewMaxTokens));
    }

    private IWhisperSpeechStreamer CreateWhisperStreamer()
    {
        string? endpoint = Environment.GetEnvironmentVariable("SIX_STARS_WHISPER_ENDPOINT")
            ?? _appConfig.Whisper.EndpointUrl
            ?? _appConfig.LocalModels.WhisperEndpointUrl;
        bool useMockWhisper = GetBooleanSetting(Environment.GetEnvironmentVariable("SIX_STARS_USE_MOCK_WHISPER"), false);
        if (useMockWhisper)
        {
            System.Diagnostics.Debug.WriteLine("[Whisper] SIX_STARS_USE_MOCK_WHISPER=true. Using mock transcript streamer intentionally.");
            return new MockWhisperSpeechStreamer();
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            System.Diagnostics.Debug.WriteLine("[Whisper] No endpoint configured. Real voice input will fail loudly instead of using a mock transcript.");
            return new UnconfiguredWhisperSpeechStreamer();
        }

        System.Diagnostics.Debug.WriteLine($"[Whisper] Real microphone transcription endpoint: {endpoint}");
        return new LocalWhisperHttpSpeechStreamer(new WhisperStreamEndpointOptions
        {
            Provider = Environment.GetEnvironmentVariable("SIX_STARS_WHISPER_PROVIDER") ?? _appConfig.Whisper.Provider,
            EndpointUrl = endpoint,
            ModelPath = Environment.GetEnvironmentVariable("SIX_STARS_WHISPER_MODEL_PATH")
                ?? (!string.IsNullOrWhiteSpace(_appConfig.Whisper.ModelPath) ? _appConfig.Whisper.ModelPath : _appConfig.LocalModels.WhisperLocalPath),
            ApiKey = Environment.GetEnvironmentVariable("SIX_STARS_WHISPER_API_KEY") ?? _appConfig.Whisper.ApiKey,
            Language = Environment.GetEnvironmentVariable("SIX_STARS_WHISPER_LANGUAGE") ?? _appConfig.Whisper.Language,
            TimeoutSeconds = GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_WHISPER_TIMEOUT_SECONDS"), _appConfig.Whisper.TimeoutSeconds)
        });
    }

    private VoiceCaptureSettings CreateVoiceCaptureSettings()
    {
        return VoiceInputConfigurationFactory.CreateTranscribeOnActivitySettings(
            minSoundMs: GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_VOICE_MIN_SOUND_MS"), _appConfig.VoiceInput.MinSoundMs),
            silenceEndMs: GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_VOICE_SILENCE_END_MS"), _appConfig.VoiceInput.SilenceEndMs),
            preRollMs: GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_VOICE_PRE_ROLL_MS"), _appConfig.VoiceInput.PreRollMs),
            rmsSpeechThreshold: GetDoubleSetting(Environment.GetEnvironmentVariable("SIX_STARS_VOICE_RMS_THRESHOLD"), _appConfig.VoiceInput.RmsSpeechThreshold),
            maxSegmentMs: GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_VOICE_MAX_SEGMENT_MS"), _appConfig.VoiceInput.MaxSegmentMs),
            muteInputWhileStarSpeaking: GetBooleanSetting(Environment.GetEnvironmentVariable("SIX_STARS_MUTE_INPUT_WHILE_STAR_SPEAKING"), _appConfig.VoiceInput.MuteInputWhileStarSpeaking));
    }

    private IStarVoiceGenerator CreateVoiceGenerator()
    {
        System.Diagnostics.Debug.WriteLine("[TTS] HTTP /v1/audio/speech generator disabled. Desktop uses WebSocket streaming TTS only; this mock fallback is used only if streaming is explicitly turned off.");
        return new MockStarVoiceGenerator();
    }

    private IWavePlaybackEndpoint CreatePlaybackEndpoint()
    {
        IWavePlaybackEndpoint playbackEndpoint = new NaudioWavePlaybackEndpoint(SelectedAudioBackend);

        bool saveGeneratedWavs = GetBooleanSetting(
            Environment.GetEnvironmentVariable("SIX_STARS_SAVE_GENERATED_WAVS")
                ?? Environment.GetEnvironmentVariable("SIX_STARS_SAVE_VOICE_WAVS"),
            _appConfig.VoiceOutput.SaveGeneratedWavs);

        if (!saveGeneratedWavs)
        {
            System.Diagnostics.Debug.WriteLine("[VoiceArchive] Generated WAV saving disabled.");
            return playbackEndpoint;
        }

        string outputDirectory = Environment.GetEnvironmentVariable("SIX_STARS_GENERATED_WAV_DIR")
            ?? _appConfig.VoiceOutput.GeneratedWavDirectory;

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            outputDirectory = Directory.GetCurrentDirectory();
        }

        bool saveMetadataText = GetBooleanSetting(
            Environment.GetEnvironmentVariable("SIX_STARS_SAVE_VOICE_METADATA"),
            _appConfig.VoiceOutput.SaveMetadataText);

        System.Diagnostics.Debug.WriteLine($"[VoiceArchive] Generated WAV saving enabled. Directory: {outputDirectory}");
        return new VoiceWavArchivePlaybackEndpoint(playbackEndpoint, outputDirectory, saveMetadataText);
    }

    private IStreamingVoiceGenerator CreateStreamingVoiceGenerator()
    {
        string? wsUrl = Environment.GetEnvironmentVariable("SIX_STARS_WEBSOCKET_TTS_URL")
            ?? _appConfig.WebSocketTTS.WebSocketUrl
            ?? _appConfig.LocalModels.QwenTtsWebSocketUrl;
        if (string.IsNullOrWhiteSpace(wsUrl))
        {
            wsUrl = "ws://localhost:8100/tts/stream";
        }
        System.Diagnostics.Debug.WriteLine($"[Streaming TTS] Configured WebSocket URL: {wsUrl}");

        return new WebSocketTtsVoiceGenerator(new WebSocketTtsOptions
        {
            WebSocketUrl = wsUrl,
            Model = Environment.GetEnvironmentVariable("SIX_STARS_WEBSOCKET_TTS_MODEL")
                ?? _appConfig.WebSocketTTS.Model
                ?? _appConfig.LocalModels.QwenTtsModel,
            DefaultLanguage = Environment.GetEnvironmentVariable("SIX_STARS_WEBSOCKET_TTS_LANGUAGE") ?? _appConfig.WebSocketTTS.DefaultLanguage,
            ResponseFormat = _appConfig.WebSocketTTS.ResponseFormat,
            TaskType = Environment.GetEnvironmentVariable("SIX_STARS_WEBSOCKET_TTS_TASK_TYPE") ?? _appConfig.WebSocketTTS.TaskType,
            MaxWordsPerChunk = _appConfig.WebSocketTTS.MaxWordsPerChunk,
            MaxSentencesPerChunk = _appConfig.WebSocketTTS.MaxSentencesPerChunk,
            ChunkGenerationMode = Environment.GetEnvironmentVariable("QWEN_TTS_CHUNK_GENERATION_MODE") ?? _appConfig.WebSocketTTS.ChunkGenerationMode,
            MaxParallelChunks = GetIntSetting(Environment.GetEnvironmentVariable("QWEN_TTS_MAX_PARALLEL_CHUNKS"), _appConfig.WebSocketTTS.MaxParallelChunks)
        });
    }

    private IScreenSummarizer CreateScreenSummarizer()
    {
        bool screenEnabled = GetBooleanSetting(
            Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_LLM_ENABLED"),
            _appConfig.ScreenLLM.Enabled);

        if (!screenEnabled)
        {
            System.Diagnostics.Debug.WriteLine("[Screen LLM] Screen vision is disabled. Using mock screen summarizer.");
            return new MockScreenSummarizer();
        }

        string? endpoint = Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_LLM_ENDPOINT")
            ?? _appConfig.ScreenLLM.EndpointUrl
            ?? _appConfig.LocalModels.LlmEndpointUrl;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            System.Diagnostics.Debug.WriteLine("[Screen LLM] No endpoint configured. Using mock screen summarizer.");
            return new MockScreenSummarizer();
        }

        LocalChatModelOptions options = new LocalChatModelOptions
        {
            EndpointUrl = endpoint,
            Model = Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_LLM_MODEL")
                ?? _appConfig.ScreenLLM.Model
                ?? _appConfig.LocalModels.LlmModel,
            ApiKey = GetScreenLlmApiKey(_appConfig.ScreenLLM.ApiKey),
            UseJsonSchemaResponseFormat = GetBooleanSetting(
                Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_LLM_USE_JSON_SCHEMA"),
                _appConfig.ScreenLLM.UseJsonSchemaResponseFormat),
            DisableReasoning = GetBooleanSetting(
                Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_LLM_DISABLE_REASONING"),
                _appConfig.ScreenLLM.DisableReasoning),
            ReasoningBudget = GetIntSetting(
                Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_LLM_REASONING_BUDGET"),
                _appConfig.ScreenLLM.ReasoningBudget),
            ReasoningFormat = Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_LLM_REASONING_FORMAT")
                ?? _appConfig.ScreenLLM.ReasoningFormat,
            SendReasoningControlFields = GetBooleanSetting(
                Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_LLM_SEND_REASONING_CONTROL_FIELDS"),
                _appConfig.ScreenLLM.SendReasoningControlFields),
            TimeoutSeconds = GetIntSetting(
                Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_LLM_TIMEOUT_SECONDS"),
                _appConfig.ScreenLLM.TimeoutSeconds),
            MaxTokens = GetIntSettingAllowZero(
                Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_LLM_MAX_TOKENS"),
                _appConfig.ScreenLLM.MaxTokens),
            Temperature = 0.1
        };

        System.Diagnostics.Debug.WriteLine($"[Screen LLM] Configured endpoint: {options.EndpointUrl}; model={options.Model}; max_tokens={options.MaxTokens}");
        return new OpenAiCompatibleVisionScreenSummarizer(options);
    }

    private void WireSpeechEvents()
    {
        _speechPipeline.StatusChanged += (_, args) => OnUiThread(() =>
        {
            SpeechStatus = args.Status + ": " + args.Message;
            VoiceInputStatus = args.Message;
        });

        _speechPipeline.InputLevelChanged += (_, args) => OnUiThread(() =>
        {
            IncomingVoiceLevelPercent = args.LevelPercent;
            IncomingVoiceDebug = $"RMS {args.Rms:0.0000} / threshold {args.Threshold:0.0000} | speech={(args.IsSpeech ? "yes" : "no")} | active={(args.IsSpeechActive ? "yes" : "no")} | mode={args.ListenMode} | source={args.Source}";
        });

        // Wire up speech interruption
        _speechPipeline.SpeechSegmentStarted += (_, args) =>
        {
            OnUiThread(() =>
            {
                SpeechStatus = "Speech active. Segment " + args.Segment.SegmentId + " started.";
                VoiceInputStatus = "Recording utterance from microphone until silence.";
                LastVoiceSegmentDetails = "Active segment " + args.Segment.SegmentId + " from " + args.Segment.Source + ".";
            });
            _interruptionManager.OnUserSpeechStarted();
        };

        _speechPipeline.SpeechSegmentCompleted += (_, args) =>
        {
            OnUiThread(() =>
            {
                SpeechStatus = "Speech segment completed. " + args.Segment.DurationMs.ToString("0") + " ms captured.";
                VoiceInputStatus = "Utterance ended. Sending captured WAV to Whisper.";
                LastVoiceSegmentDetails = $"Segment {args.Segment.SegmentId}: {args.Segment.DurationMs:0} ms, {args.Segment.Pcm16Audio.Length} bytes, chunks={args.Segment.AudioChunkCount}, peak RMS={args.Segment.PeakRms:0.0000}, source={args.Segment.Source}.";
            });
            _interruptionManager.OnUserSpeechStopped();
        };

        _speechPipeline.SpeechSegmentIgnored += (_, args) => OnUiThread(() =>
        {
            SpeechStatus = "Speech ignored: " + args.Reason;
            VoiceInputStatus = "Captured audio ignored after transcription/wake filtering.";
            LastVoiceSegmentDetails = $"Ignored segment {args.Segment.SegmentId}: {args.Segment.DurationMs:0} ms, peak RMS={args.Segment.PeakRms:0.0000}. Reason: {args.Reason}";
        });
        _speechPipeline.TranscriptReady += async (_, args) => await ProcessTranscriptFromSpeechPipelineAsync(args.Transcript);
        _speechCoordinator.SpeechRecognized += (_, args) => OnUiThread(() =>
        {
            LastTranscript = args.RecognizedSpeech.Transcript.Text;
            SpeechStatus = args.RecognizedSpeech.WasIgnored
                ? "Transcript ignored: " + args.RecognizedSpeech.IgnoreReason
                : "Transcript recognized for " + args.RecognizedSpeech.TargetStarId + ". Text wake/name required: " + SpeechRequireWakeWord + ".";
            VoiceInputStatus = args.RecognizedSpeech.WasIgnored
                ? "Whisper returned text, but the text wake-name filter ignored it."
                : "Whisper transcript accepted and sent to the Star runtime.";
        });
        _speechCoordinator.StarResponded += (_, args) => OnUiThread(() =>
        {
            TrackStoryFromResponse(args.Response);
            StarOutput = FormatStarOutput(args.Response);

            // Track state changes
            _stateChangeTracker.RecordStateChange(
                args.Response.StarId,
                args.Response.StateDeltas.Heart,
                args.Response.StateDeltas.Trust,
                args.Response.StateDeltas.Romance,
                args.Response.StateDeltas.BenaveilInfluence,
                args.Response.StateDeltas.EmotionalConnectionPercent);

            // Enhanced debug output with state tracking
            DebugJson = CreateEnhancedDebugOutput(args.RecognizedSpeech, args.Response);

            Status = "Voice processed. Heart Δ " + args.Response.StateDeltas.Heart + ", Trust Δ " + args.Response.StateDeltas.Trust + ", Romance Δ " + args.Response.StateDeltas.Romance + ", Connection Δ " + args.Response.StateDeltas.EmotionalConnectionPercent.ToString("0.00") + ". Generating Star voice...";
            CancellationToken token = _operationController.BeginNewOperation("speech transcript reply");
            IsProcessingRuntime = true;
            _ = SpeakStarResponseAsync(args.Response, token);
        });
    }

    private void WireVoiceEvents()
    {
        if (_useStreamingTts && _streamingVoiceResponsePipeline != null)
        {
            _streamingVoiceResponsePipeline.ChunkReceived += (_, args) => OnUiThread(() => 
            {
                Status = $"Voice chunk {args.ChunkIndex + 1}/{args.TotalChunks} received: {args.AudioData.Length} bytes";
                System.Diagnostics.Debug.WriteLine($"[Streaming TTS] Chunk {args.ChunkIndex + 1}/{args.TotalChunks}: '{args.Text.Substring(0, System.Math.Min(40, args.Text.Length))}...'");
            });

            _streamingVoiceResponsePipeline.ChunkReadyForConsumer += (_, args) => OnUiThread(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[Streaming TTS] Ordered chunk ready for host: {args.Result.WavAudio.Length} bytes from {args.Result.Source}");
            });

            _streamingVoiceResponsePipeline.ChunkPlaybackStarted += (_, args) => OnUiThread(() => 
            {
                Status = $"Playing chunk {args.ChunkIndex + 1}/{args.TotalChunks}: '{args.Text.Substring(0, System.Math.Min(40, args.Text.Length))}...'";
            });

            _streamingVoiceResponsePipeline.ChunkPlaybackCompleted += (_, args) => OnUiThread(() => 
            {
                Status = $"Chunk {args.ChunkIndex + 1}/{args.TotalChunks} playback completed.";
            });

            _streamingVoiceResponsePipeline.AllChunksComplete += (_, args) => OnUiThread(() => 
            {
                ReleasePendingBackgroundAnalysis("Streaming voice generation completed: " + args.TotalChunks + " chunks.");
                Status = $"✓ All {args.TotalChunks} chunks generated ({args.TotalDurationMs:0}ms). Background emotion/memory review can settle while playback/listening continues.";
            });

            _streamingVoiceResponsePipeline.VoiceInterrupted += (_, args) => OnUiThread(() => 
            {
                Status = "⚠ Star speech interrupted by user";
                System.Diagnostics.Debug.WriteLine($"[VoiceInterruption] {args.Reason}");
            });

            _streamingVoiceResponsePipeline.GenerationError += (_, args) => OnUiThread(() => 
            {
                Status = $"⚠ TTS error: {args.ErrorMessage}";
                System.Diagnostics.Debug.WriteLine($"[Streaming TTS Error] {args.ErrorMessage}");
            });
        }
        else if (_voiceResponsePipeline != null)
        {
            _voiceResponsePipeline.VoiceGenerated += (_, args) => OnUiThread(() => Status = "Voice generated: " + args.Result.WavAudio.Length + " bytes from " + args.Result.Source + ".");
            _voiceResponsePipeline.VoicePlaybackStarting += (_, _) => OnUiThread(() => Status = "Playing Star voice. Microphone input is muted during playback.");
            _voiceResponsePipeline.VoicePlaybackCompleted += (_, _) => OnUiThread(() => Status = "Star voice playback completed. Listening can resume.");
            _voiceResponsePipeline.VoiceInterrupted += (_, args) => OnUiThread(() => 
            {
                Status = "⚠ Star speech interrupted by user after " + args.UserSpeechDuration.TotalMilliseconds.ToString("0") + "ms";
                System.Diagnostics.Debug.WriteLine($"[VoiceInterruption] {args.Reason} - During generation: {args.InterruptedDuringGeneration}, During playback: {args.InterruptedDuringPlayback}");
            });
        }

        _interruptionManager.SpeechInterrupted += (_, args) => OnUiThread(() =>
        {
            SpeechStatus = "User interrupted Star's speech (" + args.UserSpeechDuration.TotalMilliseconds.ToString("0") + "ms)";
        });
    }

    private void WireMicrophoneEvents()
    {
        _microphoneSpeechSource.StatusChanged += (_, message) => OnUiThread(() =>
        {
            MicrophoneCaptureStatus = message;
            SpeechStatus = message;
        });
    }

    private void WireScreenEvents()
    {
        _screenAwarenessLoop.StatusChanged += (_, message) => OnUiThread(() => ScreenStatus = message);
        _screenAwarenessLoop.CaptureStored += (_, summary) => OnUiThread(() =>
        {
            ScreenStatus = "Captured " + summary.ActivityType + ": " + summary.CompanionSafeSummary;
        });
    }

    private void ReleasePendingBackgroundAnalysis(string reason)
    {
        string analysisId = _pendingBackgroundAnalysisId;
        if (string.IsNullOrWhiteSpace(analysisId))
        {
            return;
        }

        if (_runtime.StartBackgroundAnalysisAfterVoiceReady(analysisId, reason))
        {
            _pendingBackgroundAnalysisId = string.Empty;
            System.Diagnostics.Debug.WriteLine("[BackgroundAnalysis] Released after voice ready: " + reason);
        }
    }

    private async Task HandleGeneratedStoryResponseAsync(StarInteractionResponse response)
    {
        CancellationToken token = _operationController.CurrentToken;

        try
        {
            if (IsMobileBridgeStoryResponse(response))
            {
                await HandleGeneratedMobileStoryResponseAsync(response).ConfigureAwait(false);
                return;
            }

            // The first preprogrammed "thinking of a story" line may still be playing when the
            // story LLM finishes. Wait for that short line to leave the normal speech path before
            // starting the actual generated story, so audio never overlaps.
            while (IsProcessingRuntime && !token.IsCancellationRequested)
            {
                await Task.Delay(100, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            TrackStoryFromResponse(response);
            OnUiThread(() =>
            {
                IsProcessingRuntime = true;
                StarOutput = FormatStarOutput(response);
                Status = "Generated story is ready. Starting story narration...";
                DebugJson = _llmDebugLog.ToString()
                    + "\n================ GENERATED STORY READY ================\n"
                    + CreateEnhancedDebugOutput(new
                    {
                        storyGeneratedAfterThinkingPhrase = true,
                        response.StarId,
                        response.StoryTitle,
                        response.StoryId,
                        response.StoryPartIndex,
                        response.StoryIsFinished,
                        spokenParts = response.SpokenParts.Count
                    }, response);
            });

            await SpeakStarResponseAsync(response, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            OnUiThread(() => Status = "Generated story was cancelled before narration started.");
        }
        catch (Exception ex)
        {
            OnUiThread(() =>
            {
                IsProcessingRuntime = false;
                Status = "Generated story narration failed: " + ex.Message;
                DebugJson = _llmDebugLog.ToString()
                    + "\n================ GENERATED STORY NARRATION ERROR ================\n"
                    + ex;
            });
        }
    }

    private async Task HandleGeneratedGirlfriendProposalResponseAsync(StarInteractionResponse response)
    {
        CancellationToken token = _operationController.CurrentToken;
        try
        {
            if (IsMobileBridgeStoryResponse(response))
            {
                await HandleGeneratedMobileGirlfriendProposalResponseAsync(response).ConfigureAwait(false);
                return;
            }

            while (IsProcessingRuntime && !token.IsCancellationRequested)
            {
                await Task.Delay(100, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            OnUiThread(() =>
            {
                IsProcessingRuntime = true;
                StarOutput = FormatStarOutput(response);
                Status = "Generated girlfriend proposal answer is ready. Speaking now...";
                DebugJson = _llmDebugLog.ToString()
                    + "\n================ GIRLFRIEND PROPOSAL READY ================\n"
                    + CreateEnhancedDebugOutput(new
                    {
                        girlfriendProposalGeneratedAfterThinkingPhrase = true,
                        response.StarId,
                        spokenParts = response.SpokenParts.Count
                    }, response);
            });

            await SpeakStarResponseAsync(response, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            OnUiThread(() => Status = "Generated girlfriend proposal answer was cancelled before narration started.");
        }
        catch (Exception ex)
        {
            OnUiThread(() =>
            {
                IsProcessingRuntime = false;
                Status = "Generated girlfriend proposal narration failed: " + ex.Message;
                DebugJson = _llmDebugLog.ToString()
                    + "\n================ GIRLFRIEND PROPOSAL NARRATION ERROR ================\n"
                    + ex;
            });
        }
    }

    private async Task SpeakStarResponseAsync(StarInteractionResponse response, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(response.BackgroundAnalysisId))
        {
            _pendingBackgroundAnalysisId = response.BackgroundAnalysisId;
        }

        PrepareVoiceCloneReferenceForTts(response);

        bool completedNormally = false;
        bool storyPrefetchHandledEntireStory = false;
        bool storyThinkingPlaceholder = response.UnityEvents.Any(x => string.Equals(x.Type, "StoryGenerationStarted", StringComparison.OrdinalIgnoreCase));
        try
        {
            if (ShouldUseStoryAutoplayPrefetch(response))
            {
                System.Diagnostics.Debug.WriteLine("[StoryAutoplayPrefetch] Using story voice prefetch path. Next local chunks will request TTS while the current chunk is still playing.");
                await SpeakStoryAutoplayWithPrefetchAsync(response, cancellationToken).ConfigureAwait(false);
                storyPrefetchHandledEntireStory = true;
                completedNormally = true;
                OnUiThread(() => Status = "Story autoplay complete.");
            }
            else if (_useStreamingTts && _streamingVoiceResponsePipeline != null)
            {
                System.Diagnostics.Debug.WriteLine($"[Streaming TTS] Starting streaming speech for: '{response.SpokenText?.Substring(0, System.Math.Min(50, response.SpokenText?.Length ?? 0))}...'");
                await _streamingVoiceResponsePipeline.SpeakStreamingAsync(DefaultSessionId, response, cancellationToken);
                completedNormally = true;
                OnUiThread(() => Status = "Streaming voice playback complete.");
            }
            else if (_voiceResponsePipeline != null)
            {
                VoiceSynthesisResult voice = await _voiceResponsePipeline.SpeakAsync(DefaultSessionId, response, cancellationToken);
                completedNormally = true;
                OnUiThread(() => Status = "Voice WAV played. " + voice.WavAudio.Length + " bytes from " + voice.Source + ".");
            }
            else
            {
                completedNormally = true;
                OnUiThread(() => Status = "No voice pipeline configured.");
            }
        }
        catch (System.OperationCanceledException)
        {
            OnUiThread(() => Status = "Voice generation/playback cancelled.");
        }
        catch (Exception ex)
        {
            OnUiThread(() => Status = "Voice generation/playback error: " + ex.Message);
            System.Diagnostics.Debug.WriteLine($"[TTS Error] {ex}");
        }
        finally
        {
            OnUiThread(() =>
            {
                IsProcessingRuntime = false;
                if (!IsRecording)
                {
                    RecordButtonText = "Start Recording (Win+Ctrl+E)";
                    CancelStopButtonText = "Cancel";
                }
            });

            if (completedNormally && !storyPrefetchHandledEntireStory && !cancellationToken.IsCancellationRequested)
            {
                ReleasePendingBackgroundAnalysis("Voice generation/playback path completed normally.");
            }

            bool storyContinued = false;
            if (completedNormally && !storyPrefetchHandledEntireStory && !cancellationToken.IsCancellationRequested)
            {
                storyContinued = await TryContinueActiveStoryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!storyContinued && completedNormally && _voiceConversationLoopActive && !cancellationToken.IsCancellationRequested)
            {
                if (storyThinkingPlaceholder)
                {
                    OnUiThread(() => Status = "Waiting for generated story to finish thinking before listening again...");
                }
                else
                {
                    await StartManualRecordingAsync(loopContinuation: true).ConfigureAwait(false);
                }
            }
        }
    }


    private bool ShouldUseStoryAutoplayPrefetch(StarInteractionResponse response)
    {
        if (_storyAutoplayPrefetchStreamingGenerator == null && _storyAutoplayPrefetchVoiceGenerator == null) return false;
        return _storySpeechAdapter.ShouldUseStoryAutoplay(response);
    }

    private async Task SpeakStoryAutoplayWithPrefetchAsync(StarInteractionResponse firstResponse, CancellationToken cancellationToken)
    {
        StarInteractionResponse currentResponse = firstResponse;
        IDisposable? outputKeepAlive = null;
        if (_playbackEndpoint is IAudioOutputKeepAliveEndpoint keepAliveEndpoint)
        {
            outputKeepAlive = keepAliveEndpoint.StartOutputKeepAlive("story autoplay prefetch generation and playback", cancellationToken);
        }

        try
        {
            Task<List<VoiceSynthesisResult>> currentVoiceTask = GenerateStoryVoiceBatchAsync(currentResponse, cancellationToken);
            bool releasedBackgroundAnalysis = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                List<VoiceSynthesisResult> currentVoice = await currentVoiceTask.ConfigureAwait(false);
            if (!releasedBackgroundAnalysis)
            {
                releasedBackgroundAnalysis = true;
                ReleasePendingBackgroundAnalysis("First story autoplay voice chunk generated; local TTS prefetch continues in background.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            StarInteractionResponse? nextResponse = null;
            Task<List<VoiceSynthesisResult>>? nextVoiceTask = null;
            string markdown = string.IsNullOrWhiteSpace(_activeStoryMarkdown) ? currentResponse.StoryMarkdown : _activeStoryMarkdown;
            int startChunkIndex = Math.Max(0, currentResponse.StoryPartIndex + 1);

            if (_storySpeechAdapter.TryGetNextNonEmptyStoryChunkFromMarkdown(markdown, startChunkIndex, out int actualChunkIndex, out string nextChunk))
            {
                if (actualChunkIndex != startChunkIndex)
                {
                    System.Diagnostics.Debug.WriteLine($"[StoryAutoplayPrefetch] Skipped empty story chunks from {startChunkIndex} to {actualChunkIndex - 1}; prefetching chunk {actualChunkIndex}.");
                }

                nextResponse = CreateLocalStoryContinuationResponse(nextChunk, actualChunkIndex);
                System.Diagnostics.Debug.WriteLine($"[StoryAutoplayPrefetch] Sending TTS generation request for story chunk {nextResponse.StoryPartIndex} while chunk {currentResponse.StoryPartIndex} is playing.");
                nextVoiceTask = GenerateStoryVoiceBatchAsync(nextResponse, cancellationToken);
            }

            await PlayStoryVoiceBatchAsync(currentVoice, currentResponse, cancellationToken).ConfigureAwait(false);

            if (nextResponse == null || nextVoiceTask == null)
            {
                OnUiThread(() =>
                {
                    _activeStoryAutoContinue = false;
                    SelectedRequestType = RequestType.Chat;
                    Status = "Story finished: " + (string.IsNullOrWhiteSpace(_activeStoryTitle) ? "active story" : _activeStoryTitle);
                    SpeechStatus = "Story autoplay finished.";
                });
                return;
            }

                currentResponse = nextResponse;
                TrackStoryFromResponse(currentResponse);
                ShowLocalStoryAutoplayResponse(currentResponse, "LOCAL STORY AUTOPLAY PREFETCH");
                currentVoiceTask = nextVoiceTask;
            }
        }
        finally
        {
            outputKeepAlive?.Dispose();
        }
    }

    private void ShowLocalStoryAutoplayResponse(StarInteractionResponse response, string heading)
    {
        OnUiThread(() =>
        {
            StarOutput = FormatStarOutput(response);
            Status = "Auto-playing cached story chunk " + (response.StoryPartIndex + 1) + " without another LLM request.";
            DebugJson = _llmDebugLog.ToString()
                + "\n================ " + heading + " ================\n"
                + JsonSerializer.Serialize(new
                {
                    response.StarId,
                    response.StoryTitle,
                    response.StoryId,
                    response.StoryPartIndex,
                    response.StoryIsFinished,
                    spokenParts = response.SpokenParts.Count,
                    note = "This story continuation was read from cached storyMarkdown. No continuation LLM request was sent. TTS generation is prefetched while the previous chunk is playing."
                }, _jsonOptions);
        });
    }

    private async Task<List<VoiceSynthesisResult>> GenerateStoryVoiceBatchAsync(StarInteractionResponse response, CancellationToken cancellationToken)
    {
        if (_storyAutoplayPrefetchStreamingGenerator != null)
        {
            return await GenerateStreamingStoryVoiceBatchAsync(_storyAutoplayPrefetchStreamingGenerator, response, cancellationToken).ConfigureAwait(false);
        }

        if (_storyAutoplayPrefetchVoiceGenerator != null)
        {
            // Do not call SpeechInterruptionManager.StartSpeechGeneration here. Story prefetch runs
            // while the current audio is playing, and StartSpeechGeneration cancels the playback CTS.
            VoiceSynthesisResult result = await _storyAutoplayPrefetchVoiceGenerator.GenerateAsync(CreateVoiceSynthesisRequest(response), cancellationToken).ConfigureAwait(false);
            return new List<VoiceSynthesisResult> { result };
        }

        return new List<VoiceSynthesisResult>();
    }

    private async Task<List<VoiceSynthesisResult>> GenerateStreamingStoryVoiceBatchAsync(IStreamingVoiceGenerator generator, StarInteractionResponse response, CancellationToken cancellationToken)
    {
        TaskCompletionSource<List<VoiceSynthesisResult>> completion = new TaskCompletionSource<List<VoiceSynthesisResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        SortedDictionary<int, VoiceSynthesisResult> chunks = new SortedDictionary<int, VoiceSynthesisResult>();
        object gate = new object();
        int expectedChunks = 0;
        bool generationComplete = false;

        void TryComplete()
        {
            if (!generationComplete) return;
            int expected = Math.Max(expectedChunks, chunks.Count);
            if (expected <= 0)
            {
                completion.TrySetResult(new List<VoiceSynthesisResult>());
                return;
            }

            if (chunks.Count >= expected)
            {
                completion.TrySetResult(chunks.OrderBy(x => x.Key).Select(x => x.Value).ToList());
            }
        }

        void OnAudioChunkReady(object? sender, AudioChunkEventArgs e)
        {
            lock (gate)
            {
                expectedChunks = Math.Max(expectedChunks, e.TotalChunks);
                chunks[e.ChunkIndex] = new VoiceSynthesisResult
                {
                    StarId = e.StarId,
                    VoiceId = e.VoiceId,
                    WavAudio = CopyBytes(e.AudioData),
                    MimeType = e.MimeType,
                    Instructions = e.Text,
                    Source = "story-prefetch-streaming-websocket"
                };
                System.Diagnostics.Debug.WriteLine($"[StoryAutoplayPrefetch] Voice audio ready for story chunk {response.StoryPartIndex}, TTS chunk {e.ChunkIndex + 1}/{e.TotalChunks} ({e.AudioData.Length} bytes).");
                TryComplete();
            }
        }

        void OnGenerationComplete(object? sender, GenerationCompleteEventArgs e)
        {
            lock (gate)
            {
                expectedChunks = Math.Max(expectedChunks, e.TotalChunks);
                generationComplete = true;
                System.Diagnostics.Debug.WriteLine($"[StoryAutoplayPrefetch] Voice generation complete for story chunk {response.StoryPartIndex}: {e.TotalChunks} TTS chunks, {e.TotalDurationMs:0}ms.");
                TryComplete();
            }
        }

        void OnGenerationError(object? sender, GenerationErrorEventArgs e)
        {
            completion.TrySetException(e.Exception ?? new InvalidOperationException(e.ErrorMessage));
        }

        CancellationTokenRegistration registration = default;

        try
        {
            registration = cancellationToken.Register(() =>
            {
                generator.CancelGeneration();
                completion.TrySetCanceled(cancellationToken);
            });

            generator.AudioChunkReady += OnAudioChunkReady;
            generator.GenerationComplete += OnGenerationComplete;
            generator.GenerationError += OnGenerationError;

            await generator.GenerateStreamingAsync(CreateVoiceSynthesisRequest(response), cancellationToken).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            generator.AudioChunkReady -= OnAudioChunkReady;
            generator.GenerationComplete -= OnGenerationComplete;
            generator.GenerationError -= OnGenerationError;
            registration.Dispose();
        }
    }

    private async Task PlayStoryVoiceBatchAsync(List<VoiceSynthesisResult> voiceChunks, StarInteractionResponse response, CancellationToken cancellationToken)
    {
        if (voiceChunks.Count == 0) return;

        _speechPipeline.NotifyStarSpeechStarted();
        try
        {
            for (int i = 0; i < voiceChunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VoiceSynthesisResult chunk = voiceChunks[i];
                OnUiThread(() => Status = $"Playing story chunk {response.StoryPartIndex + 1} ({i + 1}/{voiceChunks.Count}). Prefetching the next chunk when available.");

                CancellationToken playbackInterruptionToken = _interruptionManager.StartSpeechPlayback();
                CancellationTokenSource? linkedCts = null;
                try
                {
                    CancellationToken effectiveToken = cancellationToken;
                    if (playbackInterruptionToken != CancellationToken.None)
                    {
                        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, playbackInterruptionToken);
                        effectiveToken = linkedCts.Token;
                    }

                    await _playbackEndpoint.PlayWavAsync(chunk, effectiveToken).ConfigureAwait(false);
                }
                finally
                {
                    linkedCts?.Dispose();
                    _interruptionManager.SpeechPlaybackCompleted();
                }

                if (i + 1 < voiceChunks.Count)
                {
                    await Task.Delay(35, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _speechPipeline.NotifyStarSpeechCompleted();
        }
    }

    private VoiceSynthesisRequest CreateVoiceSynthesisRequest(StarInteractionResponse response)
    {
        PrepareVoiceCloneReferenceForTts(response);

        return new VoiceSynthesisRequest
        {
            SessionId = DefaultSessionId,
            StarId = response.StarId,
            // Preserve the local story section's speech parts. These are generated by the AI
            // or by SplitStoryChunkForSpeech and should be sent directly as TTS chunks.
            Text = response.SpokenText,
            TextParts = response.SpokenParts
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList(),
            SourceResponse = response,
            VoiceSettings = CreateStoryVoiceSettingsFromResponse(response)
        };
    }

    private void PrepareVoiceCloneReferenceForTts(StarInteractionResponse response)
    {
        if (response == null || response.VoiceDirective == null)
        {
            return;
        }

        string configured = response.VoiceDirective.VoiceCloneAudioFile ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        string fullPath = ResolveVoiceCloneAudioPath(configured);
        if (!string.IsNullOrWhiteSpace(fullPath))
        {
            response.VoiceDirective.VoiceCloneAudioFile = fullPath;
            System.Diagnostics.Debug.WriteLine("[VoiceClone] Resolved reference audio path for " + (response.StarId ?? "star") + ": " + fullPath);
        }
    }

    private string ResolveVoiceCloneAudioPath(string configuredPath)
    {
        string value = (configuredPath ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        value = Environment.ExpandEnvironmentVariables(value);
        try
        {
            if (Path.IsPathFullyQualified(value))
            {
                return Path.GetFullPath(value);
            }
        }
        catch
        {
            return value;
        }

        string fileName = Path.GetFileName(value);
        List<string> candidates = new List<string>();

        void AddCandidate(string candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !candidates.Any(x => string.Equals(x, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(candidate);
            }
        }

        string baseDir = AppContext.BaseDirectory;
        AddCandidate(Path.Combine(baseDir, value));
        AddCandidate(Path.Combine(baseDir, "Content", "VoiceClones", fileName));
        AddCandidate(Path.Combine(baseDir, "Wds.SixStars.Api", "Content", "VoiceClones", fileName));

        DirectoryInfo? current = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && current != null; i++)
        {
            AddCandidate(Path.Combine(current.FullName, "Content", "VoiceClones", fileName));
            AddCandidate(Path.Combine(current.FullName, "src", "Wds.SixStars.Api", "Content", "VoiceClones", fileName));
            current = current.Parent;
        }

        string? found = candidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(found))
        {
            return Path.GetFullPath(found);
        }

        // Return the most likely full path even if the file is missing so the Python side can
        // report the exact missing path instead of silently searching random clone folders.
        return Path.GetFullPath(Path.Combine(baseDir, "Content", "VoiceClones", fileName));
    }

    private StarVoiceSettings CreateStoryVoiceSettingsFromResponse(StarInteractionResponse response)
    {
        string strongestEmotion = response.EmotionScores
            .OrderByDescending(x => x.Value.Score * x.Value.Confidence)
            .Select(x => x.Key)
            .FirstOrDefault() ?? "neutral";

        double intensity = response.EmotionScores.Count == 0
            ? 0.5
            : Math.Min(1.0, Math.Max(0.0, response.EmotionScores.Max(x => x.Value.Score) / 100.0));

        VoiceDirective directive = response.VoiceDirective ?? VoiceDirective.Empty();
        string primaryEmotion = string.IsNullOrWhiteSpace(directive.PrimaryEmotion) ? strongestEmotion : directive.PrimaryEmotion;
        double directiveIntensity = directive.EmotionIntensity <= 0 ? intensity : Math.Min(1.0, Math.Max(0.0, directive.EmotionIntensity));

        StarVoiceSettings settings = new StarVoiceSettings
        {
            StarId = response.StarId ?? string.Empty,
            VoiceId = string.IsNullOrWhiteSpace(directive.VoiceId) ? (response.StarId ?? string.Empty) : (directive.VoiceId ?? string.Empty),
            Style = primaryEmotion ?? "neutral",
            PrimaryEmotion = primaryEmotion ?? "neutral",
            EmotionIntensity = directiveIntensity,
            VoiceDesignPrompt = directive.VoiceDesignPrompt ?? string.Empty,
            VoiceCloneAudioFile = ResolveVoiceCloneAudioPath(directive.VoiceCloneAudioFile ?? string.Empty),
            VoiceCloneRefText = directive.VoiceCloneRefText ?? string.Empty,
            XVectorOnly = directive.XVectorOnly,
            DeliveryInstructions = directive.DeliveryInstructions ?? string.Empty,
            Pace = string.IsNullOrWhiteSpace(directive.Pace) ? "normal" : directive.Pace,
            PitchDescription = string.IsNullOrWhiteSpace(directive.Pitch) ? "natural" : directive.Pitch,
            Volume = string.IsNullOrWhiteSpace(directive.Volume) ? "normal" : directive.Volume,
            Prosody = directive.Prosody ?? string.Empty,
            Language = VoiceLanguageNormalizer.Normalize(directive.Language, "English")
        };
        settings.AudioTags.AddRange(directive.AudioTags ?? Enumerable.Empty<string>());
        settings.Avoid.AddRange(directive.Avoid ?? Enumerable.Empty<string>());

        if (string.IsNullOrWhiteSpace(settings.DeliveryInstructions))
        {
            settings.DeliveryInstructions = "Speak in character with " + primaryEmotion + " feeling. Keep the delivery natural and emotionally grounded.";
        }

        return settings;
    }

    private async Task PlayElusiaaCloneWavAsync()
    {
        try
        {
            string fileName = "elusiaa_clone.wav";
            string appBaseDirectory = AppContext.BaseDirectory;
            string currentDirectory = Directory.GetCurrentDirectory();

            string[] candidatePaths = new[]
            {
                Path.Combine(appBaseDirectory, "Content", "VoiceClones", fileName),
                Path.Combine(currentDirectory, "Content", "VoiceClones", fileName),
                Path.Combine(currentDirectory, "src", "Wds.SixStars.Api", "Content", "VoiceClones", fileName),
                Path.GetFullPath(Path.Combine(appBaseDirectory, "..", "..", "..", "..", "Wds.SixStars.Api", "Content", "VoiceClones", fileName))
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

            string? wavPath = candidatePaths.FirstOrDefault(File.Exists);
            if (wavPath == null)
            {
                Status = "Elusiaa clone WAV was not found. Put elusiaa_clone.wav in src\\Wds.SixStars.Api\\Content\\VoiceClones.";
                DebugJson = JsonSerializer.Serialize(new
                {
                    error = "elusiaa_clone.wav not found",
                    appBaseDirectory,
                    currentDirectory,
                    candidatePaths
                }, _jsonOptions);
                return;
            }

            byte[] wavBytes = await File.ReadAllBytesAsync(wavPath).ConfigureAwait(false);
            if (wavBytes.Length == 0)
            {
                Status = "Elusiaa clone WAV exists but is empty: " + wavPath;
                DebugJson = JsonSerializer.Serialize(new
                {
                    error = "elusiaa_clone.wav exists but is empty",
                    wavPath,
                    appBaseDirectory,
                    currentDirectory
                }, _jsonOptions);
                return;
            }

            OnUiThread(() =>
            {
                Status = "Playing local Elusiaa clone WAV through NAudio: " + wavPath;
                DebugJson = JsonSerializer.Serialize(new
                {
                    action = "play_local_elusiaa_clone_wav",
                    wavPath,
                    bytes = wavBytes.Length,
                    appBaseDirectory,
                    currentDirectory,
                    note = "This bypasses LLM, Qwen, WebSocket TTS, HTTP TTS, and generated WAV saving."
                }, _jsonOptions);
            });

            VoiceSynthesisResult testVoice = new VoiceSynthesisResult
            {
                StarId = "Elusiaa",
                VoiceId = "elusiaa_clone_file_test",
                Instructions = "Local playback test for src\\Wds.SixStars.Api\\Content\\VoiceClones\\elusiaa_clone.wav",
                WavAudio = wavBytes,
                MimeType = "audio/wav",
                GeneratedAt = DateTimeOffset.UtcNow,
                Source = "local-elusiaa-clone-wav"
            };

            await new NaudioWavePlaybackEndpoint(SelectedAudioBackend).PlayWavAsync(testVoice, CancellationToken.None).ConfigureAwait(false);
            OnUiThread(() => Status = "Local Elusiaa clone WAV playback completed: " + Path.GetFileName(wavPath));
        }
        catch (Exception ex)
        {
            OnUiThread(() =>
            {
                Status = "Local Elusiaa clone WAV playback error: " + ex.Message;
                DebugJson = ex.ToString();
            });
        }
    }

    private async Task ProcessTranscriptFromSpeechPipelineAsync(SpeechTranscript transcript)
    {
        try
        {
            if (_activeStoryAutoContinue && IsStoryInterruptCommand(transcript.Text))
            {
                InterruptStory("Voice command interrupted the story.");
                OnUiThread(() =>
                {
                    LastTranscript = transcript.Text;
                    SpeechStatus = "Story interrupted by voice command.";
                    Status = "Interrupted active story.";
                });
                return;
            }

            if (_activeStoryAutoContinue)
            {
                InterruptStory("Incoming speech command interrupted the story.", cancelRuntime: false);
            }

            UpdateSpeechOptions();
            IsProcessingRuntime = true;
            CancellationToken token = _operationController.BeginNewOperation("speech transcript to selected star");
            await _speechCoordinator.ProcessTranscriptAsync(transcript, token);
        }
        catch (OperationCanceledException)
        {
            OnUiThread(() => SpeechStatus = "Speech processing cancelled.");
        }
        catch (Exception ex)
        {
            OnUiThread(() =>
            {
                SpeechStatus = "Speech processing error.";
                DebugJson = ex.ToString();
            });
        }
    }

    private async Task StartListeningAsync()
    {
        try
        {
            UpdateSpeechOptions();
            SelectedRequestType = RequestType.Chat;
            _voiceCaptureSettings.RequireHotwordWhenIdle = false;
            await _speechPipeline.StartListeningAsync(CancellationToken.None);
            await _microphoneSpeechSource.StartAsync(CancellationToken.None);
            VoiceInputStatus = SpeechRequireWakeWord
                ? "Always-listening VAD is on. Whisper will transcribe utterances, then text wake-name filtering is applied."
                : "Always-listening VAD is on. Any detected utterance will be transcribed and sent to the selected Star.";
        }
        catch (Exception ex)
        {
            SpeechStatus = "Could not start microphone listening: " + ex.Message;
            DebugJson = ex.ToString();
        }
    }

    private async Task StopListeningAsync()
    {
        await _microphoneSpeechSource.StopAsync(CancellationToken.None);
        await _speechPipeline.StopListeningAsync(CancellationToken.None);
        OnUiThread(() =>
        {
            IncomingVoiceLevelPercent = 0;
            VoiceInputStatus = "Voice input stopped.";
        });
    }

    private async Task CancelListeningWithoutSubmittingAsync()
    {
        try
        {
            await _microphoneSpeechSource.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _speechPipeline.CancelListeningAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Speech] Cancel listening cleanup failed: " + ex.Message);
        }
    }

    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            await SubmitManualRecordingAsync().ConfigureAwait(false);
            return;
        }

        await StartManualRecordingAsync(loopContinuation: false).ConfigureAwait(false);
    }

    private async Task StartManualRecordingAsync(bool loopContinuation)
    {
        try
        {
            if (!loopContinuation)
            {
                _operationController.BeginNewOperation("manual speech recording");
                _voiceConversationLoopActive = true;
                _savedRequireWakeWordBeforeManualRecording = SpeechRequireWakeWord;
                _savedSilenceEndMs = _voiceCaptureSettings.SilenceEndMs;
            }

            SpeechRequireWakeWord = false;
            SelectedRequestType = RequestType.Chat;
            _voiceCaptureSettings.RequireHotwordWhenIdle = false;
            _voiceCaptureSettings.SilenceEndMs = 60 * 60 * 1000; // Push-to-talk: keep audio until Done/Submit.
            UpdateSpeechOptions();
            _speechPipeline.EnterConversationMode(SelectedStarId);

            await _speechPipeline.StartListeningAsync(_operationController.CurrentToken).ConfigureAwait(false);
            await _microphoneSpeechSource.StartAsync(_operationController.CurrentToken).ConfigureAwait(false);

            OnUiThread(() =>
            {
                IsRecording = true;
                IsProcessingRuntime = false;
                RecordButtonText = "Done / Submit Recording (Win+Ctrl+E)";
                CancelStopButtonText = "Cancel";
                SpeechStatus = (loopContinuation ? "Reply finished. Listening again for " : "Recording voice for ") + SelectedStarId + ". Press Done/Submit when finished.";
                Status = "Recording voice input for " + SelectedStarId + ".";
            });
        }
        catch (Exception ex)
        {
            OnUiThread(() =>
            {
                IsRecording = false;
                RecordButtonText = "Start Recording (Win+Ctrl+E)";
                CancelStopButtonText = "Cancel";
                SpeechStatus = "Could not start recording: " + ex.Message;
                DebugJson = ex.ToString();
            });
        }
    }

    private async Task SubmitManualRecordingAsync()
    {
        try
        {
            OnUiThread(() =>
            {
                IsRecording = false;
                IsProcessingRuntime = true;
                RecordButtonText = "Start Recording (Win+Ctrl+E)";
                CancelStopButtonText = "Stop";
                SpeechStatus = "Submitting recorded voice to Whisper, then LLM, then TTS.";
                Status = "Processing voice input for " + SelectedStarId + ".";
            });

            _voiceCaptureSettings.SilenceEndMs = _savedSilenceEndMs;
            _voiceCaptureSettings.RequireHotwordWhenIdle = false;
            UpdateSpeechOptions();

            await _microphoneSpeechSource.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _speechPipeline.StopListeningAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            OnUiThread(() => SpeechStatus = "Recorded voice submission cancelled.");
        }
        catch (Exception ex)
        {
            OnUiThread(() =>
            {
                IsProcessingRuntime = false;
                SpeechStatus = "Could not submit recording: " + ex.Message;
                DebugJson = ex.ToString();
            });
        }
    }

    public void CancelAllRuntimeOperations(string reason)
    {
        StopActiveStoryState();
        _voiceConversationLoopActive = false;
        _voiceCaptureSettings.SilenceEndMs = _savedSilenceEndMs;
        _voiceCaptureSettings.RequireHotwordWhenIdle = _savedRequireWakeWordBeforeManualRecording;
        SpeechRequireWakeWord = _savedRequireWakeWordBeforeManualRecording;
        _operationController.CancelAllRuntimeOperations(reason);
    }

    private async Task ClearSelectedStarHistoryAsync()
    {
        string starId = ResolveDefaultStarId(SelectedStarId);
        try
        {
            CancelAllRuntimeOperations("Clearing selected Star history.");
            StarState state = await _runtime.ResetStarMemoryAsync(DefaultSessionId, starId, CancellationToken.None).ConfigureAwait(false);
            _stateChangeTracker.Reset(starId);
            PublishMobileBridgeStateEventFromDesktop("StarHistoryCleared", state, "Elusiaa saw the desktop clear history for " + starId + ".");
            OnUiThread(() =>
            {
                _isLoadingSelectedStarState = true;
                try
                {
                    SelectedStarIsMet = state.IsMet;
                    SelectedStarIsAlly = state.IsAlly;
                }
                finally
                {
                    _isLoadingSelectedStarState = false;
                }
                StarOutput = string.Empty;
                LastTranscript = string.Empty;
                Status = "Cleared local conversation history and relationship state for " + starId + ".";
                DebugJson = JsonSerializer.Serialize(new { action = "clear_star_history", starId, state }, _jsonOptions);
            });
        }
        catch (Exception ex)
        {
            OnUiThread(() =>
            {
                Status = "Could not clear history for " + starId + ": " + ex.Message;
                DebugJson = ex.ToString();
            });
        }
    }


    private async Task ClearAllStarDataAsync()
    {
        try
        {
            CancelAllRuntimeOperations("Clearing all Star data.");
            List<object> cleared = new List<object>();
            foreach (string star in StarIds.ToList())
            {
                StarState state = await _runtime.ResetStarMemoryAsync(DefaultSessionId, star, CancellationToken.None).ConfigureAwait(false);
                // Also clear the Android dev bridge session so both front ends refresh to the same clean local state.
                await _runtime.ResetStarMemoryAsync("android-phone-dev", star, CancellationToken.None).ConfigureAwait(false);
                _stateChangeTracker.Reset(star);
                cleared.Add(new { starId = star, state.IsMet, state.IsAlly });
                PublishMobileBridgeStateEventFromDesktop("StarHistoryCleared", state, "Auerlis watched the desktop clear all local data for " + star + ".");
            }

            StarState selectedState = await _runtime.GetStarStateAsync(DefaultSessionId, ResolveDefaultStarId(SelectedStarId), CancellationToken.None).ConfigureAwait(false);
            OnUiThread(() =>
            {
                _isLoadingSelectedStarState = true;
                try
                {
                    SelectedStarIsMet = selectedState.IsMet;
                    SelectedStarIsAlly = selectedState.IsAlly;
                }
                finally
                {
                    _isLoadingSelectedStarState = false;
                }
                StarOutput = string.Empty;
                LastTranscript = string.Empty;
                PlayerText = string.Empty;
                StorySuggestion = string.Empty;
                Status = "Cleared all local Star data for desktop and Android dev sessions.";
                DebugJson = JsonSerializer.Serialize(new { action = "clear_all_star_data", cleared }, _jsonOptions);
            });
        }
        catch (Exception ex)
        {
            OnUiThread(() =>
            {
                Status = "Could not clear all Star data: " + ex.Message;
                DebugJson = ex.ToString();
            });
        }
    }

    private async Task SimulateSpeechAsync()
    {
        UpdateSpeechOptions();
        SpeechTranscript transcript = new SpeechTranscript
        {
            SegmentId = "typed-simulated-transcript-" + Guid.NewGuid().ToString("N"),
            Text = PlayerText,
            Confidence = 1.0,
            Language = "en",
            Source = "typed-simulated-transcript",
            ListenMode = "Conversation",
            SuggestedStarId = SelectedStarId,
            WakeWordDetected = true,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow
        };

        PlayerText = string.Empty;
        SpeechStatus = "Simulating recognized transcript from current Player Text. The text box has been cleared.";
        await ProcessTranscriptFromSpeechPipelineAsync(transcript).ConfigureAwait(false);
    }

    private async Task StartScreenAwarenessAsync()
    {
        ScreenStatus = "Starting screen awareness...";
        await _screenAwarenessLoop.StartAsync(default);
    }

    private async Task StopScreenAwarenessAsync()
    {
        await _screenAwarenessLoop.StopAsync(default);
    }

    private async Task CaptureScreenNowAsync()
    {
        try
        {
            ScreenStatus = "Capturing screen now...";
            ScreenCaptureSummary summary = await _screenAwarenessLoop.CaptureOnceAsync(default);
            DebugJson = JsonSerializer.Serialize(new { screenCapture = summary }, _jsonOptions);
        }
        catch (Exception ex)
        {
            ScreenStatus = "Screen capture error: " + ex.Message;
            DebugJson = ex.ToString();
        }
    }

    private async Task QuickSendAsync(RequestType requestType, string text)
    {
        SelectedRequestType = requestType;
        PlayerText = text;
        await SendAsync();
    }

    private async Task SendMessageAsync()
    {
        SelectedRequestType = RequestType.Chat;
        if (string.IsNullOrWhiteSpace(PlayerText))
        {
            Status = "Type a message first, or use Tell Me A Story for automatic story generation.";
            return;
        }

        await SendAsync();
    }

    private async Task TellStoryAsync()
    {
        StopActiveStoryState();
        SelectedRequestType = RequestType.Story;
        await SendAsync();
    }

    private StoryOptions CreateStoryOptions(bool continuation = false)
    {
        // Continuation is now local-only: once the LLM returns storyMarkdown, the desktop host
        // reads Chunk 1+ directly from the cached markdown instead of calling the LLM again.
        StoryOptions options = new StoryOptions
        {
            Length = "flexible_random_2_to_35_minute_complete_story",
            Tone = "comforting, adventurous, cave-travel friendly",
            CanonStrictness = "high",
            UseUnlockedSecrets = true,
            AllowPoetry = true,
            AskFollowupAfter = false,
            TargetMinutes = 0,
            TargetChunkCount = 0,
            MaxSentencesPerChunk = 2,
            PlayerSuggestion = StorySuggestion ?? string.Empty,
            ContinueActiveStory = false,
            ActiveStoryId = string.Empty,
            ActiveStoryTitle = string.Empty,
            ActiveStoryMarkdown = string.Empty,
            ActiveStorySummary = string.Empty,
            StoryProgressSoFar = string.Empty,
            LastStorySpokenText = string.Empty,
            NextStoryChunkMarkdown = string.Empty,
            NextStoryPartIndex = 0
        };

        return options;
    }


    private void ResetLlmDebugLog(StarInteractionRequest request)
    {
        _llmDebugLog.Clear();
        AppendLlmDebugLog("================ SIX STARS AI TURN ================\n"
            + "Time: " + DateTimeOffset.Now.ToString("O") + "\n"
            + "Star: " + request.StarId + "\n"
            + "RequestType: " + request.RequestType + "\n"
            + "PlayerText: " + request.PlayerText + "\n"
            + "StorySuggestion: " + (request.StoryOptions?.PlayerSuggestion ?? string.Empty) + "\n"
            + "===================================================\n");
    }

    private void AppendLlmDebugLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        OnUiThread(() =>
        {
            _llmDebugLog.AppendLine(text.TrimEnd());
            _llmDebugLog.AppendLine();

            const int maxChars = 240000;
            if (_llmDebugLog.Length > maxChars)
            {
                _llmDebugLog.Remove(0, _llmDebugLog.Length - maxChars);
                _llmDebugLog.Insert(0, "[debug log truncated to latest " + maxChars + " chars]\n\n");
            }

            DebugJson = _llmDebugLog.ToString();
        });
    }

    private async Task SendAsync(bool automaticStoryContinuation = false)
    {
        try
        {
            if (!automaticStoryContinuation && _activeStoryAutoContinue)
            {
                if (IsStoryInterruptCommand(PlayerText))
                {
                    InterruptStory("Typed command interrupted the story.");
                    return;
                }

                InterruptStory("New typed command interrupted the active story.", cancelRuntime: false);
            }

            // Check if using real LLM or mock
            bool usingRealLlm = !string.IsNullOrWhiteSpace(_appConfig.LLM.EndpointUrl) || 
                               !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIX_STARS_LLM_ENDPOINT"));

            if (usingRealLlm)
            {
                Status = "✓ Sending request to REAL AI LLM...";
            }
            else
            {
                Status = "⚠ Using MOCK data (no LLM endpoint configured)...";
            }

            string playerTextForRequest = PlayerText ?? string.Empty;
            StoryOptions storyOptionsForRequest = CreateStoryOptions(automaticStoryContinuation);
            if (!automaticStoryContinuation && SelectedRequestType != RequestType.Story && string.IsNullOrWhiteSpace(playerTextForRequest))
            {
                Status = "Type a message first, or choose Tell Me A Story so the selected Star can choose the story automatically.";
                return;
            }

            StarInteractionRequest request = new StarInteractionRequest
            {
                SessionId = DefaultSessionId,
                StarId = SelectedStarId,
                Speaker = "LastCat",
                RequestType = SelectedRequestType,
                PlayerText = playerTextForRequest,
                GameContext = CreateGameContext("Desktop harness text input used"),
                StoryOptions = storyOptionsForRequest
            };

            if (!automaticStoryContinuation)
            {
                PlayerText = string.Empty;
                StorySuggestion = string.Empty;
            }

            ResetLlmDebugLog(request);

            if (usingRealLlm)
            {
                int timeoutSeconds = GetIntSetting(Environment.GetEnvironmentVariable("SIX_STARS_LLM_TIMEOUT_SECONDS"), _appConfig.LLM.TimeoutSeconds);
                Status = $"⏳ Waiting for REAL LLM response. Local models can take a while after generation hits 100%; timeout is {timeoutSeconds}s...";
            }

            IsProcessingRuntime = true;
            CancellationToken runtimeToken = _operationController.BeginNewOperation("typed message to " + request.StarId);
            StarInteractionResponse response = await _runtime.ProcessAsync(request, runtimeToken);

            // Check if response came from real LLM or mock
            bool usedRealLlm = !response.Debug.RiskFlags.Contains("llm_primary_failed");
            bool isFromMock = response.Debug.Notes.Any(n => n.Contains("MOCK"));

            // Track state changes
            _stateChangeTracker.RecordStateChange(
                response.StarId,
                response.StateDeltas.Heart,
                response.StateDeltas.Trust,
                response.StateDeltas.Romance,
                response.StateDeltas.BenaveilInfluence,
                response.StateDeltas.EmotionalConnectionPercent);

            TrackStoryFromResponse(response);
            StarOutput = FormatStarOutput(response);
            LastEmotionVectorsText = FormatEmotionVectorsForResponse(response);
            TopAffectedEmotionsText = FormatTopAffectedEmotions(response.TopAffectedEmotions);
            DebugJson = _llmDebugLog.ToString()
                + "\n================ PARSED STAR RESPONSE / UNITY PAYLOAD ================\n"
                + CreateEnhancedDebugOutput(request, response);

            // Update status with clear indication of LLM vs Mock
            string dataSource = usedRealLlm ? "✓ REAL LLM" : (isFromMock ? "⚠ MOCK DATA" : "? UNKNOWN");
            Status = $"{dataSource} Response. Heart Δ {response.StateDeltas.Heart}, Trust Δ {response.StateDeltas.Trust}, Romance Δ {response.StateDeltas.Romance}, Benaveil Δ {response.StateDeltas.BenaveilInfluence}, Connection Δ {response.StateDeltas.EmotionalConnectionPercent:0.00}. Generating voice...";

            System.Diagnostics.Debug.WriteLine("\n=== DETERMINISTIC STATE CHANGE ===");
            System.Diagnostics.Debug.WriteLine($"Data Source: {(usedRealLlm ? "REAL LLM" : "MOCK DATA")}");
            System.Diagnostics.Debug.WriteLine($"Star: {response.StarId}");
            System.Diagnostics.Debug.WriteLine($"Player Input: {request.PlayerText}");
            System.Diagnostics.Debug.WriteLine($"Heart Delta: {response.StateDeltas.Heart} (Suggested: {response.SuggestedStateDeltas.Heart})");
            System.Diagnostics.Debug.WriteLine($"Trust Delta: {response.StateDeltas.Trust} (Suggested: {response.SuggestedStateDeltas.Trust})");
            System.Diagnostics.Debug.WriteLine($"Romance Delta: {response.StateDeltas.Romance} (Suggested: {response.SuggestedStateDeltas.Romance})");
            System.Diagnostics.Debug.WriteLine($"Benaveil Delta: {response.StateDeltas.BenaveilInfluence} (Suggested: {response.SuggestedStateDeltas.BenaveilInfluence})");
            System.Diagnostics.Debug.WriteLine($"Emotional Connection Delta: {response.StateDeltas.EmotionalConnectionPercent:0.00} (Suggested: {response.SuggestedStateDeltas.EmotionalConnectionPercent:0.00}); Before={response.EmotionalConnectionPercentBefore:0.0}%, After={response.EmotionalConnectionPercentAfter:0.0}%, GirlfriendReady={response.IsGirlfriendReadyAfter}");
            System.Diagnostics.Debug.WriteLine($"Girlfriend Readiness: {response.GirlfriendReadinessSummary}");
            System.Diagnostics.Debug.WriteLine($"Foreground EmotionalEffectVector: {response.EmotionalEffectVector}");
            System.Diagnostics.Debug.WriteLine($"Emotions Evaluated: {response.EmotionScores.Count}");
            foreach (var emotion in response.EmotionScores)
            {
                System.Diagnostics.Debug.WriteLine($"  {emotion.Key}: {emotion.Value.Score} (confidence: {emotion.Value.Confidence})");
            }
            System.Diagnostics.Debug.WriteLine("\n" + _stateChangeTracker.GetCombinedSummary());
            System.Diagnostics.Debug.WriteLine("================================\n");

            await SpeakStarResponseAsync(response, runtimeToken);
        }
        catch (OperationCanceledException)
        {
            IsProcessingRuntime = false;
            Status = "LLM request cancelled.";
            DebugJson = _llmDebugLog.ToString() + "\n================ CANCELLED ================\nRuntime operation was cancelled.";
        }
        catch (Exception ex)
        {
            IsProcessingRuntime = false;
            Status = "✗ Error communicating with LLM.";
            StarOutput = ex.Message;
            DebugJson = _llmDebugLog.ToString()
                + "\n================ LLM ERROR ================\n"
                + ex.ToString();
            System.Diagnostics.Debug.WriteLine("\n=== LLM ERROR ===");
            System.Diagnostics.Debug.WriteLine(ex.ToString());
            System.Diagnostics.Debug.WriteLine("================\n");
        }
    }

    private async Task<bool> TryContinueActiveStoryAsync(CancellationToken cancellationToken)
    {
        if (!_activeStoryAutoContinue || _suppressStoryContinuationOnce || string.IsNullOrWhiteSpace(_activeStoryMarkdown))
        {
            _suppressStoryContinuationOnce = false;
            return false;
        }

        if (!_storySpeechAdapter.TryGetNextNonEmptyStoryChunkFromMarkdown(_activeStoryMarkdown, _nextStoryPartIndex, out int actualChunkIndex, out string nextChunk))
        {
            OnUiThread(() =>
            {
                _activeStoryAutoContinue = false;
                SelectedRequestType = RequestType.Chat;
                Status = "Story finished: " + (string.IsNullOrWhiteSpace(_activeStoryTitle) ? "active story" : _activeStoryTitle);
            });
            return false;
        }

        if (actualChunkIndex != _nextStoryPartIndex)
        {
            System.Diagnostics.Debug.WriteLine($"[StoryAutoplay] Skipped empty story chunks from {_nextStoryPartIndex} to {actualChunkIndex - 1}; continuing with chunk {actualChunkIndex}.");
        }

        try
        {
            StarInteractionResponse response = CreateLocalStoryContinuationResponse(nextChunk, actualChunkIndex);
            TrackStoryFromResponse(response);
            OnUiThread(() =>
            {
                StarOutput = FormatStarOutput(response);
                Status = "Auto-playing cached story chunk " + (response.StoryPartIndex + 1) + " without another LLM request.";
                DebugJson = _llmDebugLog.ToString()
                    + "\n================ LOCAL STORY AUTOPLAY ================\n"
                    + JsonSerializer.Serialize(new
                    {
                        response.StarId,
                        response.StoryTitle,
                        response.StoryId,
                        response.StoryPartIndex,
                        response.StoryIsFinished,
                        spokenParts = response.SpokenParts.Count,
                        note = "This story continuation was read from cached storyMarkdown. No continuation LLM request was sent."
                    }, _jsonOptions);
            });

            await SpeakStarResponseAsync(response, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private StarInteractionResponse CreateLocalStoryContinuationResponse(string markdownChunk, int chunkIndex)
    {
        StarInteractionResponse source = new StarInteractionResponse
        {
            StarId = string.IsNullOrWhiteSpace(_activeStoryStarId) ? SelectedStarId : _activeStoryStarId,
            StoryId = _activeStoryId,
            StoryTitle = _activeStoryTitle,
            StoryKind = _activeStoryKind,
            StoryMarkdown = _activeStoryMarkdown,
            StorySummary = _activeStorySummary,
            VoiceDirective = CloneVoiceDirective(_activeStoryVoiceDirective)
        };
        return _storySpeechAdapter.CreateLocalStoryChunkResponse(source, markdownChunk, chunkIndex);
    }

    private static string[] SplitStoryChunkForSpeech(string chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk)) return Array.Empty<string>();
        string normalized = Regex.Replace(chunk.Replace("\r\n", "\n").Trim(), @"\n{3,}", "\n\n");
        string[] paragraphParts = normalized
            .Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        if (paragraphParts.Length > 1) return paragraphParts;

        string[] sentences = Regex.Split(normalized, @"(?<=[.!?…])\s+")
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        if (sentences.Length <= 2) return new[] { normalized };

        return sentences
            .Select((sentence, index) => new { sentence, index })
            .GroupBy(x => x.index / 2)
            .Select(g => string.Join(" ", g.Select(x => x.sentence)).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
    }

    private static VoiceDirective CloneVoiceDirective(VoiceDirective? source)
    {
        if (source == null) return VoiceDirective.Empty();
        return new VoiceDirective
        {
            VoiceId = source.VoiceId ?? string.Empty,
            VoiceDesignPrompt = source.VoiceDesignPrompt ?? string.Empty,
            VoiceCloneAudioFile = source.VoiceCloneAudioFile ?? string.Empty,
            VoiceCloneRefText = source.VoiceCloneRefText ?? string.Empty,
            XVectorOnly = source.XVectorOnly,
            DeliveryInstructions = source.DeliveryInstructions ?? string.Empty,
            PrimaryEmotion = source.PrimaryEmotion ?? "neutral",
            EmotionIntensity = source.EmotionIntensity,
            Pace = source.Pace ?? "normal",
            Pitch = source.Pitch ?? "natural",
            Volume = source.Volume ?? "normal",
            Prosody = source.Prosody ?? string.Empty,
            Language = source.Language ?? "Auto",
            AudioTags = source.AudioTags == null ? new System.Collections.Generic.List<string>() : source.AudioTags.ToList(),
            Avoid = source.Avoid == null ? new System.Collections.Generic.List<string>() : source.Avoid.ToList()
        };
    }

    private static bool TryGetNextNonEmptyStoryChunkFromMarkdown(string? markdown, int startChunkIndex, out int chunkIndex, out string chunk)
    {
        chunkIndex = Math.Max(0, startChunkIndex);
        chunk = string.Empty;
        if (string.IsNullOrWhiteSpace(markdown)) return false;

        int wanted = Math.Max(0, startChunkIndex);
        string text = markdown.Replace("\r\n", "\n");

        MatchCollection matches = Regex.Matches(
            text,
            @"(?ims)^\s*#{1,6}\s*Chunk\s+(?<index>\d+)\b[^\n]*\n(?<body>.*?)(?=^\s*---+\s*$|^\s*#{1,6}\s*Chunk\s+\d+\b|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        if (matches.Count > 0)
        {
            foreach (Match match in matches)
            {
                if (!int.TryParse(match.Groups["index"].Value, out int index) || index < wanted)
                {
                    continue;
                }

                string cleaned = CleanStoryChunkForPrompt(match.Groups["body"].Value);
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    continue;
                }

                chunkIndex = index;
                chunk = cleaned;
                return true;
            }

            return false;
        }

        if (!Regex.IsMatch(text, @"(?m)^\s*---+\s*$"))
        {
            return false;
        }

        string[] pieces = Regex.Split(text, @"(?m)^\s*---+\s*$")
            .Select(CleanStoryChunkForPrompt)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if (wanted >= pieces.Length) return false;

        chunkIndex = wanted;
        chunk = pieces[wanted];
        return true;
    }

    private static string ExtractStoryChunkFromMarkdown(string? markdown, int chunkIndex)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        int wanted = Math.Max(0, chunkIndex);
        string text = markdown.Replace("\r\n", "\n");

        MatchCollection matches = Regex.Matches(
            text,
            @"(?ims)^\s*#{1,6}\s*Chunk\s+(?<index>\d+)\b[^\n]*\n(?<body>.*?)(?=^\s*---+\s*$|^\s*#{1,6}\s*Chunk\s+\d+\b|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            if (int.TryParse(match.Groups["index"].Value, out int index) && index == wanted)
            {
                return CleanStoryChunkForPrompt(match.Groups["body"].Value);
            }
        }

        if (!Regex.IsMatch(text, @"(?m)^\s*---+\s*$"))
        {
            return string.Empty;
        }

        string[] pieces = Regex.Split(text, @"(?m)^\s*---+\s*$")
            .Select(CleanStoryChunkForPrompt)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        if (pieces.Length == 0) return string.Empty;
        return wanted < pieces.Length ? pieces[wanted] : string.Empty;
    }

    private static string CleanStoryChunkForPrompt(string? markdownChunk)
    {
        if (string.IsNullOrWhiteSpace(markdownChunk)) return string.Empty;
        string text = markdownChunk.Replace("\r\n", "\n").Trim();
        text = Regex.Replace(text, @"(?im)^\s*#{1,6}\s*Chunk\s+\d+\b[^\n]*$", string.Empty);
        text = Regex.Replace(text, @"(?im)^\s*#\s+.+$", string.Empty);
        text = Regex.Replace(text, @"(?m)^\s*---+\s*$", string.Empty);
        text = Regex.Replace(text, @"(?m)\s+---+\s*$", string.Empty);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private void TrackStoryFromResponse(StarInteractionResponse response)
    {
        if (response == null) return;
        bool hasStory = !string.IsNullOrWhiteSpace(response.StoryMarkdown)
            || !string.IsNullOrWhiteSpace(response.StoryTitle)
            || response.StoryPartIndex > 0;
        if (!hasStory) return;

        _activeStoryStarId = response.StarId;
        if (!string.IsNullOrWhiteSpace(response.StoryId)) _activeStoryId = response.StoryId;
        if (!string.IsNullOrWhiteSpace(response.StoryTitle)) _activeStoryTitle = response.StoryTitle ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(response.StoryMarkdown)) _activeStoryMarkdown = response.StoryMarkdown;
        if (!string.IsNullOrWhiteSpace(response.StorySummary)) _activeStorySummary = response.StorySummary;
        if (response.StoryKind != StoryKind.None) _activeStoryKind = response.StoryKind;
        if (response.VoiceDirective != null) _activeStoryVoiceDirective = CloneVoiceDirective(response.VoiceDirective);
        if (response.StoryPartIndex >= 0) _nextStoryPartIndex = Math.Max(_nextStoryPartIndex + 1, response.StoryPartIndex + 1);
        bool hasNextCachedChunk = _storySpeechAdapter.TryGetNextNonEmptyStoryChunkFromMarkdown(_activeStoryMarkdown, _nextStoryPartIndex, out int nextNonEmptyIndex, out _);
        if (hasNextCachedChunk && nextNonEmptyIndex != _nextStoryPartIndex)
        {
            System.Diagnostics.Debug.WriteLine($"[StoryAutoplay] Next cached story passage is chunk {nextNonEmptyIndex}; chunk {_nextStoryPartIndex} or later empty chunks will be skipped.");
        }
        _activeStoryAutoContinue = hasNextCachedChunk || (!response.StoryIsFinished && !string.IsNullOrWhiteSpace(_activeStoryMarkdown));

        _lastStorySpokenText = response.SpokenText ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(_lastStorySpokenText))
        {
            if (_activeStoryProgress.Length > 0) _activeStoryProgress.AppendLine().AppendLine();
            _activeStoryProgress.Append(_lastStorySpokenText.Trim());
            const int maxProgressChars = 12000;
            if (_activeStoryProgress.Length > maxProgressChars)
            {
                _activeStoryProgress.Remove(0, _activeStoryProgress.Length - maxProgressChars);
                _activeStoryProgress.Insert(0, "[earlier spoken story progress omitted]\n");
            }
        }

        if (response.StoryIsFinished && !_activeStoryAutoContinue)
        {
            SelectedRequestType = RequestType.Chat;
            Status = "Story finished: " + _activeStoryTitle;
        }
    }

    private string FormatStarOutput(StarInteractionResponse response)
    {
        if (response == null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(response.StoryMarkdown))
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# " + (string.IsNullOrWhiteSpace(response.StoryTitle) ? "Story" : response.StoryTitle));
            if (!string.IsNullOrWhiteSpace(response.StorySummary))
            {
                sb.AppendLine();
                sb.AppendLine("> " + response.StorySummary.Replace("\r", " ").Replace("\n", " "));
            }
            sb.AppendLine();
            sb.AppendLine(response.StoryMarkdown.Trim());
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("Currently speaking:");
            sb.AppendLine(response.SpokenText);
            return sb.ToString();
        }

        return response.SpokenText;
    }

    private void InterruptStory(string reason, bool cancelRuntime = true)
    {
        bool hadStory = _activeStoryAutoContinue || !string.IsNullOrWhiteSpace(_activeStoryMarkdown);
        StopActiveStoryState();
        _suppressStoryContinuationOnce = true;
        if (cancelRuntime)
        {
            _operationController.CancelAllRuntimeOperations(reason);
        }
        OnUiThread(() =>
        {
            Status = hadStory ? "Story interrupted: " + reason : "No active story to interrupt.";
            SpeechStatus = "Story autoplay stopped.";
        });
    }

    private void StopActiveStoryState()
    {
        _activeStoryAutoContinue = false;
        _activeStoryStarId = string.Empty;
        _activeStoryId = string.Empty;
        _activeStoryTitle = string.Empty;
        _activeStoryMarkdown = string.Empty;
        _activeStorySummary = string.Empty;
        _activeStoryKind = StoryKind.None;
        _activeStoryVoiceDirective = VoiceDirective.Empty();
        _activeStoryProgress.Clear();
        _lastStorySpokenText = string.Empty;
        _nextStoryPartIndex = 0;
        SelectedRequestType = RequestType.Chat;
    }

    private static bool IsStoryInterruptCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.ToLowerInvariant();
        return value.Contains("interrupt the story")
            || value.Contains("stop the story")
            || value.Contains("cancel the story")
            || value.Contains("end the story")
            || value.Contains("pause the story")
            || value.Contains("interrupt story")
            || value.Contains("stop story")
            || value.Contains("cancel story");
    }

    private void UpdateSpeechOptions()
    {
        if (_speechOptions == null)
        {
            return;
        }

        _speechOptions.DefaultStarId = SelectedStarId;
        _speechOptions.RequireWakeWord = SpeechRequireWakeWord;
        // Do not use OpenWakeWord/audio hotword gating. The mic path always records by amplitude VAD,
        // sends stopped utterances to Whisper, then SpeechToStarCoordinator may apply text wake-name filtering.
        _voiceCaptureSettings.RequireHotwordWhenIdle = false;
        _speechPipeline.PreferredStarId = SelectedStarId;
        _speechPipeline.WakeAliases = CreateDefaultWakeAliases();
        _speechOptions.WakeAliases = CreateDefaultWakeAliases();
        _speechOptions.RequestType = SelectedRequestType;
        _speechOptions.GameContext = CreateGameContext("Voice input captured or simulated");
        _speechOptions.StoryOptions = CreateStoryOptions();
    }



    private async Task RefreshSelectedStarStateForUiAsync(string reason)
    {
        try
        {
            string starId = ResolveDefaultStarId(SelectedStarId);
            StarState state = await _runtime.GetStarStateAsync(DefaultSessionId, starId, CancellationToken.None);
            string todayStateText = await ResolveTodayStarStateTextForUiAsync(starId, state, CancellationToken.None).ConfigureAwait(false);
            OnUiThread(() =>
            {
                _isLoadingSelectedStarState = true;
                try
                {
                    SelectedStarIsMet = state.IsMet;
                    SelectedStarIsAlly = state.IsAlly;
                }
                finally
                {
                    _isLoadingSelectedStarState = false;
                }
                TopAffectedEmotionsText = FormatTopAffectedEmotions(state.LastTopAffectedEmotions);
                LastEmotionVectorsText = FormatEmotionVectors(state.LastEmotionalEffectVector, state.LastNegativeEmotionVector);
                TodayStarStateText = todayStateText;
                Status = starId + " state loaded (met=" + state.IsMet + ", ally=" + state.IsAlly + ", Heart " + state.Heart + ", Trust " + state.Trust + ", Romance " + state.Romance + ", Connection " + state.EmotionalConnectionPercent.ToString("0.0") + "%, Ready " + state.IsGirlfriendReady + ").";
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Memory] Could not refresh selected star state after " + reason + ": " + ex.Message);
        }
    }

    private async Task SetSelectedStarMetAsync(bool isMet)
    {
        try
        {
            string starId = ResolveDefaultStarId(SelectedStarId);
            StarState state = await _runtime.SetStarMetAsync(DefaultSessionId, starId, isMet, CancellationToken.None);
            PublishMobileBridgeStateEventFromDesktop("StarStateChanged", state, starId + " met flag changed from the desktop UI.");
            OnUiThread(() =>
            {
                _isLoadingSelectedStarState = true;
                try
                {
                    SelectedStarIsMet = state.IsMet;
                    SelectedStarIsAlly = state.IsAlly;
                }
                finally
                {
                    _isLoadingSelectedStarState = false;
                }
                Status = starId + " met flag set to " + state.IsMet + ", ally=" + state.IsAlly + ". This is now saved in the root state file.";
                DebugJson = JsonSerializer.Serialize(new { action = "set_star_met", starId, isMet = state.IsMet, isAlly = state.IsAlly, state }, _jsonOptions);
            });
        }
        catch (Exception ex)
        {
            OnUiThread(() =>
            {
                Status = "Could not update met flag: " + ex.Message;
                DebugJson = ex.ToString();
            });
        }
    }

    private async Task SetSelectedStarAllyAsync(bool isAlly)
    {
        try
        {
            string starId = ResolveDefaultStarId(SelectedStarId);
            StarState state = await _runtime.SetStarWorldStateAsync(new StarWorldStateUpdateRequest
            {
                SessionId = DefaultSessionId,
                StarId = starId,
                IsAlly = isAlly,
                IsMet = isAlly ? true : (bool?)null,
                Reason = "desktop UI checkbox"
            }, CancellationToken.None);
            PublishMobileBridgeStateEventFromDesktop("StarStateChanged", state, starId + " ally flag changed from the desktop UI.");
            OnUiThread(() =>
            {
                _isLoadingSelectedStarState = true;
                try
                {
                    SelectedStarIsMet = state.IsMet;
                    SelectedStarIsAlly = state.IsAlly;
                }
                finally
                {
                    _isLoadingSelectedStarState = false;
                }
                Status = starId + " ally flag set to " + state.IsAlly + ", met=" + state.IsMet + ". Connection " + state.EmotionalConnectionPercent.ToString("0.0") + "%, girlfriend ready " + state.IsGirlfriendReady + ". This is now saved in the root state file.";
                DebugJson = JsonSerializer.Serialize(new { action = "set_star_ally", starId, isMet = state.IsMet, isAlly = state.IsAlly, state }, _jsonOptions);
            });
        }
        catch (Exception ex)
        {
            OnUiThread(() =>
            {
                Status = "Could not update ally flag: " + ex.Message;
                DebugJson = ex.ToString();
            });
        }
    }

    private GameContext CreateGameContext(string recentEvent)
    {
        GameContext context = new GameContext
        {
            Area = Area,
            Quest = Quest,
            BattleState = "Safe",
            RomanceMode = RomanceMode,
            BenaveilPressureLevel = 0
        };
        context.EquippedStars.Add(SelectedStarId);
        context.RecentEvents.Add(recentEvent);
        context.RecentEvents.Add("Speech API is separated from UI so Unity can push PCM into the same pipeline later");
        context.RecentEvents.Add("Screen awareness stores summaries every five minutes so the selected Star can comment on recent work context");
        context.Flags["llmEndpointConfigured"] = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIX_STARS_LLM_ENDPOINT")) ? "false" : "true";
        context.Flags["qwenTtsEndpointConfigured"] = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIX_STARS_TTS_ENDPOINT")) ? "false" : "true";
        context.Flags["whisperEndpointConfigured"] = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIX_STARS_WHISPER_ENDPOINT")) ? "false" : "true";
        context.Flags["screenEndpointConfigured"] = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIX_STARS_SCREEN_LLM_ENDPOINT")) ? "false" : "true";
        context.Flags["selectedStarIsMet"] = SelectedStarIsMet ? "true" : "false";
        context.Flags["selectedStarIsAlly"] = SelectedStarIsAlly ? "true" : "false";
        context.Flags["emotionalConnectionSkillLevel"] = "interested_private_potential_partner";
        return context;
    }

    private static WakeAliasConfig CreateDefaultWakeAliases()
    {
        return new WakeAliasConfig
        {
            EquippedStars = { "Elusiaa", "Kaelia", "Maera", "Khavia", "Auerlis", "Nyxari" },
            WakeAliases =
            {
                { "Elusiaa", new System.Collections.Generic.List<string> { "elusiaa", "elusia", "light star", "angel bearer of light" } },
                { "Kaelia", new System.Collections.Generic.List<string> { "kaelia", "kaylia", "void star" } },
                { "Maera", new System.Collections.Generic.List<string> { "maera", "blood star" } },
                { "Khavia", new System.Collections.Generic.List<string> { "khavia", "water star" } },
                { "Auerlis", new System.Collections.Generic.List<string> { "auerlis", "aurelis", "fire star" } },
                { "Nyxari", new System.Collections.Generic.List<string> { "nyxari", "shadow star" } }
            }
        };
    }

    private static string ResolveDefaultStarId(string? value)
    {
        string normalized = (value ?? "Elusiaa").Trim();
        return normalized.ToLowerInvariant() switch
        {
            "elusiaa" or "elusia" => "Elusiaa",
            "kaelia" or "kaylia" => "Kaelia",
            "maera" => "Maera",
            "khavia" => "Khavia",
            "auerlis" or "aurelis" => "Auerlis",
            "nyxari" => "Nyxari",
            _ => "Elusiaa"
        };
    }

    private static string NormalizeAudioBackend(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "wasapi" => "wasapi",
            "windows wasapi" => "wasapi",
            "directsound" => "directsound",
            "direct sound" => "directsound",
            "ds" => "directsound",
            "waveout" => "waveout",
            "wave out" => "waveout",
            "waveoutevent" => "waveout",
            "auto" => "auto",
            _ => "directsound"
        };
    }

    private static bool GetBooleanSetting(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return defaultValue;
    }

    private static int GetIntSettingAllowZero(string? value, int defaultValue)
    {
        if (int.TryParse(value, out int parsed) && parsed >= 0)
        {
            return parsed;
        }

        return defaultValue;
    }

    private static int GetIntSetting(string? value, int defaultValue)
    {
        if (int.TryParse(value, out int parsed) && parsed > 0)
        {
            return parsed;
        }

        return defaultValue;
    }

    private static double GetDoubleSetting(string? value, double defaultValue)
    {
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static bool IsTruthy(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }

    private static AppConfiguration LoadConfiguration()
    {
        try
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string configPath = Path.Combine(baseDirectory, "appsettings.json");
            string localConfigPath = Path.Combine(baseDirectory, "appsettings.Local.json");

            JsonSerializerOptions jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };

            JsonNode? mergedRoot = null;

            if (File.Exists(configPath))
            {
                mergedRoot = JsonNode.Parse(File.ReadAllText(configPath));
                System.Diagnostics.Debug.WriteLine($"[Config] Loaded base configuration from {configPath}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Config] appsettings.json not found at {configPath}. Using defaults/local overrides.");
            }

            if (File.Exists(localConfigPath))
            {
                JsonNode? localRoot = JsonNode.Parse(File.ReadAllText(localConfigPath));
                mergedRoot = MergeJsonNodes(mergedRoot, localRoot);
                System.Diagnostics.Debug.WriteLine($"[Config] Loaded local configuration override from {localConfigPath}");
            }

            if (mergedRoot is null)
            {
                return new AppConfiguration();
            }

            AppConfiguration? config = mergedRoot.Deserialize<AppConfiguration>(jsonOptions);
            return config ?? new AppConfiguration();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Config] Error loading configuration: {ex.Message}");
            return new AppConfiguration();
        }
    }

    private static JsonNode? MergeJsonNodes(JsonNode? baseNode, JsonNode? overrideNode)
    {
        if (overrideNode is null)
        {
            return baseNode?.DeepClone();
        }

        if (baseNode is null)
        {
            return overrideNode.DeepClone();
        }

        if (baseNode is JsonObject baseObject && overrideNode is JsonObject overrideObject)
        {
            JsonObject mergedObject = (JsonObject)baseObject.DeepClone();
            foreach (KeyValuePair<string, JsonNode?> property in overrideObject)
            {
                JsonNode? existingValue = mergedObject[property.Key];
                mergedObject[property.Key] = MergeJsonNodes(existingValue, property.Value);
            }

            return mergedObject;
        }

        return overrideNode.DeepClone();
    }

    private string CreateEnhancedDebugOutput(object request, StarInteractionResponse response)
    {
        var debugData = new
        {
            timestamp = DateTimeOffset.Now,
            request = request,
            response = new
            {
                starId = response.StarId,
                spokenText = response.SpokenText,
                emotionalPurpose = response.EmotionalPurpose,
                emotionalEffectVector = response.EmotionalEffectVector,
                negativeEmotionVector = response.NegativeEmotionVector,
                deterministicEmotionUpdateSummary = response.DeterministicEmotionUpdateSummary,
                acceptedPositiveEmotionSignals = response.AcceptedPositiveEmotionSignals,
                acceptedNegativeEmotionSignals = response.AcceptedNegativeEmotionSignals,
                acceptedRelationshipCombos = response.AcceptedRelationshipCombos,
                topAffectedEmotions = response.TopAffectedEmotions,
                storyKind = response.StoryKind,
                storyTitle = response.StoryTitle,
                storyId = response.StoryId,
                storySummary = response.StorySummary,
                storyIsFinished = response.StoryIsFinished,
                storyPartIndex = response.StoryPartIndex,
                storyMarkdownChars = string.IsNullOrWhiteSpace(response.StoryMarkdown) ? 0 : response.StoryMarkdown.Length,
                voiceDirective = response.VoiceDirective,
                emotionScores = response.EmotionScores,
                suggestedStateDeltas = response.SuggestedStateDeltas,
                actualStateDeltas = response.StateDeltas,
                conversationSummary = response.ConversationSummary,
                changesSummary = response.ChangesSummary,
                importantPlayerQuotes = response.ImportantPlayerQuotes,
                memoryNotes = response.MemoryNotes,
                secretsUnlocked = response.SecretsUnlocked,
                detectedCombos = response.DetectedCombos,
                relationshipEvents = response.RelationshipEvents,
                debug = response.Debug
            },
            sessionTotals = _stateChangeTracker.GetTotals(response.StarId),
            combinedTotals = _stateChangeTracker.GetAllTotals()
        };

        return JsonSerializer.Serialize(debugData, _jsonOptions);
    }

    private static string FormatTopAffectedEmotions(IEnumerable<TopAffectedEmotion>? emotions)
    {
        List<TopAffectedEmotion> list = (emotions ?? Enumerable.Empty<TopAffectedEmotion>())
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.EmotionId))
            .OrderByDescending(x => x.WeightedScore)
            .ThenByDescending(x => x.Score)
            .Take(3)
            .ToList();

        if (list.Count == 0)
        {
            return "Top emotions: none yet.";
        }

        return "Top emotions: " + string.Join(" | ", list.Select(x =>
        {
            string sign = string.Equals(x.Valence, "negative", StringComparison.OrdinalIgnoreCase) ? "-" : "+";
            string accepted = x.AcceptedForStateUpdate ? "*" : string.Empty;
            return x.EmotionId + " " + sign + x.Score.ToString("0") + " @ " + x.Confidence.ToString("0.00") + accepted;
        }));
    }


    private static string FormatEmotionVectorsForResponse(StarInteractionResponse response)
    {
        if (response == null) return "Last emotional vectors: none yet.";
        string text = FormatEmotionVectors(response.EmotionalEffectVector, response.NegativeEmotionVector);
        if (text == "Last emotional vectors: none yet." && !string.IsNullOrWhiteSpace(response.BackgroundAnalysisId))
        {
            return "Last emotional vectors: waiting for background emotion/memory update to settle after voice playback. The panel will fill when the deterministic review finishes.";
        }
        return text;
    }


    private static string FormatEmotionVectors(string? emotionalEffectVector, string? negativeEmotionVector)
    {
        List<string> positive = RelationshipDeltaCalculator.ParseVector(emotionalEffectVector, StarEmotionMatrix.StandardEmotionIds)
            .Where(x => x != null && (x.Confidence > 0 || x.Score > 0))
            .OrderByDescending(x => x.WeightedScore)
            .ThenByDescending(x => x.Score)
            .Take(8)
            .Select(x => "+" + x.Id + " " + x.Score.ToString("0") + " @ " + x.Confidence.ToString("0.00"))
            .ToList();

        List<string> negative = RelationshipDeltaCalculator.ParseVector(negativeEmotionVector, RelationshipDeltaCalculator.NegativeEmotionIds)
            .Where(x => x != null && (x.Confidence > 0 || x.Score > 0))
            .OrderByDescending(x => x.WeightedScore)
            .ThenByDescending(x => x.Score)
            .Take(8)
            .Select(x => "-" + x.Id + " " + x.Score.ToString("0") + " @ " + x.Confidence.ToString("0.00"))
            .ToList();

        if (positive.Count == 0 && negative.Count == 0)
        {
            return "Last emotional vectors: none yet.";
        }

        StringBuilder sb = new StringBuilder();
        sb.Append("Last emotional vectors");
        if (positive.Count > 0)
        {
            sb.AppendLine(":");
            sb.Append("Good: ").Append(string.Join(" | ", positive));
        }
        if (negative.Count > 0)
        {
            if (positive.Count > 0) sb.AppendLine();
            else sb.Append(": ");
            sb.Append("Bad: ").Append(string.Join(" | ", negative));
        }
        if (!string.IsNullOrWhiteSpace(emotionalEffectVector) || !string.IsNullOrWhiteSpace(negativeEmotionVector))
        {
            sb.AppendLine();
            sb.Append("Raw good=").Append(emotionalEffectVector ?? string.Empty).Append(" | bad=").Append(negativeEmotionVector ?? string.Empty);
        }
        return sb.ToString();
    }

    private async Task<string> ResolveTodayStarStateTextForUiAsync(string starId, StarState state, CancellationToken cancellationToken)
    {
        if (state?.TodayState != null && state.TodayState.IsAvailable)
        {
            return FormatTodayStarState(state.TodayState.SlotId, state.TodayState.Summary);
        }

        string[] sessions =
        {
            DefaultSessionId,
            "android-phone-dev",
            "local-dev"
        };

        foreach (string sessionId in sessions.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                StarDailyStateRecord? record = await _runtime.GetCurrentDailyStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                if (record == null || string.IsNullOrWhiteSpace(record.XmlContent)) continue;
                string text = FormatTodayStarState(record.SlotId, record.Summary);
                if (!string.Equals(sessionId, DefaultSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    text += " (loaded from " + sessionId + " fallback daily XML)";
                }
                return text;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DailyState] Could not load UI daily state for " + sessionId + "/" + starId + ": " + ex.Message);
            }
        }

        return "Today: no daily Star state yet.";
    }

    private static string FormatTodayStarState(string? slotId, string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "Today: no daily Star state yet.";
        string slot = string.IsNullOrWhiteSpace(slotId) ? string.Empty : " [" + slotId.Trim() + "]";
        string text = summary.Trim();
        if (text.Length > 220) text = text.Substring(0, 220) + "...";
        return "Today" + slot + ": " + text;
    }

    private static byte[] CopyBytes(byte[]? source)
    {
        if (source == null || source.Length == 0) return Array.Empty<byte>();
        byte[] copy = new byte[source.Length];
        Buffer.BlockCopy(source, 0, copy, 0, source.Length);
        return copy;
    }

    public void Dispose()
    {
        try
        {
            _microphoneSpeechSource.Dispose();
            _screenAwarenessLoop.Dispose();
            StopMobileBridgeServer("App shutting down.");
            _mobileBridgeServer?.Dispose();
            _mobileBridgeRuntimeGate.Dispose();
            _runtime.StopDailyStateBackgroundUpdates();
            _operationController.CancelAllRuntimeOperations("App shutting down.");
            _operationController.Dispose();
            _interruptionManager.Dispose();
            _storyAutoplayPrefetchStreamingGenerator?.Dispose();
            if (_storyAutoplayPrefetchVoiceGenerator is IDisposable disposablePrefetchVoiceGenerator)
            {
                disposablePrefetchVoiceGenerator.Dispose();
            }
        }
        catch
        {
            // Best-effort cleanup on app shutdown.
        }
    }

    private static void OnUiThread(Action action)
    {
        Application? app = Application.Current;
        if (app == null || app.Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        app.Dispatcher.Invoke(action);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
