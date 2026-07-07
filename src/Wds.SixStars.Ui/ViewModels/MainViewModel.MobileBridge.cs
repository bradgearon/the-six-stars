using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Wds.SixStars.Api.Audio;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Mobile;
using Wds.SixStars.Api.Speech;
using Wds.SixStars.Api.Screen;
using Wds.SixStars.Api.Stories;
using Wds.SixStars.Api.Voice;
using Wds.SixStars.Ui.Infrastructure;
using Wds.SixStars.Ui.Networking;
using Wds.SixStars.Api.State;

namespace Wds.SixStars.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private MobileBridgeHttpServer? _mobileBridgeServer;
    private MobileBridgeAudioSocketHost? _mobileBridgeAudioHost;
    private readonly SemaphoreSlim _mobileBridgeRuntimeGate = new SemaphoreSlim(1, 1);
    private readonly object _mobileVoiceStreamGate = new object();
    private CancellationTokenSource? _mobileVoiceStreamCts;
    private MobileVoiceStreamScope? _pendingMobileStoryVoiceStream;
    private Task? _pendingMobileStoryThinkingVoiceTask;
    private long _mobileVoiceStreamOrdinal;
    private string _mobileBridgeHostName = "192.168.1.217";
    private string _mobileBridgePort = "8066";
    private bool _mobileBridgeEnabled;
    private string _mobileBridgeStatus = "Android phone bridge stopped.";
    private string _mobileBridgeLastRequest = "No Android request yet.";
    private string _mobileBridgeVoiceDebugLog = "Elusiaa is listening for Android voice stream events.";

    public ICommand StartMobileBridgeCommand { get; private set; } = null!;
    public ICommand StopMobileBridgeCommand { get; private set; } = null!;

    public string MobileBridgeHostName
    {
        get => _mobileBridgeHostName;
        set
        {
            if (SetProperty(ref _mobileBridgeHostName, value))
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeUrl)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeAudioUrl)));
            }
        }
    }

    public string MobileBridgePort
    {
        get => _mobileBridgePort;
        set
        {
            if (SetProperty(ref _mobileBridgePort, value))
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeUrl)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeAudioUrl)));
            }
        }
    }

    public string MobileBridgeUrl => "http://" + (string.IsNullOrWhiteSpace(MobileBridgeHostName) ? Environment.MachineName : MobileBridgeHostName.Trim()) + ":" + ResolveMobileBridgePort().ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string MobileBridgeAudioUrl => "tcp://" + (string.IsNullOrWhiteSpace(MobileBridgeHostName) ? Environment.MachineName : MobileBridgeHostName.Trim()) + ":" + ResolveMobileBridgeAudioPort().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool MobileBridgeEnabled
    {
        get => _mobileBridgeEnabled;
        set => SetProperty(ref _mobileBridgeEnabled, value);
    }

    public string MobileBridgeStatus
    {
        get => _mobileBridgeStatus;
        set => SetProperty(ref _mobileBridgeStatus, value);
    }

    public string MobileBridgeLastRequest
    {
        get => _mobileBridgeLastRequest;
        set => SetProperty(ref _mobileBridgeLastRequest, value);
    }

    public string MobileBridgeVoiceDebugLog
    {
        get => _mobileBridgeVoiceDebugLog;
        set => SetProperty(ref _mobileBridgeVoiceDebugLog, value);
    }

    private void InitializeMobileBridgeCommandsAndMaybeStart()
    {
        _mobileBridgeHostName = string.IsNullOrWhiteSpace(_appConfig.MobileBridge.HostName) ? "192.168.1.217" : _appConfig.MobileBridge.HostName;
        _mobileBridgePort = (_appConfig.MobileBridge.Port <= 0 ? 8066 : _appConfig.MobileBridge.Port).ToString(System.Globalization.CultureInfo.InvariantCulture);
        StartMobileBridgeCommand = new RelayCommand(_ => StartMobileBridgeServer());
        StopMobileBridgeCommand = new RelayCommand(_ => StopMobileBridgeServer("Stopped by user."));

        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeHostName)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgePort)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeUrl)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeAudioUrl)));

        if (_appConfig.MobileBridge.EnabledOnStartup || IsTruthy(Environment.GetEnvironmentVariable("SIX_STARS_MOBILE_BRIDGE_ENABLED")))
        {
            StartMobileBridgeServer();
        }
    }

    private void StartMobileBridgeServer()
    {
        if (_mobileBridgeServer != null && _mobileBridgeServer.IsRunning)
        {
            MobileBridgeStatus = "Android bridge is already running. HTTP " + MobileBridgeUrl + "; audio " + MobileBridgeAudioUrl + ".";
            return;
        }

        int port = ResolveMobileBridgePort();
        int audioPort = ResolveMobileBridgeAudioPort();
        _mobileBridgeAudioHost?.Dispose();
        _mobileBridgeAudioHost = new MobileBridgeAudioSocketHost();
        _mobileBridgeAudioHost.StatusChanged += (_, message) => OnUiThread(() =>
        {
            MobileBridgeStatus = message + " HTTP " + MobileBridgeUrl + "; audio " + MobileBridgeAudioUrl + ".";
            AppendMobileBridgeVoiceDebug("Audio socket status", message);
        });
        _mobileBridgeAudioHost.Start(audioPort);

        _mobileBridgeServer?.Dispose();
        _mobileBridgeServer = new MobileBridgeHttpServer(
            port,
            ProcessMobileMessageAsync,
            ProcessMobileAudioAsync,
            ProcessMobileTestAudioAsync,
            CreateMobileHealthPayload,
            CreateMobileStarsPayload,
            CreateMobileStatePayloadAsync,
            _mobileBridgeDispatcher);
        _mobileBridgeServer.StatusChanged += (_, message) => OnUiThread(() =>
        {
            MobileBridgeStatus = message + " HTTP " + MobileBridgeUrl + "; audio " + MobileBridgeAudioUrl + ".";
            AppendMobileBridgeVoiceDebug("HTTP bridge status", message);
        });
        _mobileBridgeServer.Start();

        MobileBridgeEnabled = true;
        MobileBridgeStatus = "Android bridge running. HTTP " + MobileBridgeUrl + "; phone audio socket " + MobileBridgeAudioUrl + ". Use the HTTP URL in the Android app.";
    }

    private void StopMobileBridgeServer(string reason)
    {
        try { _mobileBridgeServer?.Dispose(); } catch { }
        try { _mobileBridgeAudioHost?.Dispose(); } catch { }
        _mobileBridgeServer = null;
        _mobileBridgeAudioHost = null;
        MobileBridgeEnabled = false;
        MobileBridgeStatus = reason;
    }

    private int ResolveMobileBridgePort()
    {
        if (int.TryParse(MobileBridgePort, out int port) && port > 0 && port <= 65535)
        {
            return port;
        }
        return 8066;
    }

    private int ResolveMobileBridgeAudioPort()
    {
        int httpPort = ResolveMobileBridgePort();
        return httpPort >= 65535 ? httpPort - 1 : httpPort + 1;
    }

    private object CreateMobileHealthPayload()
    {
        int clients = _mobileBridgeAudioHost?.ClientCount ?? 0;
        return new
        {
            ok = true,
            service = "six-stars-mobile-bridge",
            machineName = Environment.MachineName,
            hostName = MobileBridgeHostName,
            port = ResolveMobileBridgePort(),
            audioPort = ResolveMobileBridgeAudioPort(),
            audioSocketConnected = clients > 0,
            audioClientCount = clients,
            selectedStarId = SelectedStarId,
            streamingTts = _useStreamingTts,
            time = DateTimeOffset.Now
        };
    }

    private object CreateMobileStarsPayload()
    {
        return new
        {
            ok = true,
            stars = StarIds.ToArray(),
            requestTypes = RequestTypes.Select(x => x.ToString()).ToArray()
        };
    }

    private async Task<object> CreateMobileStatePayloadAsync(string sessionId, CancellationToken cancellationToken)
    {
        string safeSessionId = string.IsNullOrWhiteSpace(sessionId) ? "android-phone-dev" : sessionId.Trim();
        List<object> states = new List<object>();
        foreach (string starId in StarIds)
        {
            StarState state = await _runtime.GetStarStateAsync(safeSessionId, starId, cancellationToken).ConfigureAwait(false);
            states.Add(CreateMobileStarStateDto(state));
        }

        return new
        {
            ok = true,
            selectedStarId = SelectedStarId,
            sessionId = safeSessionId,
            stars = states,
            latestEventSequence = _mobileBridgeDispatcher.GetEventsAfter(long.MaxValue).LatestSequence,
            time = DateTimeOffset.Now
        };
    }

    private async Task<MobileBridgeDispatchResult> HandleMobileBridgeCommandAsync(MobileBridgeDispatchCommand command, CancellationToken cancellationToken)
    {
        string commandType = string.IsNullOrWhiteSpace(command.CommandType) ? "RefreshState" : command.CommandType;
        string sessionId = string.IsNullOrWhiteSpace(command.SessionId) ? "android-phone-dev" : command.SessionId;
        string starId = ResolveDefaultStarId(command.StarId);

        switch (commandType)
        {
            case "SelectStar":
            {
                OnUiThread(() =>
                {
                    _suppressMobileBridgeSelectedStarBroadcast = true;
                    try
                    {
                        SelectedStarId = starId;
                    }
                    finally
                    {
                        _suppressMobileBridgeSelectedStarBroadcast = false;
                    }
                });
                StarState state = await _runtime.GetStarStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, "Android selected " + starId + "."));
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, "Elusiaa shifted the phone focus to " + starId + ".");
            }
            case "SetMet":
            {
                bool isMet = command.IsMet ?? true;
                StarState state = await _runtime.SetStarMetAsync(sessionId, starId, isMet, cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, starId + " met=" + state.IsMet + ", ally=" + state.IsAlly + "."));
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, starId + (state.IsMet ? " is now marked met." : " is no longer marked met."));
            }
            case "SetAlly":
            {
                bool isAlly = command.IsAlly ?? true;
                StarState state = await _runtime.SetStarWorldStateAsync(new StarWorldStateUpdateRequest
                {
                    SessionId = sessionId,
                    StarId = starId,
                    IsAlly = isAlly,
                    IsMet = isAlly ? true : (bool?)null,
                    Reason = string.IsNullOrWhiteSpace(command.Reason) ? "Android bridge ally toggle" : command.Reason
                }, cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, starId + " ally=" + state.IsAlly + ", met=" + state.IsMet + "."));
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, starId + (state.IsAlly ? " joined as an ally." : " ally flag was removed."));
            }
            case "BreakupGirlfriend":
            {
                StarState state = await _runtime.SetExclusiveGirlfriendAsync(sessionId, starId, false, string.IsNullOrWhiteSpace(command.Reason) ? "Android prod breakup button" : command.Reason, cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, starId + " girlfriend relationship ended."));
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, starId + " is no longer marked as girlfriend.");
            }

            case "ClearHistory":
            {
                StarState state = await ResetStarEverywhereAsync(starId, sessionId, cancellationToken).ConfigureAwait(false);
                _stateChangeTracker.Reset(starId);
                OnUiThread(() =>
                {
                    ApplyMobileStateToSelectedStarUi(state, "Cleared Android bridge history for " + starId + ".");
                    if (string.Equals(SelectedStarId, starId, StringComparison.OrdinalIgnoreCase))
                    {
                        StarOutput = string.Empty;
                        LastTranscript = string.Empty;
                    }
                });
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, "Khavia washed the Android conversation history clean for " + starId + ".");
            }

            case "ClearAllData":
            {
                StarState selectedState = null!;
                foreach (string star in StarIds.ToList())
                {
                    StarState reset = await ResetStarEverywhereAsync(star, sessionId, cancellationToken).ConfigureAwait(false);
                    _stateChangeTracker.Reset(star);
                    if (string.Equals(star, starId, StringComparison.OrdinalIgnoreCase) || string.Equals(star, SelectedStarId, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedState = reset;
                    }
                    _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
                    {
                        EventType = "StarHistoryCleared",
                        CommandId = command.CommandId,
                        SourceClientId = command.SourceClientId,
                        SessionId = sessionId,
                        StarId = star,
                        SelectedStarId = SelectedStarId,
                        IsMet = reset.IsMet,
                        IsAlly = reset.IsAlly,
                        IsGirlfriend = reset.IsGirlfriend,
                        Heart = reset.Heart,
                        Trust = reset.Trust,
                        Romance = reset.Romance,
                        EmotionalConnectionPercent = reset.EmotionalConnectionPercent,
                        IsGirlfriendReady = reset.IsGirlfriendReady,
                        Message = "Elusiaa helped fully clear local Star data for " + star + " across desktop, Android dev, Android prod, daily JSON/XML cache, relationship scores, and conversation history."
                    });
                }
                if (selectedState == null) selectedState = await _runtime.GetStarStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                OnUiThread(() =>
                {
                    ApplyMobileStateToSelectedStarUi(selectedState, "Cleared all Android bridge Star data.");
                    StarOutput = string.Empty;
                    LastTranscript = string.Empty;
                });
                return PublishMobileCommandResult(command, true, commandType, selectedState.StarId, sessionId, selectedState, "Elusiaa swept every Star memory shelf clean across desktop and Android sessions and refreshed the state.");
            }
            case "SendPhoto":
            {
                MobileBridgeRequest photoRequest = new MobileBridgeRequest
                {
                    SessionId = sessionId,
                    StarId = starId,
                    RequestType = "Photo",
                    PlayerText = string.IsNullOrWhiteSpace(command.PlayerText) ? FirstNonEmptyForMobile(command.PhotoCaption, "I attached a photo. Please look at it and reply directly to what you see.") : command.PlayerText,
                    Area = string.IsNullOrWhiteSpace(command.Area) ? Area : command.Area,
                    Quest = string.IsNullOrWhiteSpace(command.Quest) ? Quest : command.Quest,
                    RomanceMode = command.RomanceMode,
                    SpeakOnDesktop = command.SpeakOnDesktop,
                    ReturnAudio = command.ReturnAudio,
                    ClientName = string.IsNullOrWhiteSpace(command.ClientName) ? "SixStarsMobileAndroid" : command.ClientName,
                    PhotoBase64 = command.PhotoBase64 ?? string.Empty,
                    PhotoMimeType = string.IsNullOrWhiteSpace(command.PhotoMimeType) ? "image/jpeg" : command.PhotoMimeType,
                    PhotoCaption = FirstNonEmptyForMobile(command.PhotoCaption, command.PlayerText)
                };

                MobileBridgeResponse photoResponse = await ProcessMobileConversationAsync(photoRequest, string.Empty, cancellationToken).ConfigureAwait(false);
                StarState state = await _runtime.GetStarStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                MobileBridgeDispatchResult result = CreateConversationDispatchResult(command, "SendPhoto", sessionId, starId, state, photoResponse);
                result.Event = _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
                {
                    EventType = photoResponse.Ok ? "PhotoResponseReady" : "PhotoResponseFailed",
                    CommandId = command.CommandId,
                    SourceClientId = command.SourceClientId,
                    SessionId = sessionId,
                    StarId = starId,
                    SelectedStarId = SelectedStarId,
                    IsMet = state.IsMet,
                    IsAlly = state.IsAlly,
                    IsGirlfriend = state.IsGirlfriend,
                    Heart = state.Heart,
                    Trust = state.Trust,
                    Romance = state.Romance,
                    EmotionalConnectionPercent = state.EmotionalConnectionPercent,
                    IsGirlfriendReady = state.IsGirlfriendReady,
                    Message = photoResponse.Ok ? "The Star answered the Android photo request." : photoResponse.VoiceError,
                    Error = photoResponse.Ok ? string.Empty : photoResponse.VoiceError,
                    SpokenText = photoResponse.SpokenText ?? string.Empty,
                    StoryId = photoResponse.StoryId ?? string.Empty,
                    StoryTitle = photoResponse.StoryTitle ?? string.Empty,
                    StoryMarkdown = photoResponse.StoryMarkdown ?? string.Empty,
                    StorySummary = photoResponse.StorySummary ?? string.Empty,
                    StoryPartIndex = photoResponse.StoryPartIndex,
                    StoryIsFinished = photoResponse.StoryIsFinished,
                    StoryChunkCount = photoResponse.StoryChunkCount,
                    PhotoAttached = photoResponse.PhotoAttached,
                    PhotoSummary = photoResponse.PhotoSummary ?? string.Empty,
                    PhotoError = photoResponse.PhotoError ?? string.Empty,
                    TopAffectedEmotions = photoResponse.TopAffectedEmotions.Count > 0 ? photoResponse.TopAffectedEmotions.ToList() : (state.LastTopAffectedEmotions ?? new List<TopAffectedEmotion>()).ToList(),
                    RecentConversationHistory = CreateMobileRecentConversationHistory(state, 10),
                    EmotionalEffectVector = FirstNonEmptyForMobile(photoResponse.EmotionalEffectVector, state.LastEmotionalEffectVector),
                    NegativeEmotionVector = FirstNonEmptyForMobile(photoResponse.NegativeEmotionVector, state.LastNegativeEmotionVector),
                    LastEmotionVectorsText = FirstNonEmptyForMobile(photoResponse.LastEmotionVectorsText, FormatEmotionVectors(photoResponse.EmotionalEffectVector, photoResponse.NegativeEmotionVector)),
                    InterstarStatusText = InterstarStatusText
                });
                return result;
            }

            case "MaybeStarInitiative":
            {
                MobileBridgeRequest initiativeRequest = new MobileBridgeRequest
                {
                    SessionId = sessionId,
                    StarId = starId,
                    RequestType = "Chat",
                    PlayerText = string.IsNullOrWhiteSpace(command.PlayerText)
                        ? "At-rest companion initiative check: The Last Cat is quiet or just reconnected. If there is something from your daily state, recent emotions, or interstar feedback that you want to say to him, say it naturally now. Do not pretend he asked this. If nothing feels important, give a brief gentle check-in."
                        : command.PlayerText,
                    Area = string.IsNullOrWhiteSpace(command.Area) ? Area : command.Area,
                    Quest = string.IsNullOrWhiteSpace(command.Quest) ? "AndroidProdStarInitiative" : command.Quest,
                    RomanceMode = command.RomanceMode,
                    SpeakOnDesktop = command.SpeakOnDesktop,
                    ReturnAudio = command.ReturnAudio,
                    ClientName = string.IsNullOrWhiteSpace(command.ClientName) ? "SixStarsMobileAndroid-StarInitiative" : command.ClientName
                };

                MobileBridgeResponse initiativeResponse = await ProcessMobileConversationAsync(initiativeRequest, string.Empty, cancellationToken).ConfigureAwait(false);
                StarState state = await _runtime.GetStarStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                MobileBridgeDispatchResult result = CreateConversationDispatchResult(command, "MaybeStarInitiative", sessionId, starId, state, initiativeResponse);
                result.Event = _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
                {
                    EventType = initiativeResponse.Ok ? "StarInitiativeResponseReady" : "StarInitiativeResponseFailed",
                    CommandId = command.CommandId,
                    SourceClientId = command.SourceClientId,
                    SessionId = sessionId,
                    StarId = starId,
                    SelectedStarId = SelectedStarId,
                    IsMet = state.IsMet,
                    IsAlly = state.IsAlly,
                    IsGirlfriend = state.IsGirlfriend,
                    Heart = state.Heart,
                    Trust = state.Trust,
                    Romance = state.Romance,
                    EmotionalConnectionPercent = state.EmotionalConnectionPercent,
                    IsGirlfriendReady = state.IsGirlfriendReady,
                    Message = initiativeResponse.Ok ? starId + " chose to say something at rest." : initiativeResponse.VoiceError,
                    Error = initiativeResponse.Ok ? string.Empty : initiativeResponse.VoiceError,
                    SpokenText = initiativeResponse.SpokenText ?? string.Empty,
                    StoryId = initiativeResponse.StoryId ?? string.Empty,
                    StoryTitle = initiativeResponse.StoryTitle ?? string.Empty,
                    StoryMarkdown = initiativeResponse.StoryMarkdown ?? string.Empty,
                    StorySummary = initiativeResponse.StorySummary ?? string.Empty,
                    StoryPartIndex = initiativeResponse.StoryPartIndex,
                    StoryIsFinished = initiativeResponse.StoryIsFinished,
                    StoryChunkCount = initiativeResponse.StoryChunkCount,
                    TopAffectedEmotions = initiativeResponse.TopAffectedEmotions.Count > 0 ? initiativeResponse.TopAffectedEmotions.ToList() : (state.LastTopAffectedEmotions ?? new List<TopAffectedEmotion>()).ToList(),
                    RecentConversationHistory = CreateMobileRecentConversationHistory(state, 10),
                    EmotionalEffectVector = FirstNonEmptyForMobile(initiativeResponse.EmotionalEffectVector, state.LastEmotionalEffectVector),
                    NegativeEmotionVector = FirstNonEmptyForMobile(initiativeResponse.NegativeEmotionVector, state.LastNegativeEmotionVector),
                    LastEmotionVectorsText = FirstNonEmptyForMobile(initiativeResponse.LastEmotionVectorsText, FormatEmotionVectors(initiativeResponse.EmotionalEffectVector, initiativeResponse.NegativeEmotionVector)),
                    InterstarStatusText = InterstarStatusText
                });
                return result;
            }

            case "RefreshState":
            default:
            {
                StarState state = await _runtime.GetStarStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, "Refreshed Android bridge state for " + starId + "."));
                return PublishMobileCommandResult(command, true, "RefreshState", starId, sessionId, state, "Nyxari checked the current state for " + starId + ".");
            }
        }
    }

    private void ApplyMobileStateToSelectedStarUi(StarState state, string status)
    {
        if (state == null) return;
        if (string.Equals(SelectedStarId, state.StarId, StringComparison.OrdinalIgnoreCase))
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
        }
        MobileBridgeStatus = status;
        if (string.Equals(SelectedStarId, state.StarId, StringComparison.OrdinalIgnoreCase))
        {
            TopAffectedEmotionsText = FormatTopAffectedEmotions(state.LastTopAffectedEmotions);
        }
        AppendMobileBridgeVoiceDebug("Mobile state update", status + " Heart=" + state.Heart + ", Trust=" + state.Trust + ", Romance=" + state.Romance + ", Connection=" + state.EmotionalConnectionPercent.ToString("0.0") + "%, top=" + FormatTopAffectedEmotions(state.LastTopAffectedEmotions));
    }

    private MobileBridgeDispatchResult CreateConversationDispatchResult(MobileBridgeDispatchCommand command, string commandType, string sessionId, string starId, StarState state, MobileBridgeResponse response)
    {
        return new MobileBridgeDispatchResult
        {
            Ok = response.Ok,
            CommandId = command.CommandId,
            CommandType = commandType,
            StarId = starId,
            SelectedStarId = SelectedStarId,
            IsMet = state.IsMet,
            IsAlly = state.IsAlly,
            IsGirlfriend = state.IsGirlfriend,
            State = state,
            Message = response.Ok ? "Conversation response ready." : response.VoiceError,
            Error = response.Ok ? string.Empty : response.VoiceError,
            RequestType = response.RequestType,
            Transcript = response.Transcript,
            SpokenText = response.SpokenText,
            StarOutput = response.StarOutput,
            ConversationSummary = response.ConversationSummary,
            ChangesSummary = response.ChangesSummary,
            VoiceRequested = response.VoiceRequested,
            VoiceSocketConnected = response.VoiceSocketConnected,
            VoiceChunksSent = response.VoiceChunksSent,
            VoiceBytesSent = response.VoiceBytesSent,
            VoiceMessage = response.VoiceMessage,
            VoiceError = response.VoiceError,
            StoryId = response.StoryId,
            StoryTitle = response.StoryTitle,
            StoryMarkdown = response.StoryMarkdown,
            StorySummary = response.StorySummary,
            StoryPartIndex = response.StoryPartIndex,
            StoryIsFinished = response.StoryIsFinished,
            StoryMarkdownReady = response.StoryMarkdownReady,
            StoryAutoplayPlanned = response.StoryAutoplayPlanned,
            StoryChunkCount = response.StoryChunkCount,
            PhotoAttached = response.PhotoAttached,
            PhotoSummary = response.PhotoSummary,
            PhotoError = response.PhotoError,
            DailyStateSlotId = FirstNonEmptyForMobile(response.DailyStateSlotId, state.TodayState?.SlotId),
            DailyStateLocalDate = FirstNonEmptyForMobile(response.DailyStateLocalDate, state.TodayState?.LocalDate),
            DailyStateGeneratedAt = response.DailyStateGeneratedAt ?? state.TodayState?.GeneratedAt,
            DailyStateSummary = FirstNonEmptyForMobile(response.DailyStateSummary, state.TodayState?.Summary),
            DailyStateJson = FirstNonEmptyForMobile(response.DailyStateJson, state.TodayState?.Json, state.TodayState?.Xml),
            DailyStateXml = string.Empty,
            TopAffectedEmotions = (response.TopAffectedEmotions ?? new List<TopAffectedEmotion>()).ToList(),
            RecentConversationHistory = CreateMobileRecentConversationHistory(state, 10),
            EmotionalEffectVector = FirstNonEmptyForMobile(response.EmotionalEffectVector, state.LastEmotionalEffectVector),
            NegativeEmotionVector = FirstNonEmptyForMobile(response.NegativeEmotionVector, state.LastNegativeEmotionVector),
            LastEmotionVectorsText = FirstNonEmptyForMobile(response.LastEmotionVectorsText, FormatEmotionVectors(response.EmotionalEffectVector, response.NegativeEmotionVector)),
            InterstarStatusText = InterstarStatusText
        };
    }

    private MobileBridgeDispatchResult PublishMobileCommandResult(MobileBridgeDispatchCommand command, bool ok, string commandType, string starId, string sessionId, StarState state, string message)
    {
        MobileBridgeDispatchResult result = new MobileBridgeDispatchResult
        {
            Ok = ok,
            CommandId = command.CommandId,
            CommandType = commandType,
            StarId = starId,
            SelectedStarId = SelectedStarId,
            IsMet = state.IsMet,
            IsAlly = state.IsAlly,
            IsGirlfriend = state.IsGirlfriend,
            State = state,
            DailyStateSlotId = state.TodayState?.SlotId ?? string.Empty,
            DailyStateLocalDate = state.TodayState?.LocalDate ?? string.Empty,
            DailyStateGeneratedAt = state.TodayState?.GeneratedAt,
            DailyStateSummary = state.TodayState?.Summary ?? string.Empty,
            DailyStateJson = state.TodayState?.Json ?? state.TodayState?.Xml ?? string.Empty,
            DailyStateXml = string.Empty,
            TopAffectedEmotions = (state.LastTopAffectedEmotions ?? new List<TopAffectedEmotion>()).ToList(),
            RecentConversationHistory = CreateMobileRecentConversationHistory(state, 10),
            EmotionalEffectVector = state.LastEmotionalEffectVector ?? string.Empty,
            NegativeEmotionVector = state.LastNegativeEmotionVector ?? string.Empty,
            LastEmotionVectorsText = FormatEmotionVectors(state.LastEmotionalEffectVector, state.LastNegativeEmotionVector),
                InterstarStatusText = InterstarStatusText,
                Message = message
        };

        result.Event = _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
        {
            EventType = commandType == "SelectStar" ? "SelectedStarChanged" : (commandType == "ClearHistory" || commandType == "ClearAllData") ? "StarHistoryCleared" : "StarStateChanged",
            CommandId = command.CommandId,
            SourceClientId = command.SourceClientId,
            SessionId = sessionId,
            StarId = starId,
            SelectedStarId = SelectedStarId,
            IsMet = state.IsMet,
            IsAlly = state.IsAlly,
            IsGirlfriend = state.IsGirlfriend,
            Heart = state.Heart,
            Trust = state.Trust,
            Romance = state.Romance,
            EmotionalConnectionPercent = state.EmotionalConnectionPercent,
            IsGirlfriendReady = state.IsGirlfriendReady,
            TopAffectedEmotions = (state.LastTopAffectedEmotions ?? new List<TopAffectedEmotion>()).ToList(),
            RecentConversationHistory = CreateMobileRecentConversationHistory(state, 10),
            EmotionalEffectVector = state.LastEmotionalEffectVector ?? string.Empty,
            NegativeEmotionVector = state.LastNegativeEmotionVector ?? string.Empty,
            LastEmotionVectorsText = FormatEmotionVectors(state.LastEmotionalEffectVector, state.LastNegativeEmotionVector),
            InterstarStatusText = InterstarStatusText,
            DailyStateSlotId = state.TodayState?.SlotId ?? string.Empty,
            DailyStateLocalDate = state.TodayState?.LocalDate ?? string.Empty,
            DailyStateGeneratedAt = state.TodayState?.GeneratedAt,
            DailyStateSummary = state.TodayState?.Summary ?? string.Empty,
            DailyStateJson = state.TodayState?.Json ?? state.TodayState?.Xml ?? string.Empty,
            DailyStateXml = string.Empty,
            Message = message
        });
        return result;
    }

    private async Task PublishMobileSelectedStarUpdateAsync(string reason)
    {
        try
        {
            string starId = ResolveDefaultStarId(SelectedStarId);
            StarState state = await _runtime.GetStarStateAsync("android-phone-dev", starId, CancellationToken.None).ConfigureAwait(false);
            _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
            {
                EventType = "SelectedStarChanged",
                SourceClientId = "desktop-ui",
                SessionId = "android-phone-dev",
                StarId = starId,
                SelectedStarId = starId,
                IsMet = state.IsMet,
                IsAlly = state.IsAlly,
                IsGirlfriend = state.IsGirlfriend,
                Heart = state.Heart,
                Trust = state.Trust,
                Romance = state.Romance,
                EmotionalConnectionPercent = state.EmotionalConnectionPercent,
                IsGirlfriendReady = state.IsGirlfriendReady,
                TopAffectedEmotions = (state.LastTopAffectedEmotions ?? new List<TopAffectedEmotion>()).ToList(),
                RecentConversationHistory = CreateMobileRecentConversationHistory(state, 10),
                EmotionalEffectVector = state.LastEmotionalEffectVector ?? string.Empty,
                NegativeEmotionVector = state.LastNegativeEmotionVector ?? string.Empty,
                LastEmotionVectorsText = FormatEmotionVectors(state.LastEmotionalEffectVector, state.LastNegativeEmotionVector),
                DailyStateSlotId = state.TodayState?.SlotId ?? string.Empty,
                DailyStateLocalDate = state.TodayState?.LocalDate ?? string.Empty,
                DailyStateGeneratedAt = state.TodayState?.GeneratedAt,
                DailyStateSummary = state.TodayState?.Summary ?? string.Empty,
                DailyStateJson = state.TodayState?.Json ?? state.TodayState?.Xml ?? string.Empty,
            DailyStateXml = string.Empty,
                Message = "Elusiaa noticed the desktop selected Star changed to " + starId + " (" + reason + ")."
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[MobileBridge] Could not publish selected Star update: " + ex.Message);
        }
    }

    private static List<MobileConversationHistoryItem> CreateMobileRecentConversationHistory(StarState state, int maxItems)
    {
        if (state == null) return new List<MobileConversationHistoryItem>();

        List<ConversationLogEntry> entries = new List<ConversationLogEntry>();
        if (state.MemoryLog != null)
        {
            entries.AddRange(state.MemoryLog.Where(IsUsableMobileConversationTurn));
        }

        if (entries.Count == 0 && state.OldConversationArchives != null)
        {
            entries.AddRange(state.OldConversationArchives
                .Where(x => x != null)
                .OrderByDescending(x => x.EndedAt)
                .SelectMany(x => x.Entries ?? new List<ConversationLogEntry>())
                .Where(IsUsableMobileConversationTurn)
                .OrderByDescending(x => x.CreatedAt));
        }

        return entries
            .OrderByDescending(x => x.CreatedAt)
            .Take(Math.Max(1, maxItems))
            .Select(x => new MobileConversationHistoryItem
            {
                CreatedAt = x.CreatedAt,
                RequestType = x.RequestType ?? string.Empty,
                PlayerText = x.PlayerText ?? string.Empty,
                StarResponse = x.StarResponse ?? string.Empty,
                Summary = x.Summary ?? string.Empty
            })
            .ToList();
    }

    private static List<MobileConversationHistoryItem> CreateMobileRecentConversationHistoryFromResponse(MobileBridgeRequest mobileRequest, StarInteractionRequest runtimeRequest, StarInteractionResponse response, int maxItems)
    {
        List<MobileConversationHistoryItem> items = new List<MobileConversationHistoryItem>();
        if (response != null && !string.IsNullOrWhiteSpace(response.SpokenText))
        {
            string playerText = runtimeRequest?.PlayerText;
            if (string.IsNullOrWhiteSpace(playerText)) playerText = mobileRequest?.PlayerText;
            if (string.IsNullOrWhiteSpace(playerText)) playerText = mobileRequest?.PhotoCaption;

            items.Add(new MobileConversationHistoryItem
            {
                CreatedAt = DateTimeOffset.Now,
                RequestType = (runtimeRequest?.RequestType ?? ParseMobileRequestType(mobileRequest?.RequestType)).ToString(),
                PlayerText = playerText ?? string.Empty,
                StarResponse = response.SpokenText ?? string.Empty,
                Summary = !string.IsNullOrWhiteSpace(response.ConversationSummary)
                    ? response.ConversationSummary
                    : (response.ChangesSummary ?? string.Empty)
            });
        }

        return items
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.StarResponse))
            .OrderByDescending(x => x.CreatedAt)
            .Take(Math.Max(1, maxItems))
            .ToList();
    }

    private static List<MobileConversationHistoryItem> CreateMobileConversationHistoryFromResponse(StarInteractionResponse response, int maxItems)
    {
        if (response == null || string.IsNullOrWhiteSpace(response.SpokenText)) return new List<MobileConversationHistoryItem>();
        return new List<MobileConversationHistoryItem>
        {
            new MobileConversationHistoryItem
            {
                CreatedAt = DateTimeOffset.Now,
                RequestType = string.Empty,
                PlayerText = string.Empty,
                StarResponse = response.SpokenText ?? string.Empty,
                Summary = response.ConversationSummary ?? string.Empty
            }
        }.Take(Math.Max(1, maxItems)).ToList();
    }

    private static bool IsUsableMobileConversationTurn(ConversationLogEntry entry)
    {
        if (entry == null) return false;
        if (string.IsNullOrWhiteSpace(entry.PlayerText)) return false;
        if (string.IsNullOrWhiteSpace(entry.StarResponse)) return false;
        string text = entry.StarResponse;
        return text.IndexOf("thoughts came back in a shape the system could not fully read", StringComparison.OrdinalIgnoreCase) < 0
            && text.IndexOf("answer came back in a strange shape", StringComparison.OrdinalIgnoreCase) < 0
            && !(text.IndexOf("received your words", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("try again", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static object CreateMobileStarStateDto(StarState state)
    {
        return new
        {
            starId = state.StarId,
            isMet = state.IsMet,
            isAlly = state.IsAlly,
            isGirlfriend = state.IsGirlfriend,
            heart = state.Heart,
            trust = state.Trust,
            romance = state.Romance,
            emotionalConnectionPercent = state.EmotionalConnectionPercent,
            isGirlfriendReady = state.IsGirlfriendReady,
            topAffectedEmotions = state.LastTopAffectedEmotions ?? new List<TopAffectedEmotion>(),
            emotionalEffectVector = state.LastEmotionalEffectVector ?? string.Empty,
            negativeEmotionVector = state.LastNegativeEmotionVector ?? string.Empty,
            lastEmotionVectorsText = FormatEmotionVectors(state.LastEmotionalEffectVector, state.LastNegativeEmotionVector),
            recentConversationHistory = CreateMobileRecentConversationHistory(state, 10),
            todayState = state.TodayState,
            dailyStateSlotId = state.TodayState?.SlotId ?? string.Empty,
            dailyStateLocalDate = state.TodayState?.LocalDate ?? string.Empty,
            dailyStateGeneratedAt = state.TodayState?.GeneratedAt,
            dailyStateSummary = state.TodayState?.Summary ?? string.Empty,
            dailyStateJson = state.TodayState?.Json ?? state.TodayState?.Xml ?? string.Empty,
            dailyStateXml = string.Empty,
            interactionCount = state.InteractionCount
        };
    }

    private void PublishMobileBridgeStateEventFromDesktop(string eventType, StarState state, string message)
    {
        if (state == null) return;
        try
        {
            _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
            {
                EventType = string.IsNullOrWhiteSpace(eventType) ? "StarStateChanged" : eventType,
                SourceClientId = "desktop-ui",
                SessionId = DefaultSessionId,
                StarId = state.StarId,
                SelectedStarId = SelectedStarId,
                IsMet = state.IsMet,
                IsAlly = state.IsAlly,
                IsGirlfriend = state.IsGirlfriend,
                Heart = state.Heart,
                Trust = state.Trust,
                Romance = state.Romance,
                EmotionalConnectionPercent = state.EmotionalConnectionPercent,
                IsGirlfriendReady = state.IsGirlfriendReady,
                TopAffectedEmotions = (state.LastTopAffectedEmotions ?? new List<TopAffectedEmotion>()).ToList(),
                RecentConversationHistory = CreateMobileRecentConversationHistory(state, 10),
                EmotionalEffectVector = state.LastEmotionalEffectVector ?? string.Empty,
                NegativeEmotionVector = state.LastNegativeEmotionVector ?? string.Empty,
                LastEmotionVectorsText = FormatEmotionVectors(state.LastEmotionalEffectVector, state.LastNegativeEmotionVector),
                Message = message
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[MobileBridge] Could not publish desktop state event: " + ex.Message);
        }
    }

    private void PublishMobileBridgeTopEmotionUpdate(StarInteractionResponse response, string eventType = "TopAffectedEmotionsChanged")
    {
        if (response == null) return;
        try
        {
            _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
            {
                EventType = string.IsNullOrWhiteSpace(eventType) ? "TopAffectedEmotionsChanged" : eventType,
                SourceClientId = "desktop-runtime",
                SessionId = DefaultSessionId,
                StarId = response.StarId ?? SelectedStarId,
                SelectedStarId = SelectedStarId,
                EmotionalConnectionPercent = response.EmotionalConnectionPercentAfter,
                IsGirlfriendReady = response.IsGirlfriendReadyAfter,
                TopAffectedEmotions = (response.TopAffectedEmotions ?? new List<TopAffectedEmotion>()).ToList(),
                EmotionalEffectVector = response.EmotionalEffectVector ?? string.Empty,
                NegativeEmotionVector = response.NegativeEmotionVector ?? string.Empty,
                LastEmotionVectorsText = FormatEmotionVectors(response.EmotionalEffectVector, response.NegativeEmotionVector),
                Message = FormatTopAffectedEmotions(response.TopAffectedEmotions)
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[MobileBridge] Could not publish top affected emotions update: " + ex.Message);
        }
    }


    private void PublishMobileBridgeInterstarStatus(InterstarUpdateStatusEventArgs args)
    {
        if (args == null) return;
        try
        {
            _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
            {
                EventType = "InterstarUpdate" + (string.IsNullOrWhiteSpace(args.Phase) ? "Status" : args.Phase),
                SourceClientId = "desktop-runtime",
                SessionId = string.IsNullOrWhiteSpace(args.SessionId) ? DefaultSessionId : args.SessionId,
                StarId = string.IsNullOrWhiteSpace(args.TargetStarId) ? args.OriginStarId : args.TargetStarId,
                SelectedStarId = SelectedStarId,
                InterstarStatusText = "Interstar state: " + args.Phase + " — " + args.Message,
                Message = args.Message
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[MobileBridge] Could not publish interstar status update: " + ex.Message);
        }
    }

    private void AppendMobileBridgeVoiceDebug(string title, string details)
    {
        string line = "[" + DateTimeOffset.Now.ToString("HH:mm:ss") + "] Elusiaa whispers: " + title;
        if (!string.IsNullOrWhiteSpace(details))
        {
            line += " — " + details.Replace("\r", " ").Replace("\n", " ");
        }

        string next = line + Environment.NewLine + MobileBridgeVoiceDebugLog;
        const int maxChars = 9000;
        if (next.Length > maxChars) next = next.Substring(0, maxChars);
        MobileBridgeVoiceDebugLog = next;
    }

    private static string Sha256Hex(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes);
        StringBuilder builder = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) builder.Append(b.ToString("x2"));
        return builder.ToString();
    }

    private static string ShortHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return "none";
        return hash.Length <= 12 ? hash : hash.Substring(0, 12);
    }


    private async Task<object> ProcessMobileTestAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_mobileBridgeAudioHost == null || _mobileBridgeAudioHost.ClientCount == 0)
            {
                return new
                {
                    ok = false,
                    error = "No Android phone audio socket is connected. Open the Android app and wait for the phone voice socket to connect first.",
                    audioPort = ResolveMobileBridgeAudioPort(),
                    audioClientCount = 0
                };
            }

            byte[] wav = CreateMobileTestToneWav(sampleRate: 24000, seconds: 1.25, frequencyHz: 660.0);
            string checksum = Sha256Hex(wav);
            await _mobileBridgeAudioHost.SendStatusAsync("Elusiaa is sending the desktop test tone. sha256=" + ShortHash(checksum), cancellationToken).ConfigureAwait(false);
            VoiceSynthesisResult result = new VoiceSynthesisResult
            {
                StarId = SelectedStarId ?? "Elusiaa",
                VoiceId = "android_phone_audio_socket_test",
                Instructions = "Synthetic 24 kHz PCM WAV test tone from desktop to Android phone audio queue.",
                WavAudio = wav,
                MimeType = "audio/wav",
                GeneratedAt = DateTimeOffset.UtcNow,
                DurationMs = 1250,
                Source = "mobile-bridge-test-tone"
            };

            MobileAudioSendResult sent = await _mobileBridgeAudioHost.SendVoiceChunkAsync(result, 0, 1, cancellationToken).ConfigureAwait(false);
            await _mobileBridgeAudioHost.SendStatusAsync("Elusiaa finished the desktop test tone send. sha256=" + ShortHash(checksum) + ", clients=" + sent.SentClients, cancellationToken).ConfigureAwait(false);
            OnUiThread(() =>
            {
                MobileBridgeStatus = "Sent Android phone audio test tone. Clients=" + sent.SentClients + ", bytes=" + wav.Length + ", sha256=" + ShortHash(checksum) + ".";
                AppendMobileBridgeVoiceDebug("Desktop test tone sent", "clients=" + sent.SentClients + ", bytes=" + wav.Length + ", sha256=" + ShortHash(checksum));
            });
            return new
            {
                ok = sent.SentClients > 0,
                message = "Desktop sent a synthetic 24 kHz PCM WAV test tone to the Android voice socket.",
                sent.SentClients,
                sent.ConnectedClients,
                sent.FailedClients,
                wavBytes = wav.Length,
                sha256 = checksum,
                sha256Short = ShortHash(checksum),
                audioPort = ResolveMobileBridgeAudioPort()
            };
        }
        catch (Exception ex)
        {
            OnUiThread(() => MobileBridgeStatus = "Android phone audio test failed: " + ex.Message);
            return new { ok = false, error = ex.Message, type = ex.GetType().Name };
        }
    }

    private static byte[] CreateMobileTestToneWav(int sampleRate, double seconds, double frequencyHz)
    {
        int sampleCount = Math.Max(1, (int)(sampleRate * seconds));
        byte[] pcm = new byte[sampleCount * 2];
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double envelope = Math.Min(1.0, Math.Min(i / (sampleRate * 0.05), (sampleCount - i) / (sampleRate * 0.08)));
            short sample = (short)(Math.Sin(2.0 * Math.PI * frequencyHz * t) * 0.22 * envelope * short.MaxValue);
            pcm[i * 2] = (byte)(sample & 0xff);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
        }

        using MemoryStream ms = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(ms);
        writer.Write(new[] { 'R', 'I', 'F', 'F' }.Select(c => (byte)c).ToArray());
        writer.Write(36 + pcm.Length);
        writer.Write(new[] { 'W', 'A', 'V', 'E' }.Select(c => (byte)c).ToArray());
        writer.Write(new[] { 'f', 'm', 't', ' ' }.Select(c => (byte)c).ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(new[] { 'd', 'a', 't', 'a' }.Select(c => (byte)c).ToArray());
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return ms.ToArray();
    }

    private async Task<MobileBridgeResponse> ProcessMobileMessageAsync(MobileBridgeRequest request, CancellationToken cancellationToken)
    {
        return await ProcessMobileConversationAsync(request, transcript: string.Empty, cancellationToken).ConfigureAwait(false);
    }

    private void PublishMobileTranscriptReady(MobileBridgeRequest request, string transcriptText)
    {
        if (string.IsNullOrWhiteSpace(transcriptText)) return;
        try
        {
            string starId = ResolveDefaultStarId(request.StarId);
            _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
            {
                EventType = "MobileTranscriptReady",
                SourceClientId = "desktop-whisper",
                SessionId = string.IsNullOrWhiteSpace(request.SessionId) ? "android-phone-dev" : request.SessionId,
                StarId = starId,
                SelectedStarId = SelectedStarId,
                Transcript = transcriptText.Trim(),
                Message = "Voice transcript ready for " + starId + "; LLM response is now processing."
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[MobileBridge] Could not publish mobile transcript event: " + ex.Message);
        }
    }

    private async Task<MobileBridgeResponse> ProcessMobileAudioAsync(MobileBridgeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AudioBase64))
        {
            return new MobileBridgeResponse { Ok = false, VoiceError = "No audioBase64 was supplied.", Notes = { "Android did not send audio data." } };
        }

        string transcriptText;
        try
        {
            byte[] wavBytes = Convert.FromBase64String(request.AudioBase64);
            PcmWavData pcm = DecodePcm16WavOrRaw(wavBytes, request.SampleRate, request.Channels, request.BitsPerSample);
            string segmentId = "android-" + Guid.NewGuid().ToString("N");
            IWhisperSpeechStreamer createdStreamer = CreateWhisperStreamer();
            using DisposableWhisperWrapper streamer = new DisposableWhisperWrapper(createdStreamer);

            await streamer.BeginSegmentAsync(segmentId, pcm.Format, new WhisperTranscriptionOptions
            {
                Language = Environment.GetEnvironmentVariable("SIX_STARS_WHISPER_LANGUAGE") ?? _appConfig.Whisper.Language
            }, cancellationToken).ConfigureAwait(false);
            await streamer.PushAudioAsync(segmentId, pcm.Pcm16, 0, pcm.Pcm16.Length, cancellationToken).ConfigureAwait(false);
            SpeechTranscript transcript = await streamer.EndSegmentAsync(segmentId, cancellationToken).ConfigureAwait(false);
            transcriptText = transcript.Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            return new MobileBridgeResponse { Ok = false, VoiceError = "Android audio transcription failed: " + ex.Message };
        }

        if (string.IsNullOrWhiteSpace(transcriptText))
        {
            return new MobileBridgeResponse { Ok = false, Transcript = string.Empty, VoiceError = "Whisper returned an empty transcript." };
        }

        request.PlayerText = transcriptText;
        OnUiThread(() =>
        {
            LastTranscript = transcriptText;
            MobileBridgeStatus = "Android voice transcribed; sending transcript to the Star LLM.";
            AppendMobileBridgeVoiceDebug("Android voice transcript ready", TruncateForUi(transcriptText, 220));
        });
        PublishMobileTranscriptReady(request, transcriptText);
        MobileBridgeResponse response = await ProcessMobileConversationAsync(request, transcriptText, cancellationToken).ConfigureAwait(false);
        response.Transcript = transcriptText;
        return response;
    }

    private async Task<MobileBridgeResponse> ProcessMobileConversationAsync(MobileBridgeRequest mobileRequest, string transcript, CancellationToken cancellationToken)
    {
        await _mobileBridgeRuntimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string starId = ResolveDefaultStarId(mobileRequest.StarId);
            RequestType requestType = ParseMobileRequestType(mobileRequest.RequestType);
            if (HasMobilePhoto(mobileRequest)) requestType = RequestType.Photo;
            bool wantsPhoneAudio = mobileRequest.ReturnAudio;
            DateTimeOffset started = DateTimeOffset.Now;

            OnUiThread(() =>
            {
                MobileBridgeLastRequest = started.ToString("HH:mm:ss") + " " + mobileRequest.ClientName + " -> " + starId + " " + requestType + ": " + TruncateForUi(mobileRequest.PlayerText, 180);
                MobileBridgeStatus = "Android request is using the desktop runtime for " + starId + ".";
                AppendMobileBridgeVoiceDebug("Phone request arrived", starId + " " + requestType + ", phoneAudio=" + wantsPhoneAudio + ", textChars=" + (mobileRequest.PlayerText?.Length ?? 0));
                Status = "Android bridge request received for " + starId + ".";
            });

            StarInteractionRequest request = new StarInteractionRequest
            {
                SessionId = string.IsNullOrWhiteSpace(mobileRequest.SessionId) ? "android-phone-dev" : mobileRequest.SessionId,
                StarId = starId,
                Speaker = "LastCat",
                RequestType = requestType,
                PlayerText = mobileRequest.PlayerText ?? string.Empty,
                AttachedPhoto = new StarPhotoAttachment(),
                GameContext = CreateMobileGameContext(mobileRequest, starId),
                StoryOptions = CreateMobileStoryOptions(mobileRequest, requestType)
            };

            MobileAttachedPhotoResult photoResult = await AddMobileAttachedPhotoContextAsync(request, mobileRequest, cancellationToken).ConfigureAwait(false);

            ResetLlmDebugLog(request);
            CancellationToken runtimeToken = _operationController.BeginNewOperation("android bridge request to " + starId);
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runtimeToken);
            StarInteractionResponse starResponse = await _runtime.ProcessAsync(request, linkedCts.Token).ConfigureAwait(false);
            if (photoResult.Attached)
            {
                starResponse.Debug.Notes.Add("Android photo summary: " + photoResult.Summary);
            }
            else if (!string.IsNullOrWhiteSpace(photoResult.Error))
            {
                starResponse.Debug.Notes.Add("Android photo error: " + photoResult.Error);
            }

            _stateChangeTracker.RecordStateChange(
                starResponse.StarId,
                starResponse.StateDeltas.Heart,
                starResponse.StateDeltas.Trust,
                starResponse.StateDeltas.Romance,
                starResponse.StateDeltas.BenaveilInfluence,
                starResponse.StateDeltas.EmotionalConnectionPercent);
            TrackStoryFromResponse(starResponse);
            PublishMobileStoryEventIfNeeded(starResponse, "StoryInitialResponse", "Initial story response returned to Android.");
            bool storyThinkingPlaceholder = IsMobileStoryThinkingPlaceholder(starResponse);
            MobileVoiceStreamScope? sharedStoryVoiceStream = null;
            if (wantsPhoneAudio && storyThinkingPlaceholder)
            {
                sharedStoryVoiceStream = BeginMobileVoiceStream(starResponse, true, "Android story thinking phrase followed by generated story narration");
                lock (_mobileVoiceStreamGate) _pendingMobileStoryVoiceStream = sharedStoryVoiceStream;
                OnUiThread(() => AppendMobileBridgeVoiceDebug("Story voice stream reserved", "The thinking phrase and generated story will share stream=" + sharedStoryVoiceStream.StreamId + " so Android does not cut a word when the full story arrives."));
            }

            MobileBridgeResponse response = CreateMobileResponse(mobileRequest, request, transcript, starResponse);

            OnUiThread(() => AppendMobileBridgeVoiceDebug("Text response ready", (starResponse.StarId ?? starId) + " spokenChars=" + (response.SpokenText?.Length ?? 0) + ", phoneAudio=" + wantsPhoneAudio));

            OnUiThread(() =>
            {
                StarOutput = FormatStarOutput(starResponse);
                Status = "Android bridge response ready for " + starResponse.StarId + ". Heart Δ " + starResponse.StateDeltas.Heart + ", Trust Δ " + starResponse.StateDeltas.Trust + ", Romance Δ " + starResponse.StateDeltas.Romance + ".";
                DebugJson = _llmDebugLog.ToString()
                    + "\n================ ANDROID BRIDGE STAR RESPONSE ================\n"
                    + JsonSerializer.Serialize(new
                    {
                        mobile = true,
                        response.StarId,
                        response.RequestType,
                        transcriptLength = transcript?.Length ?? 0,
                        spokenChars = response.SpokenText.Length,
                        wantsPhoneAudio,
                        response.VoiceSocketConnected,
                        response.VoiceChunksSent,
                        response.VoiceBytesSent,
                        response.VoiceError,
                        storyMarkdownChars = response.StoryMarkdown?.Length ?? 0,
                        storyChunkCount = response.StoryChunkCount,
                        storyMarkdownReady = response.StoryMarkdownReady,
                        response.PhotoAttached,
                        photoSummaryChars = response.PhotoSummary?.Length ?? 0,
                        response.PhotoError,
                        state = new
                        {
                            response.HeartDelta,
                            response.TrustDelta,
                            response.RomanceDelta,
                            response.BenaveilDelta,
                            response.EmotionalConnectionDelta,
                            response.EmotionalConnectionAfter,
                            response.IsGirlfriendReadyAfter
                        }
                    }, _jsonOptions);
            });

            if (!string.IsNullOrWhiteSpace(starResponse.BackgroundAnalysisId))
            {
                _pendingBackgroundAnalysisId = starResponse.BackgroundAnalysisId;
            }

            if (wantsPhoneAudio)
            {
                response.VoiceSocketConnected = (_mobileBridgeAudioHost?.ClientCount ?? 0) > 0;
                response.VoiceMessage = response.VoiceSocketConnected
                    ? "Text returned immediately. Voice is generating in the desktop app and will stream to the Android phone socket."
                    : "Text returned immediately, but no Android audio socket is connected yet.";

                OnUiThread(() => AppendMobileBridgeVoiceDebug("HTTP response returned to phone", response.VoiceSocketConnected ? "Voice generation is now running in the background." : "No phone audio socket was connected."));

                Task phoneVoiceTask = Task.Run(async () =>
                {
                    try
                    {
                        await SendMobileVoiceAsync(starResponse, response, CancellationToken.None, sharedStoryVoiceStream).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        OnUiThread(() => MobileBridgeStatus = "Android background voice error: " + ex.Message);
                        try { ReleasePendingBackgroundAnalysis("Android background voice failed: " + ex.Message); } catch { }
                    }
                });

                if (sharedStoryVoiceStream != null)
                {
                    lock (_mobileVoiceStreamGate)
                    {
                        if (ReferenceEquals(_pendingMobileStoryVoiceStream, sharedStoryVoiceStream))
                        {
                            _pendingMobileStoryThinkingVoiceTask = phoneVoiceTask;
                        }
                    }
                }
            }
            else if (mobileRequest.SpeakOnDesktop || _appConfig.MobileBridge.SpeakResponsesOnDesktop)
            {
                await SpeakStarResponseAsync(starResponse, linkedCts.Token).ConfigureAwait(false);
            }
            else
            {
                ReleasePendingBackgroundAnalysis("Android bridge response returned without voice playback.");
            }

            response.VoiceSocketConnected = (_mobileBridgeAudioHost?.ClientCount ?? 0) > 0;
            OnUiThread(() =>
            {
                MobileBridgeStatus = wantsPhoneAudio
                    ? "Android text response returned. Phone voice generation continues in the background."
                    : "Android response complete. Voice chunks sent: " + response.VoiceChunksSent + ", bytes: " + response.VoiceBytesSent + ".";
            });
            return response;
        }
        catch (OperationCanceledException)
        {
            return new MobileBridgeResponse { Ok = false, VoiceError = "Android bridge request was cancelled." };
        }
        catch (Exception ex)
        {
            OnUiThread(() => MobileBridgeStatus = "Android bridge error: " + ex.Message);
            return new MobileBridgeResponse { Ok = false, VoiceError = ex.Message, Notes = { ex.GetType().Name } };
        }
        finally
        {
            IsProcessingRuntime = false;
            _mobileBridgeRuntimeGate.Release();
        }
    }

    private GameContext CreateMobileGameContext(MobileBridgeRequest request, string starId)
    {
        GameContext context = new GameContext
        {
            Area = string.IsNullOrWhiteSpace(request.Area) ? Area : request.Area,
            Quest = string.IsNullOrWhiteSpace(request.Quest) ? Quest : request.Quest,
            BattleState = "Safe",
            RomanceMode = request.RomanceMode,
            BenaveilPressureLevel = 0
        };
        context.EquippedStars.Add(starId);
        context.RecentEvents.Add("Android phone bridge conversation request");
        if (!string.IsNullOrWhiteSpace(request.ClientName)) context.RecentEvents.Add("Client: " + request.ClientName);
        context.Flags["mobileBridge"] = "true";
        context.Flags["phoneAudioSocketConnected"] = ((_mobileBridgeAudioHost?.ClientCount ?? 0) > 0).ToString().ToLowerInvariant();
        context.Flags["speakOnDesktop"] = request.SpeakOnDesktop.ToString().ToLowerInvariant();
        context.Flags["returnAudioToPhone"] = request.ReturnAudio.ToString().ToLowerInvariant();
        if (IsMobileStarInitiativeRequest(request))
        {
            context.Flags["starInitiative"] = "true";
            context.Flags["initiativeReason"] = string.IsNullOrWhiteSpace(request.Quest) ? "mobile at-rest initiative" : request.Quest;
            context.RecentEvents.Add("At-rest Star initiative check from Android/desktop bridge; the Star may speak naturally if she has something to say.");
        }
        if (HasMobilePhoto(request))
        {
            context.Flags["androidPhotoAttached"] = "true";
            context.Flags["androidPhotoMimeType"] = string.IsNullOrWhiteSpace(request.PhotoMimeType) ? "image/jpeg" : request.PhotoMimeType;
            context.RecentEvents.Add("Android attached a phone camera/gallery image and asked the selected Star to answer it directly.");
            if (!string.IsNullOrWhiteSpace(request.PhotoCaption)) context.RecentEvents.Add("Android photo caption: " + TruncateForUi(request.PhotoCaption, 240));
        }
        return context;
    }


    private static bool IsMobileStarInitiativeRequest(MobileBridgeRequest request)
    {
        if (request == null) return false;
        return ContainsIgnoreCase(request.ClientName, "StarInitiative")
            || ContainsIgnoreCase(request.Quest, "StarInitiative");
    }

    private static bool ContainsIgnoreCase(string? value, string fragment)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }


    private async Task<MobileAttachedPhotoResult> AddMobileAttachedPhotoContextAsync(StarInteractionRequest request, MobileBridgeRequest mobileRequest, CancellationToken cancellationToken)
    {
        if (request == null || mobileRequest == null || !HasMobilePhoto(mobileRequest))
        {
            return MobileAttachedPhotoResult.Empty;
        }

        try
        {
            byte[] imageBytes = DecodeMobilePhotoBase64(mobileRequest.PhotoBase64);
            if (imageBytes.Length == 0)
            {
                request.GameContext.RecentEvents.Add("Android tried to attach a photo, but the image data was empty.");
                return new MobileAttachedPhotoResult(false, string.Empty, "Attached photo was empty.");
            }

            if (imageBytes.Length > 12 * 1024 * 1024)
            {
                request.GameContext.RecentEvents.Add("Android attached a photo, but it was too large for the mobile bridge vision path.");
                return new MobileAttachedPhotoResult(false, string.Empty, "Attached photo was larger than 12 MB.");
            }

            string mimeType = string.IsNullOrWhiteSpace(mobileRequest.PhotoMimeType) ? "image/jpeg" : mobileRequest.PhotoMimeType.Trim();
            ScreenImageCapture capture = new ScreenImageCapture
            {
                CapturedAt = DateTimeOffset.UtcNow,
                ImageBytes = imageBytes,
                MimeType = mimeType,
                ImageReference = "android-phone-photo-" + Guid.NewGuid().ToString("N"),
                ActiveApp = "SixStarsMobileAndroid",
                ActiveWindowTitle = "Android phone photo attachment",
                ActiveProcessName = "com.wds.sixstarsmobile"
            };

            ScreenCaptureSummary summary;
            try
            {
                IScreenSummarizer summarizer = CreateScreenSummarizer();
                summary = await summarizer.SummarizeAsync(new ScreenSummarizationRequest
                {
                    SessionId = request.SessionId,
                    Capture = capture,
                    IncludeBase64InSummary = false,
                    PromptOverride = BuildAndroidPhotoVisionPrompt(mobileRequest)
                }, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(summary.CompanionSafeSummary))
                {
                    summary.CompanionSafeSummary = "The player attached a phone photo, but the vision summarizer returned no description.";
                }
            }
            catch (Exception ex)
            {
                summary = new ScreenCaptureSummary
                {
                    CapturedAt = capture.CapturedAt,
                    ActiveApp = capture.ActiveApp,
                    ActivityType = "android_photo_attachment",
                    LikelyActivity = "The player attached a phone camera or gallery image for the Star to answer directly.",
                    CompanionSafeSummary = "The player attached a phone photo. Vision summarization failed, so answer the player's caption and acknowledge that the image was attached but could not be inspected in detail. Vision error: " + ex.Message,
                    Sensitivity = "normal",
                    ImageReference = capture.ImageReference,
                    Confidence = 0.15
                };
            }

            summary.ActivityType = string.IsNullOrWhiteSpace(summary.ActivityType) ? "android_photo_attachment" : summary.ActivityType;
            summary.ActiveApp = string.IsNullOrWhiteSpace(summary.ActiveApp) ? "SixStarsMobileAndroid" : summary.ActiveApp;
            summary.ImageReference = string.IsNullOrWhiteSpace(summary.ImageReference) ? capture.ImageReference : summary.ImageReference;
            summary.ImageBase64Jpeg = string.Empty;
            if (!string.IsNullOrWhiteSpace(mobileRequest.PhotoCaption))
            {
                summary.VisibleTextHighlights.Add("Player photo caption/request: " + TruncateForUi(mobileRequest.PhotoCaption, 500));
            }

            request.RequestType = RequestType.Photo;
            request.AttachedPhoto = new StarPhotoAttachment
            {
                ImageBytes = imageBytes.ToArray(),
                MimeType = mimeType,
                Caption = mobileRequest.PhotoCaption ?? string.Empty,
                ImageReference = capture.ImageReference,
                PhotoSummary = summary.CompanionSafeSummary ?? string.Empty,
                PhotoError = string.Empty
            };
            request.RecentScreenCaptures.Insert(0, summary);
            request.GameContext.Flags["androidPhotoAttached"] = "true";
            request.GameContext.Flags["androidPhotoSummary"] = TruncateForUi(summary.CompanionSafeSummary, 1000);
            request.GameContext.RecentEvents.Add("Android photo vision summary: " + TruncateForUi(summary.CompanionSafeSummary, 600));
            if (string.IsNullOrWhiteSpace(request.PlayerText))
            {
                request.PlayerText = string.IsNullOrWhiteSpace(mobileRequest.PhotoCaption)
                    ? "I attached a photo. Please look at it and reply directly to what you can see."
                    : mobileRequest.PhotoCaption.Trim();
            }

            return new MobileAttachedPhotoResult(true, summary.CompanionSafeSummary, string.Empty);
        }
        catch (Exception ex)
        {
            request.AttachedPhoto = new StarPhotoAttachment
            {
                MimeType = string.IsNullOrWhiteSpace(mobileRequest.PhotoMimeType) ? "image/jpeg" : mobileRequest.PhotoMimeType,
                Caption = mobileRequest.PhotoCaption ?? string.Empty,
                PhotoError = ex.Message
            };
            request.GameContext.RecentEvents.Add("Android photo attachment failed before the Star turn: " + ex.Message);
            return new MobileAttachedPhotoResult(false, string.Empty, ex.Message);
        }
    }

    private static string BuildAndroidPhotoVisionPrompt(MobileBridgeRequest mobileRequest)
    {
        StringBuilder prompt = new StringBuilder();
        prompt.AppendLine("You are a vision summarizer for The Six Stars Android photo bridge.");
        prompt.AppendLine("Look at the attached camera/gallery photo and describe it so an in-character Star companion can respond directly to the player.");
        prompt.AppendLine("This is not a desktop screen recording. It is a user-shared photo attached to a fresh Star request.");
        prompt.AppendLine("Mention concrete visible objects, colors, layout, mood, readable non-sensitive text, and any obvious artistic/game-related details.");
        prompt.AppendLine("Do not identify real people by name. Do not invent private facts. If the image is unclear, say confidence is low.");
        prompt.AppendLine("Keep the companionSafeSummary useful for a character reaction. Do not include secrets, passwords, account data, exact addresses, or medical/bank details.");
        if (!string.IsNullOrWhiteSpace(mobileRequest?.PhotoCaption))
        {
            prompt.AppendLine("Player caption/request: " + TruncateForUi(mobileRequest.PhotoCaption, 500));
        }
        return prompt.ToString();
    }

    private static bool HasMobilePhoto(MobileBridgeRequest request)
    {
        return request != null && !string.IsNullOrWhiteSpace(request.PhotoBase64);
    }

    private static byte[] DecodeMobilePhotoBase64(string photoBase64)
    {
        if (string.IsNullOrWhiteSpace(photoBase64)) return Array.Empty<byte>();
        string clean = photoBase64.Trim();
        int comma = clean.IndexOf(',');
        if (comma >= 0 && clean.Substring(0, comma).IndexOf("base64", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            clean = clean.Substring(comma + 1);
        }
        return Convert.FromBase64String(clean);
    }

    private static string TryGetLatestPhotoSummary(StarInteractionResponse response, MobileBridgeRequest request)
    {
        if (response != null && response.Debug != null && response.Debug.Notes != null)
        {
            string? note = response.Debug.Notes.FirstOrDefault(x => x != null && x.StartsWith("Android photo summary:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(note)) return note.Substring("Android photo summary:".Length).Trim();
        }

        if (request != null && HasMobilePhoto(request))
        {
            return "A phone photo was attached to this request. The vision summary was included in recent screen context for the Star.";
        }
        return string.Empty;
    }

    private static string TryGetLatestPhotoError(StarInteractionResponse response)
    {
        if (response != null && response.Debug != null && response.Debug.Notes != null)
        {
            string? note = response.Debug.Notes.FirstOrDefault(x => x != null && x.StartsWith("Android photo error:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(note)) return note.Substring("Android photo error:".Length).Trim();
        }
        return string.Empty;
    }

    private readonly struct MobileAttachedPhotoResult
    {
        public MobileAttachedPhotoResult(bool attached, string summary, string error)
        {
            Attached = attached;
            Summary = summary ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public bool Attached { get; }
        public string Summary { get; }
        public string Error { get; }
        public static MobileAttachedPhotoResult Empty => new MobileAttachedPhotoResult(false, string.Empty, string.Empty);
    }

    private MobileVoiceStreamScope BeginMobileVoiceStream(StarInteractionResponse response, bool isStory, string reason)
    {
        CancellationTokenSource cts = new CancellationTokenSource();
        CancellationTokenSource? previous = null;
        long ordinal;
        lock (_mobileVoiceStreamGate)
        {
            previous = _mobileVoiceStreamCts;
            if (_pendingMobileStoryVoiceStream != null && ReferenceEquals(_pendingMobileStoryVoiceStream.CancellationSource, previous))
            {
                _pendingMobileStoryVoiceStream = null;
                _pendingMobileStoryThinkingVoiceTask = null;
            }
            _mobileVoiceStreamCts = cts;
            ordinal = ++_mobileVoiceStreamOrdinal;
        }

        try { previous?.Cancel(); } catch { }

        string streamId = "android-voice-" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        MobileAudioStreamSession? audioSession = _mobileBridgeAudioHost?.CreateStreamSession(
            streamId,
            ordinal,
            "android-phone-dev",
            response.StarId ?? SelectedStarId,
            isStory,
            reason ?? string.Empty);

        OnUiThread(() => AppendMobileBridgeVoiceDebug("Android voice stream opened", "stream=" + streamId + ", ordinal=" + ordinal + ", story=" + isStory + ", targets=" + (audioSession?.TargetClientIds.Count ?? 0) + ", reason=" + reason));
        return new MobileVoiceStreamScope(streamId, ordinal, cts, audioSession);
    }

    private bool IsMobileVoiceStreamCurrent(MobileVoiceStreamScope stream)
    {
        if (stream == null) return false;
        lock (_mobileVoiceStreamGate)
        {
            return ReferenceEquals(_mobileVoiceStreamCts, stream.CancellationSource) && _mobileVoiceStreamOrdinal == stream.Ordinal && !stream.CancellationToken.IsCancellationRequested;
        }
    }

    private void EndMobileVoiceStream(MobileVoiceStreamScope stream, string reason)
    {
        if (stream == null) return;
        bool current;
        lock (_mobileVoiceStreamGate)
        {
            current = ReferenceEquals(_mobileVoiceStreamCts, stream.CancellationSource) && _mobileVoiceStreamOrdinal == stream.Ordinal;
            if (current) _mobileVoiceStreamCts = null;
        }

        if (current)
        {
            OnUiThread(() => AppendMobileBridgeVoiceDebug("Android voice stream closed", "stream=" + stream.StreamId + ", ordinal=" + stream.Ordinal + ", " + reason));
        }
        stream.Dispose();
    }

    private sealed class MobileVoiceStreamScope : IDisposable
    {
        public MobileVoiceStreamScope(string streamId, long ordinal, CancellationTokenSource cancellationSource, MobileAudioStreamSession? audioSession)
        {
            StreamId = streamId;
            Ordinal = ordinal;
            CancellationSource = cancellationSource;
            AudioSession = audioSession;
        }

        public string StreamId { get; }
        public long Ordinal { get; }
        public CancellationTokenSource CancellationSource { get; }
        public CancellationToken CancellationToken => CancellationSource.Token;
        public MobileAudioStreamSession? AudioSession { get; }

        public void Dispose()
        {
            try { CancellationSource.Dispose(); } catch { }
        }
    }

    private static string GetFlag(StarInteractionRequest request, string key)
    {
        if (request != null && request.GameContext != null && request.GameContext.Flags != null && request.GameContext.Flags.TryGetValue(key, out string? value))
        {
            return value ?? string.Empty;
        }
        return string.Empty;
    }

    private static DateTimeOffset? TryParseDateTimeOffset(string value)
    {
        if (DateTimeOffset.TryParse(value, out DateTimeOffset parsed)) return parsed;
        return null;
    }

    private MobileBridgeResponse CreateMobileResponse(MobileBridgeRequest mobileRequest, StarInteractionRequest runtimeRequest, string transcript, StarInteractionResponse response)
    {
        RequestType requestType = runtimeRequest?.RequestType ?? ParseMobileRequestType(mobileRequest.RequestType);
        return new MobileBridgeResponse
        {
            Ok = true,
            Source = "android-mobile-bridge",
            StarId = response.StarId ?? mobileRequest.StarId,
            RequestType = requestType.ToString(),
            Transcript = transcript ?? string.Empty,
            SpokenText = response.SpokenText ?? string.Empty,
            StarOutput = FormatStarOutput(response),
            ConversationSummary = response.ConversationSummary ?? string.Empty,
            ChangesSummary = response.ChangesSummary ?? string.Empty,
            EmotionalPurpose = response.EmotionalPurpose ?? string.Empty,
            HeartDelta = response.StateDeltas.Heart,
            TrustDelta = response.StateDeltas.Trust,
            RomanceDelta = response.StateDeltas.Romance,
            BenaveilDelta = response.StateDeltas.BenaveilInfluence,
            EmotionalConnectionDelta = response.StateDeltas.EmotionalConnectionPercent,
            EmotionalConnectionAfter = response.EmotionalConnectionPercentAfter,
            IsGirlfriendReadyAfter = response.IsGirlfriendReadyAfter,
            IsGirlfriendAfter = false,
            VoiceRequested = mobileRequest.ReturnAudio,
            VoiceSocketConnected = (_mobileBridgeAudioHost?.ClientCount ?? 0) > 0,
            StoryId = response.StoryId ?? string.Empty,
            StoryTitle = response.StoryTitle ?? string.Empty,
            StoryMarkdown = response.StoryMarkdown ?? string.Empty,
            StorySummary = response.StorySummary ?? string.Empty,
            StoryPartIndex = response.StoryPartIndex,
            StoryIsFinished = response.StoryIsFinished,
            StoryMarkdownReady = !string.IsNullOrWhiteSpace(response.StoryMarkdown),
            StoryAutoplayPlanned = _storySpeechAdapter.ShouldUseStoryAutoplay(response),
            StoryChunkCount = _storySpeechAdapter.CountStoryChunks(response.StoryMarkdown),
            PhotoAttached = HasMobilePhoto(mobileRequest),
            PhotoSummary = TryGetLatestPhotoSummary(response, mobileRequest),
            PhotoError = TryGetLatestPhotoError(response),
            TopAffectedEmotions = (response.TopAffectedEmotions ?? new List<TopAffectedEmotion>()).ToList(),
            RecentConversationHistory = CreateMobileRecentConversationHistoryFromResponse(mobileRequest, runtimeRequest, response, 10),
            EmotionalEffectVector = response.EmotionalEffectVector ?? string.Empty,
            NegativeEmotionVector = response.NegativeEmotionVector ?? string.Empty,
            LastEmotionVectorsText = FormatEmotionVectors(response.EmotionalEffectVector, response.NegativeEmotionVector),
            InterstarStatusText = InterstarStatusText,
            DailyStateSlotId = GetFlag(runtimeRequest, "todayStarStateSlotId"),
            DailyStateLocalDate = GetFlag(runtimeRequest, "todayStarStateLocalDate"),
            DailyStateGeneratedAt = TryParseDateTimeOffset(GetFlag(runtimeRequest, "todayStarStateGeneratedAt")),
            DailyStateSummary = GetFlag(runtimeRequest, "todayStarStateSummary"),
            DailyStateJson = GetFlag(runtimeRequest, "todayStarStateJson"),
            DailyStateXml = string.Empty,
            VoiceMessage = mobileRequest.ReturnAudio
                ? "Voice is streamed over the phone audio socket on port " + ResolveMobileBridgeAudioPort() + "."
                : "Phone voice was not requested."
        };
    }

    private async Task SendMobileVoiceAsync(StarInteractionResponse starResponse, MobileBridgeResponse mobileResponse, CancellationToken cancellationToken, MobileVoiceStreamScope? existingStream = null)
    {
        bool ownsStream = existingStream == null;
        MobileVoiceStreamScope voiceStream = existingStream ?? BeginMobileVoiceStream(starResponse, _storySpeechAdapter.ShouldUseStoryAutoplay(starResponse), "Android phone voice request");
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, voiceStream.CancellationToken);
        CancellationToken voiceToken = linkedCts.Token;

        try
        {
            if (_mobileBridgeAudioHost == null || _mobileBridgeAudioHost.ClientCount == 0 || voiceStream.AudioSession == null || voiceStream.AudioSession.TargetClientIds.Count == 0)
            {
                mobileResponse.VoiceSocketConnected = false;
                mobileResponse.VoiceError = "No Android audio socket is connected. The phone should connect to TCP " + ResolveMobileBridgeAudioPort() + " after /health.";
                OnUiThread(() => AppendMobileBridgeVoiceDebug("Voice generation skipped", "No Android audio socket was connected at stream start; stale reconnects will not receive this old stream."));
                ReleasePendingBackgroundAnalysis("Android bridge had no phone audio socket; released background analysis after text response.");
                return;
            }

            await _mobileBridgeAudioHost.SendStreamControlAsync(voiceStream.AudioSession, "start", "Starting fresh Android voice stream " + voiceStream.StreamId + ". The phone should clear stale queued speech before accepting this stream.", voiceToken).ConfigureAwait(false);

            if (_storySpeechAdapter.ShouldUseStoryAutoplay(starResponse))
            {
                await SendMobileStoryVoiceWithPrefetchAsync(starResponse, mobileResponse, voiceToken, voiceStream).ConfigureAwait(false);
                return;
            }

            PrepareVoiceCloneReferenceForTts(starResponse);
            mobileResponse.VoiceSocketConnected = true;
            await _mobileBridgeAudioHost.SendStatusAsync("Elusiaa opened the voice stream. Generating " + (starResponse.StarId ?? "Star") + " voice...", voiceToken).ConfigureAwait(false);
            OnUiThread(() => AppendMobileBridgeVoiceDebug("Voice generation started", (starResponse.StarId ?? "Star") + ", stream=" + voiceStream.Ordinal + ", streaming=" + _useStreamingTts));

            if (_useStreamingTts)
            {
                using IStreamingVoiceGenerator generator = CreateStreamingVoiceGenerator();
                using StreamingStarVoiceResponsePipeline mobilePipeline = new StreamingStarVoiceResponsePipeline(generator, new NullWavePlaybackEndpoint(), _speechPipeline, _interruptionManager)
                {
                    AutoPlayChunks = false
                };

                int chunksSent = 0;
                int bytesSent = 0;
                mobilePipeline.ChunkReadyForConsumer += (sender, args) =>
                {
                    try
                    {
                        if (!IsMobileVoiceStreamCurrent(voiceStream) || voiceToken.IsCancellationRequested) return;
                        int chunkIndex = chunksSent;
                        int totalChunks = Math.Max(1, starResponse.SpokenParts.Count == 0 ? 1 : starResponse.SpokenParts.Count);
                        string checksum = Sha256Hex(args.Result.WavAudio);
                        MobileAudioFrameContext frameContext = new MobileAudioFrameContext
                        {
                            IsStory = false,
                            TtsPartIndex = chunkIndex,
                            TtsTotalParts = totalChunks
                        };
                        MobileAudioSendResult sent = _mobileBridgeAudioHost.SendVoiceChunkAsync(args.Result, chunkIndex, totalChunks, voiceToken, voiceStream.AudioSession, frameContext).GetAwaiter().GetResult();
                        chunksSent++;
                        bytesSent += args.Result.WavAudio.Length;
                        _mobileBridgeAudioHost.SendStatusAsync("Elusiaa sent phone voice chunk " + chunksSent + "/" + totalChunks + " sha256=" + ShortHash(checksum) + ", bytes=" + args.Result.WavAudio.Length + ".", voiceToken).GetAwaiter().GetResult();
                        OnUiThread(() =>
                        {
                            MobileBridgeStatus = "Sent phone voice chunk " + chunksSent + " (" + args.Result.WavAudio.Length + " bytes, sha256=" + ShortHash(checksum) + ") to " + sent.SentClients + " phone client(s).";
                            AppendMobileBridgeVoiceDebug("Voice chunk sent", "stream=" + voiceStream.Ordinal + ", chunk=" + chunksSent + "/" + totalChunks + ", clients=" + sent.SentClients + ", bytes=" + args.Result.WavAudio.Length + ", sha256=" + ShortHash(checksum));
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        // A newer Android voice stream replaced this one.
                    }
                    catch (Exception ex)
                    {
                        mobileResponse.VoiceError = "Phone audio send failed: " + ex.Message;
                    }
                };
                mobilePipeline.GenerationError += (_, args) =>
                {
                    mobileResponse.VoiceError = args.ErrorMessage ?? "TTS generation error.";
                    if (!voiceToken.IsCancellationRequested) _ = _mobileBridgeAudioHost.SendErrorAsync(mobileResponse.VoiceError, CancellationToken.None);
                };

                await mobilePipeline.SpeakStreamingAsync(DefaultSessionId, starResponse, voiceToken).ConfigureAwait(false);
                await _mobileBridgeAudioHost.SendStatusAsync("Elusiaa closed the voice stream. Finished generating and sending " + chunksSent + " chunk(s), bytes=" + bytesSent + ".", voiceToken).ConfigureAwait(false);
                OnUiThread(() => AppendMobileBridgeVoiceDebug("Voice generation finished", "stream=" + voiceStream.Ordinal + ", chunks=" + chunksSent + ", bytes=" + bytesSent));
                mobileResponse.VoiceChunksSent = chunksSent;
                mobileResponse.VoiceBytesSent = bytesSent;
                if (string.IsNullOrWhiteSpace(mobileResponse.VoiceError))
                {
                    mobileResponse.VoiceMessage = "Queued " + chunksSent + " ordered voice chunk(s) on the Android phone audio socket.";
                }
            }
            else
            {
                IStarVoiceGenerator generator = CreateVoiceGenerator();
                VoiceSynthesisResult result = await generator.GenerateAsync(CreateVoiceSynthesisRequest(starResponse), voiceToken).ConfigureAwait(false);
                string checksum = Sha256Hex(result.WavAudio);
                MobileAudioFrameContext frameContext = new MobileAudioFrameContext { IsStory = false, TtsPartIndex = 0, TtsTotalParts = 1 };
                MobileAudioSendResult sent = await _mobileBridgeAudioHost.SendVoiceChunkAsync(result, 0, 1, voiceToken, voiceStream.AudioSession, frameContext).ConfigureAwait(false);
                await _mobileBridgeAudioHost.SendStatusAsync("Elusiaa sent one complete voice WAV. sha256=" + ShortHash(checksum) + ", bytes=" + result.WavAudio.Length + ".", voiceToken).ConfigureAwait(false);
                mobileResponse.VoiceChunksSent = sent.SentClients > 0 ? 1 : 0;
                mobileResponse.VoiceBytesSent = result.WavAudio.Length;
                mobileResponse.VoiceMessage = "Queued one non-streaming voice WAV on the Android phone audio socket.";
                OnUiThread(() => AppendMobileBridgeVoiceDebug("Non-streaming voice sent", "stream=" + voiceStream.Ordinal + ", clients=" + sent.SentClients + ", bytes=" + result.WavAudio.Length + ", sha256=" + ShortHash(checksum)));
            }

            if (!voiceToken.IsCancellationRequested)
            {
                ReleasePendingBackgroundAnalysis("Android phone voice chunks were generated/sent.");
            }
        }
        catch (OperationCanceledException)
        {
            mobileResponse.VoiceError = "Android phone voice stream was replaced by a newer request.";
            try
            {
                if (_mobileBridgeAudioHost != null && voiceStream.AudioSession != null)
                {
                    await _mobileBridgeAudioHost.SendStreamControlAsync(voiceStream.AudioSession, "cancel", "This Android voice stream was cancelled because a newer stream started.", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch { }
            OnUiThread(() => AppendMobileBridgeVoiceDebug("Android voice stream cancelled", "stream=" + voiceStream.Ordinal + " was replaced."));
        }
        finally
        {
            if (ownsStream)
            {
                try
                {
                    if (_mobileBridgeAudioHost != null && voiceStream.AudioSession != null && !voiceStream.CancellationToken.IsCancellationRequested)
                    {
                        await _mobileBridgeAudioHost.SendStreamControlAsync(voiceStream.AudioSession, "end", "Android voice stream completed.", CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch { }
                EndMobileVoiceStream(voiceStream, "completed or replaced");
            }
        }
    }



    private static bool IsMobileStoryThinkingPlaceholder(StarInteractionResponse response)
    {
        if (response == null) return false;
        return response.UnityEvents.Any(x => string.Equals(x.Type, "StoryGenerationStarted", StringComparison.OrdinalIgnoreCase));
    }

    private MobileVoiceStreamScope? TakePendingMobileStoryVoiceStream(out Task? thinkingVoiceTask)
    {
        MobileVoiceStreamScope? stream;
        lock (_mobileVoiceStreamGate)
        {
            stream = _pendingMobileStoryVoiceStream;
            thinkingVoiceTask = _pendingMobileStoryThinkingVoiceTask;
            _pendingMobileStoryVoiceStream = null;
            _pendingMobileStoryThinkingVoiceTask = null;
            if (stream == null) return null;
            bool current = ReferenceEquals(_mobileVoiceStreamCts, stream.CancellationSource)
                && _mobileVoiceStreamOrdinal == stream.Ordinal
                && !stream.CancellationToken.IsCancellationRequested;
            if (current) return stream;
        }

        try { stream.Dispose(); } catch { }
        return null;
    }

    private async Task EndSharedMobileStoryVoiceStreamAsync(MobileVoiceStreamScope stream, string reason)
    {
        if (stream == null) return;
        try
        {
            if (_mobileBridgeAudioHost != null && stream.AudioSession != null && !stream.CancellationToken.IsCancellationRequested)
            {
                await _mobileBridgeAudioHost.SendStreamControlAsync(stream.AudioSession, "end", "Android story voice stream completed.", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { }
        EndMobileVoiceStream(stream, reason);
    }

    private StoryOptions CreateMobileStoryOptions(MobileBridgeRequest mobileRequest, RequestType requestType)
    {
        StoryOptions options = CreateStoryOptions(false);
        string playerText = mobileRequest.PlayerText ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(playerText))
        {
            options.PlayerSuggestion = playerText.Trim();
        }
        return options;
    }

    private bool IsMobileBridgeStoryResponse(StarInteractionResponse response)
    {
        if (response == null) return false;
        return response.UnityEvents.Any(x =>
            x.Data.TryGetValue("mobileBridge", out string? value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    private async Task HandleGeneratedMobileStoryResponseAsync(StarInteractionResponse response)
    {
        TrackStoryFromResponse(response);
        PublishMobileStoryEventIfNeeded(response, "StoryResponseReady", "The full story markdown is ready for Android and game clients.");
        OnUiThread(() =>
        {
            StarOutput = FormatStarOutput(response);
            Status = "Generated Android story is ready. Streaming story narration to phone.";
            AppendMobileBridgeVoiceDebug("Story markdown ready", (response.StarId ?? "Star") + " chunks=" + _storySpeechAdapter.CountStoryChunks(response.StoryMarkdown) + ", chars=" + (response.StoryMarkdown?.Length ?? 0));
        });

        StarInteractionRequest generatedStoryRuntimeRequest = new StarInteractionRequest
        {
            StarId = response.StarId ?? SelectedStarId,
            RequestType = RequestType.Story,
            SessionId = "android-phone-dev"
        };
        MobileBridgeResponse mobileResponse = CreateMobileResponse(new MobileBridgeRequest
        {
            StarId = response.StarId ?? SelectedStarId,
            RequestType = "Story",
            ReturnAudio = true,
            SessionId = "android-phone-dev"
        }, generatedStoryRuntimeRequest, string.Empty, response);

        MobileVoiceStreamScope? sharedStoryVoiceStream = TakePendingMobileStoryVoiceStream(out Task? thinkingVoiceTask);
        try
        {
            if (thinkingVoiceTask != null)
            {
                try
                {
                    await thinkingVoiceTask.ConfigureAwait(false);
                    OnUiThread(() => AppendMobileBridgeVoiceDebug("Story thinking voice finished sending", "The generated story will continue on the same Android stream without clearing playback."));
                }
                catch (Exception ex)
                {
                    OnUiThread(() => AppendMobileBridgeVoiceDebug("Story thinking voice did not finish cleanly", ex.Message));
                }
            }

            await SendMobileVoiceAsync(response, mobileResponse, CancellationToken.None, sharedStoryVoiceStream).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnUiThread(() => MobileBridgeStatus = "Android generated story voice error: " + ex.Message);
            try { ReleasePendingBackgroundAnalysis("Android generated story voice failed: " + ex.Message); } catch { }
        }
        finally
        {
            if (sharedStoryVoiceStream != null)
            {
                await EndSharedMobileStoryVoiceStreamAsync(sharedStoryVoiceStream, "generated story narration completed").ConfigureAwait(false);
            }
        }
    }

    private void PublishMobileStoryEventIfNeeded(StarInteractionResponse response, string eventType, string message)
    {
        if (response == null) return;
        bool hasStoryData = response.UnityEvents.Any(x => x.Type.StartsWith("Story", StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrWhiteSpace(response.StoryMarkdown)
            || !string.IsNullOrWhiteSpace(response.StoryTitle);
        if (!hasStoryData) return;

        _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
        {
            EventType = eventType,
            SourceClientId = "desktop-runtime",
            SessionId = "android-phone-dev",
            StarId = response.StarId ?? SelectedStarId,
            SelectedStarId = SelectedStarId,
            IsGirlfriend = response.RelationshipEvents.Any(x => x.IndexOf("Accepted girlfriend proposal", StringComparison.OrdinalIgnoreCase) >= 0),
            Message = message,
            StoryId = response.StoryId ?? string.Empty,
            StoryTitle = response.StoryTitle ?? string.Empty,
            StoryMarkdown = response.StoryMarkdown ?? string.Empty,
            StorySummary = response.StorySummary ?? string.Empty,
            StoryPartIndex = response.StoryPartIndex,
            StoryIsFinished = response.StoryIsFinished,
            StoryChunkCount = _storySpeechAdapter.CountStoryChunks(response.StoryMarkdown),
            SpokenText = response.SpokenText ?? string.Empty,
            EmotionalEffectVector = response.EmotionalEffectVector ?? string.Empty,
            NegativeEmotionVector = response.NegativeEmotionVector ?? string.Empty,
            LastEmotionVectorsText = FormatEmotionVectors(response.EmotionalEffectVector, response.NegativeEmotionVector)
        });
    }

    private async Task HandleGeneratedMobileGirlfriendProposalResponseAsync(StarInteractionResponse response)
    {
        PublishMobileGirlfriendProposalEvent(response, "GirlfriendProposalResponseReady", "The full girlfriend proposal response is ready for Android.");
        OnUiThread(() =>
        {
            StarOutput = FormatStarOutput(response);
            Status = "Generated Android girlfriend proposal response is ready. Streaming narration to phone.";
            AppendMobileBridgeVoiceDebug("Girlfriend proposal ready", (response.StarId ?? "Star") + " response chars=" + (response.SpokenText?.Length ?? 0));
        });

        string sessionId = ExtractUnityEventData(response, "sessionId", "android-phone-prod");
        StarInteractionRequest runtimeRequest = new StarInteractionRequest
        {
            StarId = response.StarId ?? SelectedStarId,
            RequestType = RequestType.Chat,
            SessionId = sessionId
        };
        MobileBridgeResponse mobileResponse = CreateMobileResponse(new MobileBridgeRequest
        {
            StarId = response.StarId ?? SelectedStarId,
            RequestType = "Chat",
            ReturnAudio = true,
            SessionId = sessionId
        }, runtimeRequest, string.Empty, response);

        try
        {
            await SendMobileVoiceAsync(response, mobileResponse, CancellationToken.None, null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnUiThread(() => MobileBridgeStatus = "Android generated girlfriend proposal voice error: " + ex.Message);
            try { ReleasePendingBackgroundAnalysis("Android generated girlfriend proposal voice failed: " + ex.Message); } catch { }
        }
    }

    private void PublishMobileGirlfriendProposalEvent(StarInteractionResponse response, string eventType, string message)
    {
        if (response == null) return;
        string sessionId = ExtractUnityEventData(response, "sessionId", "android-phone-prod");
        string proposalDecision = ExtractUnityEventData(response, "decision", string.Empty);
        bool proposalIsGirlfriend = response.RelationshipEvents.Any(x => x.IndexOf("Accepted girlfriend proposal", StringComparison.OrdinalIgnoreCase) >= 0)
            || string.Equals(proposalDecision, "accept", StringComparison.OrdinalIgnoreCase)
            || string.Equals(proposalDecision, "already_girlfriend", StringComparison.OrdinalIgnoreCase);
        _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
        {
            EventType = eventType,
            SourceClientId = "desktop-runtime",
            SessionId = sessionId,
            StarId = response.StarId ?? SelectedStarId,
            SelectedStarId = SelectedStarId,
            IsGirlfriend = proposalIsGirlfriend,
            Message = message,
            SpokenText = response.SpokenText ?? string.Empty,
            EmotionalEffectVector = response.EmotionalEffectVector ?? string.Empty,
            NegativeEmotionVector = response.NegativeEmotionVector ?? string.Empty,
            LastEmotionVectorsText = FormatEmotionVectors(response.EmotionalEffectVector, response.NegativeEmotionVector)
        });
    }

    private static string ExtractUnityEventData(StarInteractionResponse response, string key, string fallback)
    {
        if (response != null)
        {
            foreach (UnityEventEnvelope envelope in response.UnityEvents ?? new List<UnityEventEnvelope>())
            {
                if (envelope != null && envelope.Data != null && envelope.Data.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        return fallback;
    }

    private async Task SendMobileStoryVoiceWithPrefetchAsync(StarInteractionResponse firstResponse, MobileBridgeResponse mobileResponse, CancellationToken cancellationToken, MobileVoiceStreamScope voiceStream)
    {
        StarInteractionResponse currentResponse = firstResponse;
        int totalStoryChunks = Math.Max(1, _storySpeechAdapter.CountStoryChunks(firstResponse.StoryMarkdown));
        int storyChunksQueued = 0;
        int totalVoiceChunks = 0;
        int totalBytes = 0;
        bool releasedBackgroundAnalysis = false;
        Task<List<VoiceSynthesisResult>>? prefetchedVoiceTask = null;

        PublishMobileStoryEventIfNeeded(firstResponse, "StoryResponseReady", "Full story markdown is ready; Android story voice prefetch is starting.");
        await _mobileBridgeAudioHost!.SendStatusAsync("Story voice mode: streaming the first ready sentence immediately and asking Qwen for the next passage as soon as the phone starts receiving audio.", cancellationToken).ConfigureAwait(false);
        OnUiThread(() => AppendMobileBridgeVoiceDebug("Story voice low-latency prefetch started", (firstResponse.StarId ?? "Star") + " storyChunks=" + totalStoryChunks));

        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();

            object prefetchGate = new object();
            bool nextPrefetchStarted = false;
            StarInteractionResponse? nextResponse = null;
            Task<List<VoiceSynthesisResult>>? nextVoiceTask = null;

            void StartNextPrefetchFromCurrentSpeech()
            {
                lock (prefetchGate)
                {
                    if (nextPrefetchStarted) return;
                    nextPrefetchStarted = true;

                    if (_storySpeechAdapter.TryCreateNextStoryChunkResponse(currentResponse, out StarInteractionResponse? createdNext) && createdNext != null)
                    {
                        nextResponse = createdNext;
                        TrackStoryFromResponse(createdNext);
                        PublishMobileStoryEventIfNeeded(createdNext, "StoryVoicePrefetchStarted", "The next story voice request was sent as soon as the current Android speech began queueing.");
                        nextVoiceTask = GenerateMobileStoryVoiceBatchAsync(createdNext, cancellationToken);
                        _ = _mobileBridgeAudioHost!.SendStatusAsync((createdNext.StarId ?? "Star") + " is already preparing story passage " + (createdNext.StoryPartIndex + 1) + " while the phone plays passage " + (currentResponse.StoryPartIndex + 1) + ".", CancellationToken.None);
                        OnUiThread(() => AppendMobileBridgeVoiceDebug("Next story passage requested early", "current=" + (currentResponse.StoryPartIndex + 1) + ", next=" + (createdNext.StoryPartIndex + 1)));
                    }
                }
            }

            MobileStoryVoiceSendStats stats;
            if (prefetchedVoiceTask != null)
            {
                // For an already-prefetched passage, ask for the following passage before sending this
                // passage to the phone. The phone begins playback as soon as these WAV chunks arrive.
                StartNextPrefetchFromCurrentSpeech();
                List<VoiceSynthesisResult> prefetchedVoice = await prefetchedVoiceTask.ConfigureAwait(false);
                if (!releasedBackgroundAnalysis)
                {
                    releasedBackgroundAnalysis = true;
                    ReleasePendingBackgroundAnalysis("Android story voice is queued; story TTS prefetch continues while the phone plays.");
                }

                stats = await SendMobileStoryVoiceBatchAsync(prefetchedVoice, currentResponse, totalStoryChunks, cancellationToken, voiceStream).ConfigureAwait(false);
            }
            else
            {
                stats = await GenerateAndSendMobileStoryVoicePassageStreamingAsync(
                    currentResponse,
                    totalStoryChunks,
                    voiceStream,
                    () =>
                    {
                        StartNextPrefetchFromCurrentSpeech();
                        if (!releasedBackgroundAnalysis)
                        {
                            releasedBackgroundAnalysis = true;
                            ReleasePendingBackgroundAnalysis("First Android story voice sentence queued; story TTS prefetch continues while the phone plays.");
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            storyChunksQueued++;
            totalVoiceChunks += stats.VoiceChunks;
            totalBytes += stats.Bytes;

            if (nextResponse == null || nextVoiceTask == null)
            {
                await _mobileBridgeAudioHost.SendStatusAsync("Story voice stream finished. Queued " + storyChunksQueued + " story passage(s), voice chunks=" + totalVoiceChunks + ", bytes=" + totalBytes + ".", cancellationToken).ConfigureAwait(false);
                OnUiThread(() => AppendMobileBridgeVoiceDebug("Story voice finished", "storyChunks=" + storyChunksQueued + ", voiceChunks=" + totalVoiceChunks + ", bytes=" + totalBytes));
                mobileResponse.VoiceChunksSent = totalVoiceChunks;
                mobileResponse.VoiceBytesSent = totalBytes;
                mobileResponse.VoiceMessage = "Queued full story narration to Android with sentence-level streaming and next-passage prefetch.";
                return;
            }

            currentResponse = nextResponse;
            prefetchedVoiceTask = nextVoiceTask;
        }
    }

    private async Task<MobileStoryVoiceSendStats> GenerateAndSendMobileStoryVoicePassageStreamingAsync(StarInteractionResponse response, int totalStoryChunks, MobileVoiceStreamScope voiceStream, Action onFirstVoiceChunkQueued, CancellationToken cancellationToken)
    {
        PrepareVoiceCloneReferenceForTts(response);

        if (!_useStreamingTts)
        {
            List<VoiceSynthesisResult> voice = await GenerateMobileStoryVoiceBatchAsync(response, cancellationToken).ConfigureAwait(false);
            onFirstVoiceChunkQueued?.Invoke();
            return await SendMobileStoryVoiceBatchAsync(voice, response, totalStoryChunks, cancellationToken, voiceStream).ConfigureAwait(false);
        }

        using IStreamingVoiceGenerator generator = CreateStreamingVoiceGenerator();
        using StreamingStarVoiceResponsePipeline pipeline = new StreamingStarVoiceResponsePipeline(generator, new NullWavePlaybackEndpoint(), _speechPipeline, _interruptionManager)
        {
            AutoPlayChunks = false
        };

        int voiceChunks = 0;
        int bytes = 0;
        bool firstChunkQueued = false;

        pipeline.ChunkReadyForConsumer += (sender, args) =>
        {
            try
            {
                if (!IsMobileVoiceStreamCurrent(voiceStream) || cancellationToken.IsCancellationRequested) return;
                if (!firstChunkQueued)
                {
                    firstChunkQueued = true;
                    onFirstVoiceChunkQueued?.Invoke();
                }

                int chunkIndex = voiceChunks;
                int totalTtsChunks = Math.Max(1, response.SpokenParts.Count == 0 ? 1 : response.SpokenParts.Count);
                string checksum = Sha256Hex(args.Result.WavAudio);
                MobileAudioFrameContext frameContext = new MobileAudioFrameContext
                {
                    IsStory = true,
                    StoryId = response.StoryId ?? string.Empty,
                    StoryTitle = response.StoryTitle ?? string.Empty,
                    StoryPartIndex = response.StoryPartIndex,
                    StoryChunkCount = totalStoryChunks,
                    TtsPartIndex = chunkIndex,
                    TtsTotalParts = totalTtsChunks
                };
                MobileAudioSendResult sent = _mobileBridgeAudioHost!.SendVoiceChunkAsync(args.Result, chunkIndex, totalTtsChunks, cancellationToken, voiceStream.AudioSession, frameContext).GetAwaiter().GetResult();
                voiceChunks++;
                bytes += args.Result.WavAudio.Length;
                _mobileBridgeAudioHost.SendStatusAsync("Story voice chunk " + (response.StoryPartIndex + 1) + "/" + totalStoryChunks + " streamed to phone immediately, ttsPart=" + voiceChunks + "/" + totalTtsChunks + ", sha256=" + ShortHash(checksum) + ".", cancellationToken).GetAwaiter().GetResult();
                _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
                {
                    EventType = "StoryVoiceChunkQueued",
                    SourceClientId = "desktop-runtime",
                    SessionId = "android-phone-dev",
                    StarId = response.StarId ?? SelectedStarId,
                    SelectedStarId = SelectedStarId,
                    Message = "Streamed story voice chunk " + (response.StoryPartIndex + 1) + " to " + sent.SentClients + " Android phone client(s).",
                    StoryId = response.StoryId ?? string.Empty,
                    StoryTitle = response.StoryTitle ?? string.Empty,
                    StoryPartIndex = response.StoryPartIndex,
                    StoryIsFinished = response.StoryIsFinished,
                    StoryChunkCount = totalStoryChunks,
                    SpokenText = response.SpokenText ?? string.Empty
                });
                OnUiThread(() => AppendMobileBridgeVoiceDebug("Story voice chunk streamed", "storyPart=" + (response.StoryPartIndex + 1) + "/" + totalStoryChunks + ", ttsPart=" + voiceChunks + "/" + totalTtsChunks + ", clients=" + sent.SentClients + ", bytes=" + args.Result.WavAudio.Length + ", sha256=" + ShortHash(checksum)));
            }
            catch (Exception ex)
            {
                OnUiThread(() => AppendMobileBridgeVoiceDebug("Story voice stream send failed", ex.Message));
            }
        };

        pipeline.GenerationError += (_, args) =>
        {
            if (!cancellationToken.IsCancellationRequested) _ = _mobileBridgeAudioHost!.SendErrorAsync(args.ErrorMessage ?? "TTS generation error.", CancellationToken.None);
        };

        await pipeline.SpeakStreamingAsync(DefaultSessionId, response, cancellationToken).ConfigureAwait(false);

        if (!firstChunkQueued)
        {
            onFirstVoiceChunkQueued?.Invoke();
        }

        return new MobileStoryVoiceSendStats(voiceChunks, bytes);
    }

    private async Task<List<VoiceSynthesisResult>> GenerateMobileStoryVoiceBatchAsync(StarInteractionResponse response, CancellationToken cancellationToken)
    {
        PrepareVoiceCloneReferenceForTts(response);
        if (_useStreamingTts)
        {
            using IStreamingVoiceGenerator generator = CreateStreamingVoiceGenerator();
            return await GenerateStreamingStoryVoiceBatchAsync(generator, response, cancellationToken).ConfigureAwait(false);
        }

        IStarVoiceGenerator nonStreaming = CreateVoiceGenerator();
        VoiceSynthesisResult result = await nonStreaming.GenerateAsync(CreateVoiceSynthesisRequest(response), cancellationToken).ConfigureAwait(false);
        return new List<VoiceSynthesisResult> { result };
    }

    private async Task<MobileStoryVoiceSendStats> SendMobileStoryVoiceBatchAsync(List<VoiceSynthesisResult> voiceChunks, StarInteractionResponse response, int totalStoryChunks, CancellationToken cancellationToken, MobileVoiceStreamScope voiceStream)
    {
        int bytes = 0;
        if (voiceChunks.Count == 0)
        {
            return new MobileStoryVoiceSendStats(0, 0);
        }

        for (int i = 0; i < voiceChunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsMobileVoiceStreamCurrent(voiceStream)) throw new OperationCanceledException("Android story stream was replaced.", cancellationToken);
            VoiceSynthesisResult chunk = voiceChunks[i];
            string checksum = Sha256Hex(chunk.WavAudio);
            MobileAudioFrameContext frameContext = new MobileAudioFrameContext
            {
                IsStory = true,
                StoryId = response.StoryId ?? string.Empty,
                StoryTitle = response.StoryTitle ?? string.Empty,
                StoryPartIndex = response.StoryPartIndex,
                StoryChunkCount = totalStoryChunks,
                TtsPartIndex = i,
                TtsTotalParts = voiceChunks.Count
            };
            MobileAudioSendResult sent = await _mobileBridgeAudioHost!.SendVoiceChunkAsync(chunk, i, voiceChunks.Count, cancellationToken, voiceStream.AudioSession, frameContext).ConfigureAwait(false);
            bytes += chunk.WavAudio.Length;
            await _mobileBridgeAudioHost.SendStatusAsync("Story voice chunk " + (response.StoryPartIndex + 1) + "/" + totalStoryChunks + " sent to phone, ttsPart=" + (i + 1) + "/" + voiceChunks.Count + ", sha256=" + ShortHash(checksum) + ".", cancellationToken).ConfigureAwait(false);
            _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
            {
                EventType = "StoryVoiceChunkQueued",
                SourceClientId = "desktop-runtime",
                SessionId = "android-phone-dev",
                StarId = response.StarId ?? SelectedStarId,
                SelectedStarId = SelectedStarId,
                Message = "Queued story voice chunk " + (response.StoryPartIndex + 1) + " to " + sent.SentClients + " Android phone client(s).",
                StoryId = response.StoryId ?? string.Empty,
                StoryTitle = response.StoryTitle ?? string.Empty,
                StoryPartIndex = response.StoryPartIndex,
                StoryIsFinished = response.StoryIsFinished,
                StoryChunkCount = totalStoryChunks,
                SpokenText = response.SpokenText ?? string.Empty
            });
            OnUiThread(() => AppendMobileBridgeVoiceDebug("Story voice chunk sent", "storyPart=" + (response.StoryPartIndex + 1) + "/" + totalStoryChunks + ", ttsPart=" + (i + 1) + "/" + voiceChunks.Count + ", clients=" + sent.SentClients + ", bytes=" + chunk.WavAudio.Length + ", sha256=" + ShortHash(checksum)));
        }

        return new MobileStoryVoiceSendStats(voiceChunks.Count, bytes);
    }

    private readonly struct MobileStoryVoiceSendStats
    {
        public MobileStoryVoiceSendStats(int voiceChunks, int bytes)
        {
            VoiceChunks = voiceChunks;
            Bytes = bytes;
        }

        public int VoiceChunks { get; }
        public int Bytes { get; }
    }

    private static RequestType ParseMobileRequestType(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out RequestType parsed))
        {
            return parsed;
        }
        return RequestType.Chat;
    }

    private static string FirstNonEmptyForMobile(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return string.Empty;
    }

    private static string TruncateForUi(string? text, int maxChars)
    {
        string value = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        if (value.Length <= maxChars) return value;
        return value.Substring(0, Math.Max(0, maxChars - 3)) + "...";
    }

    private static PcmWavData DecodePcm16WavOrRaw(byte[] bytes, int fallbackSampleRate, int fallbackChannels, int fallbackBits)
    {
        if (bytes.Length >= 44 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F')
        {
            int offset = 12;
            int channels = fallbackChannels <= 0 ? 1 : fallbackChannels;
            int sampleRate = fallbackSampleRate <= 0 ? 16000 : fallbackSampleRate;
            int bits = fallbackBits <= 0 ? 16 : fallbackBits;
            byte[]? data = null;
            while (offset + 8 <= bytes.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
                int size = BitConverter.ToInt32(bytes, offset + 4);
                offset += 8;
                if (size < 0 || offset + size > bytes.Length) break;
                if (id == "fmt " && size >= 16)
                {
                    short audioFormat = BitConverter.ToInt16(bytes, offset);
                    channels = BitConverter.ToInt16(bytes, offset + 2);
                    sampleRate = BitConverter.ToInt32(bytes, offset + 4);
                    bits = BitConverter.ToInt16(bytes, offset + 14);
                    if (audioFormat != 1 || bits != 16)
                    {
                        throw new NotSupportedException("Only PCM16 WAV input is supported by the Android bridge. audioFormat=" + audioFormat + ", bits=" + bits);
                    }
                }
                else if (id == "data")
                {
                    data = new byte[size];
                    Buffer.BlockCopy(bytes, offset, data, 0, size);
                }
                offset += size + (size % 2);
            }
            if (data == null) throw new InvalidDataException("WAV did not contain a data chunk.");
            return new PcmWavData(data, new PcmAudioFormat { SampleRateHz = sampleRate, Channels = channels, BitsPerSample = bits, Encoding = "pcm_s16le" });
        }

        return new PcmWavData(bytes, new PcmAudioFormat
        {
            SampleRateHz = fallbackSampleRate <= 0 ? 16000 : fallbackSampleRate,
            Channels = fallbackChannels <= 0 ? 1 : fallbackChannels,
            BitsPerSample = fallbackBits <= 0 ? 16 : fallbackBits,
            Encoding = "pcm_s16le"
        });
    }

    private sealed class PcmWavData
    {
        public PcmWavData(byte[] pcm16, PcmAudioFormat format)
        {
            Pcm16 = pcm16;
            Format = format;
        }
        public byte[] Pcm16 { get; }
        public PcmAudioFormat Format { get; }
    }

    private sealed class DisposableWhisperWrapper : IWhisperSpeechStreamer, IDisposable
    {
        private readonly IWhisperSpeechStreamer _inner;
        public DisposableWhisperWrapper(IWhisperSpeechStreamer inner) { _inner = inner; }
        public Task BeginSegmentAsync(string segmentId, PcmAudioFormat format, WhisperTranscriptionOptions options, CancellationToken cancellationToken) => _inner.BeginSegmentAsync(segmentId, format, options, cancellationToken);
        public Task PushAudioAsync(string segmentId, byte[] pcm16Buffer, int offset, int count, CancellationToken cancellationToken) => _inner.PushAudioAsync(segmentId, pcm16Buffer, offset, count, cancellationToken);
        public Task<SpeechTranscript> EndSegmentAsync(string segmentId, CancellationToken cancellationToken) => _inner.EndSegmentAsync(segmentId, cancellationToken);
        public Task CancelSegmentAsync(string segmentId, CancellationToken cancellationToken) => _inner.CancelSegmentAsync(segmentId, cancellationToken);
        public void Dispose()
        {
            if (_inner is IDisposable disposable) disposable.Dispose();
        }
    }
}
