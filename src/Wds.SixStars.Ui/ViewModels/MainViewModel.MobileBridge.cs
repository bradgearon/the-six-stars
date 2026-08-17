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
using Wds.SixStars.Api.DailyState;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Mobile;
using Wds.SixStars.Api.Speech;
using Wds.SixStars.Api.Screen;
using Wds.SixStars.Api.Stories;
using Wds.SixStars.Api.Voice;
using Wds.SixStars.Api.SpeechIntents;
using Wds.SixStars.Api.PreparedSpeech;
using Wds.SixStars.Api.Writings;
using Wds.SixStars.Ui.Infrastructure;
using Wds.SixStars.Ui.Networking;
using Wds.SixStars.Ui.Services;
using Wds.SixStars.Api.State;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private readonly object _mobileVoiceStreamGate = new object();
    private CancellationTokenSource? _mobileVoiceStreamCts;
    private CancellationTokenSource? _backgroundPreparedSpeechCts;
    private MobileVoiceStreamScope? _pendingMobileStoryVoiceStream;
    private Task? _pendingMobileStoryThinkingVoiceTask;
    private long _mobileVoiceStreamOrdinal;
    private string _mobileBridgeHostName = "192.168.1.179";
    private string _mobileBridgePort = "8066";
    private bool _mobileBridgeEnabled;
    private string _mobileBridgeStatus = "Android phone bridge stopped.";
    private string _mobileBridgeLastRequest = "No Android request yet.";
    private string _mobileBridgeVoiceDebugLog = "Elusiaa is listening for Android voice stream events.";

    public ICommand StartMobileBridgeCommand { get; private set; } = null!;
    public ICommand StopMobileBridgeCommand { get; private set; } = null!;
    public ICommand OpenConversationArchiveCommand { get; private set; } = null!;

    public string MobileBridgeHostName
    {
        get => _mobileBridgeHostName;
        set
        {
            if (SetProperty(ref _mobileBridgeHostName, value))
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeUrl)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeAudioUrl)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ConversationArchiveUrl)));
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
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ConversationArchiveUrl)));
            }
        }
    }

    public string MobileBridgeUrl => "http://" + (string.IsNullOrWhiteSpace(MobileBridgeHostName) ? Environment.MachineName : MobileBridgeHostName.Trim()) + ":" + ResolveMobileBridgePort().ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string MobileBridgeAudioUrl => "tcp://" + (string.IsNullOrWhiteSpace(MobileBridgeHostName) ? Environment.MachineName : MobileBridgeHostName.Trim()) + ":" + ResolveMobileBridgeAudioPort().ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string ConversationArchiveUrl => MobileBridgeUrl + NormalizeArchivePrefix(_appConfig.ConversationArchive.PathPrefix) + "/";

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
        _mobileBridgeHostName = string.IsNullOrWhiteSpace(_appConfig.MobileBridge.HostName) ? "192.168.1.179" : _appConfig.MobileBridge.HostName;
        _mobileBridgePort = (_appConfig.MobileBridge.Port <= 0 ? 8066 : _appConfig.MobileBridge.Port).ToString(System.Globalization.CultureInfo.InvariantCulture);
        StartMobileBridgeCommand = new RelayCommand(_ => StartMobileBridgeServer());
        StopMobileBridgeCommand = new RelayCommand(_ => StopMobileBridgeServer("Stopped by user."));
        OpenConversationArchiveCommand = new RelayCommand(_ => OpenConversationArchive());

        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeHostName)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgePort)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeUrl)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeAudioUrl)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ConversationArchiveUrl)));

        if (_appConfig.MobileBridge.EnabledOnStartup || IsTruthy(Environment.GetEnvironmentVariable("SIX_STARS_MOBILE_BRIDGE_ENABLED")))
        {
            StartMobileBridgeServer();
        }
    }

    private void StartMobileBridgeServer()
    {
        if (_services.MobileBridgeHost.IsRunning)
        {
            MobileBridgeStatus = "Android bridge is already running. HTTP/WebSocket " + MobileBridgeUrl + "; audio " + MobileBridgeAudioUrl + "; archive " + ConversationArchiveUrl + ".";
            return;
        }

        _services.MobileBridgeHost.Start(
            ResolveMobileBridgePort(),
            ResolveMobileBridgeAudioPort(),
            ProcessMobileMessageAsync,
            ProcessMobileAudioAsync,
            ProcessMobileTestAudioAsync,
            CreateMobileHealthPayload,
            CreateMobileStarsPayload,
            CreateMobileStatePayloadAsync,
            _mobileBridgeDispatcher,
            CreateConversationArchivePayloadAsync,
            ResolveConversationArchiveRoot(),
            _appConfig.ConversationArchive.PathPrefix,
            _appConfig.ConversationArchive.Enabled,
            _runtime,
            FirstNonEmptyForMobile(Environment.GetEnvironmentVariable("SIX_STARS_WORLD_EVENT_API_KEY"), _appConfig.WorldEvents.ApiKey));

        MobileBridgeEnabled = true;
        MobileBridgeStatus = "Android bridge running. HTTP/WebSocket " + MobileBridgeUrl + "; phone audio socket " + MobileBridgeAudioUrl + ". Archive: " + ConversationArchiveUrl + ". Use the HTTP base URL in the Android app.";
    }

    private void StopMobileBridgeServer(string reason)
    {
        _services.MobileBridgeHost.Stop();
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
        int clients = _services.MobileVoice.ConnectedClients;
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
            webSocketClientCount = _services.MobileBridgeHost.WebSocketClientCount,
            selectedStarId = SelectedStarId,
            backgroundWritingLoopEnabled = BackgroundWritingLoopEnabled,
            backgroundWritingLoopStatus = BackgroundWritingLoopStatus,
            xPostingEnabled = XPostingEnabled,
            xPostingStatus = XPostingStatus,
            streamingTts = _useStreamingTts,
            time = DateTimeOffset.Now
        };
    }

    private object CreateMobileStarsPayload()
    {
        IReadOnlyList<StarProfile> profiles = _profileProvider.GetAllProfiles()
            .Where(x => x != null && x.Active && !string.IsNullOrWhiteSpace(x.StarId))
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new
        {
            ok = true,
            stars = profiles.Select(x => x.StarId).ToArray(),
            starProfiles = profiles.Select(x => new
            {
                starId = x.StarId,
                displayName = x.DisplayName,
                element = x.Element,
                domain = x.Domain,
                sortOrder = x.SortOrder,
                actorId = x.ActorId,
                actorType = x.ActorType,
                soulDepth = x.Soul == null ? 0 : x.Soul.Depth
            }).ToArray(),
            requestTypes = RequestTypes.Select(x => x.ToString()).ToArray()
        };
    }

    private async Task<object> CreateMobileStatePayloadAsync(string sessionId, CancellationToken cancellationToken)
    {
        string safeSessionId = string.IsNullOrWhiteSpace(sessionId) ? "android-phone-dev" : sessionId.Trim();
        List<object> states = new List<object>();
        foreach (string starId in StarIds)
        {
            StarState state = await _services.MobileConversation.GetStateAsync(safeSessionId, starId, cancellationToken).ConfigureAwait(false);
            StarWritingNotesState writingState = await _writingService.GetStateAsync(starId, cancellationToken).ConfigureAwait(false);
            states.Add(CreateMobileStarStateDto(state, writingState));
        }

        return new
        {
            ok = true,
            selectedStarId = SelectedStarId,
            sessionId = safeSessionId,
            backgroundWritingLoopEnabled = BackgroundWritingLoopEnabled,
            backgroundWritingLoopStatus = BackgroundWritingLoopStatus,
            xPostingEnabled = XPostingEnabled,
            xPostingStatus = XPostingStatus,
            stars = states,
            latestEventSequence = _mobileBridgeDispatcher.GetEventsAfter(long.MaxValue).LatestSequence,
            time = DateTimeOffset.Now
        };
    }

    private async Task<object> CreateConversationArchivePayloadAsync(string sessionId, int maxLogs, CancellationToken cancellationToken)
    {
        string safeSessionId = string.IsNullOrWhiteSpace(sessionId) ? ResolveDefaultArchiveSessionId() : sessionId.Trim();
        if (maxLogs <= 0) maxLogs = _appConfig.ConversationArchive.MaxLogs <= 0 ? 160 : _appConfig.ConversationArchive.MaxLogs;
        if (maxLogs > 500) maxLogs = 500;

        SixStarsSaveData saveData = await _runtime.ExportSaveDataAsync(safeSessionId, cancellationToken).ConfigureAwait(false);
        List<object> dailyStates = new List<object>();
        foreach (string starId in StarIds)
        {
            StarDailyStateRecord? daily = await _runtime.GetCurrentDailyStateAsync(safeSessionId, starId, cancellationToken).ConfigureAwait(false);
            dailyStates.Add(new
            {
                starId,
                current = daily
            });
        }

        List<object> conversationTurns = CreateConversationArchiveTurnPayload(saveData);

        List<object> logs = new List<object>();
        string logDirectory = _runtime.ConversationDebugLogDirectory;
        if (Directory.Exists(logDirectory))
        {
            foreach (FileInfo file in new DirectoryInfo(logDirectory).GetFiles("*.md", SearchOption.AllDirectories)
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .Take(maxLogs))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text;
                try { text = await File.ReadAllTextAsync(file.FullName, cancellationToken).ConfigureAwait(false); }
                catch { continue; }

                logs.Add(new
                {
                    fileName = file.Name,
                    relativePath = Path.GetRelativePath(logDirectory, file.FullName),
                    lastWriteTimeUtc = file.LastWriteTimeUtc,
                    length = file.Length,
                    markdown = text
                });
            }
        }

        return new
        {
            ok = true,
            title = "The Last Cat: The Early Years",
            generatedAt = DateTimeOffset.Now,
            sessionId = safeSessionId,
            defaultSessionId = ResolveDefaultArchiveSessionId(),
            selectedStarId = SelectedStarId,
            starIds = StarIds.ToArray(),
            saveData,
            dailyStates,
            conversationTurns,
            conversations = conversationTurns,
            logs,
            logDirectory,
            latestConversationDebugLogPath = _runtime.LatestConversationDebugLogPath,
            archiveUrl = ConversationArchiveUrl
        };
    }

    private static List<object> CreateConversationArchiveTurnPayload(SixStarsSaveData saveData)
    {
        List<object> turns = new List<object>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (StarState state in (saveData.Stars ?? new List<StarState>()).Where(x => x != null))
        {
            string starId = string.IsNullOrWhiteSpace(state.StarId) ? "UnknownStar" : state.StarId.Trim();

            if (state.OldConversationArchives != null)
            {
                foreach (StarConversationArchiveEntry archive in state.OldConversationArchives
                    .Where(x => x != null)
                    .OrderBy(x => x.StartedAt))
                {
                    foreach (ConversationLogEntry entry in (archive.Entries ?? new List<ConversationLogEntry>()).OrderBy(x => x.CreatedAt))
                    {
                        AddConversationArchiveTurn(turns, seen, starId, entry, "old-archive", archive);
                    }
                }
            }

            if (state.MemoryLog != null)
            {
                foreach (ConversationLogEntry entry in state.MemoryLog.OrderBy(x => x.CreatedAt))
                {
                    AddConversationArchiveTurn(turns, seen, starId, entry, "memory-log", null);
                }
            }
        }

        return turns;
    }

    private static void AddConversationArchiveTurn(List<object> turns, HashSet<string> seen, string starId, ConversationLogEntry entry, string source, StarConversationArchiveEntry? archive)
    {
        if (!IsUsableMobileConversationTurn(entry)) return;

        string key = starId + "|"
            + entry.CreatedAt.ToUniversalTime().ToString("O") + "|"
            + (entry.PlayerText ?? string.Empty).Trim() + "|"
            + (entry.StarResponse ?? string.Empty).Trim();
        if (!seen.Add(key)) return;

        turns.Add(new
        {
            kind = "state-conversation-turn",
            source,
            archiveId = archive?.Id ?? string.Empty,
            archiveSummary = archive?.Summary ?? string.Empty,
            archiveStartedAt = archive?.StartedAt,
            archiveEndedAt = archive?.EndedAt,
            archiveTags = archive?.TopicTags ?? new List<string>(),
            starId,
            createdAt = entry.CreatedAt,
            requestType = entry.RequestType ?? string.Empty,
            playerText = entry.PlayerText ?? string.Empty,
            starResponse = entry.StarResponse ?? string.Empty,
            summary = entry.Summary ?? string.Empty,
            emotionalEffectVector = entry.EmotionalEffectVector ?? string.Empty,
            negativeEmotionVector = entry.NegativeEmotionVector ?? string.Empty,
            deterministicEmotionSummary = entry.DeterministicEmotionSummary ?? string.Empty,
            wisdomScore = entry.WisdomScore,
            actualDelta = entry.ActualDelta,
            suggestedDelta = entry.SuggestedDelta,
            emotionalConnectionPercentBefore = entry.EmotionalConnectionPercentBefore,
            emotionalConnectionPercentAfter = entry.EmotionalConnectionPercentAfter,
            isGirlfriendReadyAfter = entry.IsGirlfriendReadyAfter,
            acceptedPositiveSignals = entry.AcceptedPositiveSignals ?? new List<string>(),
            acceptedNegativeSignals = entry.AcceptedNegativeSignals ?? new List<string>(),
            acceptedCombos = entry.AcceptedCombos ?? new List<string>(),
            importantQuotes = entry.ImportantQuotes ?? new List<string>(),
            relationshipEvents = entry.RelationshipEvents ?? new List<string>(),
            tags = entry.Tags ?? new List<string>()
        });
    }

    private string ResolveDefaultArchiveSessionId()
    {
        return string.IsNullOrWhiteSpace(_appConfig.ConversationArchive.DefaultSessionId)
            ? "android-phone-prod"
            : _appConfig.ConversationArchive.DefaultSessionId.Trim();
    }

    private string ResolveConversationArchiveRoot()
    {
        string baseDir = AppContext.BaseDirectory;
        string candidate = Path.Combine(baseDir, "Content", "WebArchive");
        if (Directory.Exists(candidate)) return candidate;

        DirectoryInfo? dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            string projectCandidate = Path.Combine(dir.FullName, "src", "Wds.SixStars.Ui", "Content", "WebArchive");
            if (Directory.Exists(projectCandidate)) return projectCandidate;
            string directCandidate = Path.Combine(dir.FullName, "Content", "WebArchive");
            if (Directory.Exists(directCandidate)) return directCandidate;
            dir = dir.Parent;
        }
        return candidate;
    }

    private void OpenConversationArchive()
    {
        if (!_services.MobileBridgeHost.IsRunning)
        {
            StartMobileBridgeServer();
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ConversationArchiveUrl,
                UseShellExecute = true
            });
            MobileBridgeStatus = "Opened conversation archive: " + ConversationArchiveUrl;
        }
        catch (Exception ex)
        {
            MobileBridgeStatus = "Could not open conversation archive: " + ex.Message + " URL: " + ConversationArchiveUrl;
        }
    }

    private static string NormalizeArchivePrefix(string? prefix)
    {
        string value = string.IsNullOrWhiteSpace(prefix) ? "/archive" : prefix.Trim();
        if (!value.StartsWith("/", StringComparison.Ordinal)) value = "/" + value;
        return value.TrimEnd('/');
    }

    private async Task<MobileBridgeDispatchResult> HandleMobileBridgeCommandAsync(MobileBridgeDispatchCommand command, CancellationToken cancellationToken)
    {
        string commandType = string.IsNullOrWhiteSpace(command.CommandType) ? "RefreshState" : command.CommandType;
        string sessionId = string.IsNullOrWhiteSpace(command.SessionId) ? "android-phone-dev" : command.SessionId;
        string starId = ResolveDefaultStarId(command.StarId);

        // Build 15H: any explicit phone control is foreground player activity. Quiet automatic
        // RefreshState and at-rest initiative traffic are intentionally excluded so background
        // writing can still run while the phone is truly idle. A separate PlayerActivity command
        // is sent on Android touch/local playback so even controls that never need the LLM can
        // immediately cancel the current writing iteration.
        if (!commandType.Equals("RefreshState", StringComparison.OrdinalIgnoreCase)
            && !commandType.Equals("MaybeStarInitiative", StringComparison.OrdinalIgnoreCase)
            && !commandType.Equals("PlayerActivity", StringComparison.OrdinalIgnoreCase))
        {
            NotifyBackgroundWritingLoopForegroundActivity("Android command " + commandType);
        }

        switch (commandType)
        {
            case "PlayerActivity":
            {
                string activityReason = string.IsNullOrWhiteSpace(command.Reason)
                    ? "Android app activity"
                    : command.Reason.Trim();
                NotifyBackgroundWritingLoopForegroundActivity(activityReason);
                return new MobileBridgeDispatchResult
                {
                    Ok = true,
                    CommandId = command.CommandId,
                    CommandType = commandType,
                    StarId = starId,
                    SelectedStarId = SelectedStarId,
                    BackgroundWritingLoopEnabled = BackgroundWritingLoopEnabled,
                    BackgroundWritingLoopStatus = BackgroundWritingLoopStatus,
                    Message = "Background writing was preempted for player activity and will wait for 30 idle seconds before resuming.",
                    SuppressEventPublication = true
                };
            }
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
                StarState state = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                StarWritingNotesState writingState = await _writingService.GetStateAsync(starId, cancellationToken).ConfigureAwait(false);
                OnUiThread(() =>
                {
                    ApplyMobileStateToSelectedStarUi(state, "Android selected " + starId + ".");
                    PendingWritingCount = writingState.PendingCount;
                    PersistedWritingCount = writingState.PersistedWritings.Count;
                });
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, "Elusiaa shifted the phone focus to " + starId + ".", writingState);
            }
            case "SetMet":
            {
                bool isMet = command.IsMet ?? true;
                StarState state = await _services.StarState.SetMetAsync(sessionId, starId, isMet, cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, starId + " met=" + state.IsMet + ", ally=" + state.IsAlly + "."));
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, starId + (state.IsMet ? " is now marked met." : " is no longer marked met."));
            }
            case "SetAlly":
            {
                bool isAlly = command.IsAlly ?? true;
                StarState state = await _services.StarState.SetAllyAsync(
                    sessionId,
                    starId,
                    isAlly,
                    string.IsNullOrWhiteSpace(command.Reason) ? "Android bridge ally toggle" : command.Reason,
                    cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, starId + " ally=" + state.IsAlly + ", met=" + state.IsMet + "."));
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, starId + (state.IsAlly ? " joined as an ally." : " ally flag was removed."));
            }
            case "BreakupGirlfriend":
            {
                StarState state = await _services.StarState.SetExclusiveGirlfriendAsync(sessionId, starId, false, string.IsNullOrWhiteSpace(command.Reason) ? "Android prod breakup button" : command.Reason, cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, starId + " girlfriend relationship ended."));
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, starId + " is no longer marked as girlfriend.");
            }

            case "SetGirlfriendCuteMode":
            {
                bool enabled = command.CuteModeEnabled ?? true;
                StarState current = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                if (!current.IsGirlfriend)
                {
                    return PublishMobileCommandResult(command, false, commandType, starId, sessionId, current, starId + " is not currently the girlfriend Star, so cute mode settings were not changed.");
                }

                StarState state = await _services.StarState.SetCuteModeEnabledAsync(sessionId, starId, enabled, string.IsNullOrWhiteSpace(command.Reason) ? "Android girlfriend settings cute mode toggle" : command.Reason, cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, starId + " cute mode " + (enabled ? "enabled" : "disabled") + "."));
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, starId + " girlfriend cute mode " + (enabled ? "enabled." : "disabled."));
            }

            case "ActivateCuteModeNow":
            {
                StarState current = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                if (!current.IsGirlfriend)
                {
                    return PublishMobileCommandResult(command, false, commandType, starId, sessionId, current, starId + " is not currently the girlfriend Star, so cute mode could not be activated now.");
                }

                StarState state = await _runtime.ActivateGirlfriendCuteModeNowAsync(sessionId, starId, string.IsNullOrWhiteSpace(command.Reason) ? "Android Cute Now button" : command.Reason, cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, starId + " is in cute mode now."));
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, starId + " entered girlfriend cute mode now.");
            }

            case "SetBackgroundWritingLoopEnabled":
            {
                bool enabled = command.BackgroundWritingLoopEnabled ?? BackgroundWritingLoopEnabled;
                OnUiThread(() => BackgroundWritingLoopEnabled = enabled);

                StarState state = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                StarWritingNotesState writingState = await _writingService.GetStateAsync(starId, cancellationToken).ConfigureAwait(false);
                string message = enabled
                    ? "Desktop automatic Star writing generation is enabled. It will resume after the runtime has been idle for a few seconds."
                    : "Desktop automatic Star writing generation is off. The current background generation pass was cancelled for reliable voice playback.";
                OnUiThread(() => Status = message);
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, message, writingState);
            }

            case "SetXPostingEnabled":
            {
                bool enabled = command.XPostingEnabled ?? XPostingEnabled;
                OnUiThread(() => XPostingEnabled = enabled);
                StarState state = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                StarWritingNotesState writingState = await _writingService.GetStateAsync(starId, cancellationToken).ConfigureAwait(false);
                string message = enabled
                    ? "Automatic X posting is enabled for the four daily slots. A batch that starts will not be interrupted by foreground activity."
                    : "Automatic X posting is off. Any public-posting batch already in progress will finish.";
                OnUiThread(() => Status = message);
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, message, writingState);
            }

            case "PostAllStarWritingsToXNow":
            {
                List<string> configuredStars = _xPostingService.ConfiguredStarIds
                    .Select(ResolveDefaultStarId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                StarState state = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                StarWritingNotesState writingState = await _writingService.GetStateAsync(starId, cancellationToken).ConfigureAwait(false);
                if (configuredStars.Count == 0)
                {
                    string unavailable = "No enabled/configured X Star accounts are available to queue.";
                    OnUiThread(() => Status = unavailable);
                    return PublishMobileCommandResult(command, false, commandType, starId, sessionId, state, unavailable, writingState);
                }

                foreach (string configuredStar in configuredStars)
                {
                    QueueManualXPosting(configuredStar);
                }

                string message = "Queued background five-post X batches for " + configuredStars.Count + " configured Stars: "
                    + string.Join(", ", configuredStars)
                    + ". They will run one Star at a time through the uninterrupted X posting lane.";
                OnUiThread(() => Status = message);
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, message, writingState);
            }

            case "PostStarWritingsToXNow":
            {
                StarState state = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                StarWritingNotesState writingState = await _writingService.GetStateAsync(starId, cancellationToken).ConfigureAwait(false);
                if (!_xPostingService.HasConfiguredClient(starId))
                {
                    string unavailable = "No enabled X account with a ClientId and loopback RedirectUri is configured for " + starId + ".";
                    OnUiThread(() => Status = unavailable);
                    return PublishMobileCommandResult(command, false, commandType, starId, sessionId, state, unavailable, writingState);
                }

                QueueManualXPosting(starId);
                string message = "Queued an uninterrupted five-post X batch for " + starId + ". Progress will arrive through bridge events.";
                OnUiThread(() => Status = message);
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, message, writingState);
            }

            case "GenerateStarWriting":
            {
                using IDisposable writingLoopLease = BeginBackgroundWritingForegroundActivity("an Android request to generate Star writing");
                StarWritingCreateResult writingResult = await GenerateForgeShelfContentAsync(starId, cancellationToken).ConfigureAwait(false);
                StarState state = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                List<string> items = IsConstellationStory(writingResult)
                    ? writingResult.Batch.Items.Select(x =>
                        (string.IsNullOrWhiteSpace(x.Title) ? "Constellation Story" : x.Title) + Environment.NewLine + x.Writing +
                        (x.Persisted ? " (saved)" : " (not saved)"))
                        .ToList()
                    : writingResult.Batch.Items.Select((x, i) =>
                        (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". [" + string.Join(" + ", x.WordsUsed) + "] " + x.Writing +
                        (x.Persisted ? " (saved)" : " (not saved)"))
                        .ToList();
                string message = BuildWritingGenerationStatus(starId, writingResult);
                OnUiThread(() =>
                {
                    if (string.Equals(SelectedStarId, starId, StringComparison.OrdinalIgnoreCase))
                    {
                        PendingWritingCount = writingResult.State.PendingCount;
                        PersistedWritingCount = writingResult.State.PersistedWritings.Count;
                        StarOutput = string.Join(Environment.NewLine + Environment.NewLine, items);
                        Status = message;
                    }
                });
                MobileBridgeDispatchEvent evt = _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
                {
                    EventType = "StarWritingGenerated",
                    CommandId = command.CommandId,
                    SourceClientId = command.SourceClientId,
                    SessionId = sessionId,
                    StarId = starId,
                    SelectedStarId = SelectedStarId,
                    IsMet = state.IsMet,
                    IsAlly = state.IsAlly,
                    IsGirlfriend = state.IsGirlfriend,
                    PendingWritingCount = writingResult.State.PendingCount,
                    PersistedWritingCount = writingResult.State.PersistedWritings.Count,
                    WritingItems = items,
                    Message = message
                });
                return new MobileBridgeDispatchResult
                {
                    Ok = true,
                    CommandId = command.CommandId,
                    CommandType = commandType,
                    StarId = starId,
                    SelectedStarId = SelectedStarId,
                    IsMet = state.IsMet,
                    IsAlly = state.IsAlly,
                    IsGirlfriend = state.IsGirlfriend,
                    PendingWritingCount = writingResult.State.PendingCount,
                    PersistedWritingCount = writingResult.State.PersistedWritings.Count,
                    WritingItems = items,
                    StarOutput = string.Join(Environment.NewLine + Environment.NewLine, items),
                    State = state,
                    Event = evt,
                    Message = message,
                    SuppressEventPublication = true
                };
            }

            case "TellStarWriting":
            {
                using IDisposable writingLoopLease = BeginBackgroundWritingForegroundActivity("an Android request for the Star to tell her writing");
                StarWritingPerformanceResult? performance = await _writingService.TellPendingAsync(
                    starId,
                    string.IsNullOrWhiteSpace(command.WritingOutputType) ? StarWritingOutputTypes.Spark : command.WritingOutputType,
                    cancellationToken).ConfigureAwait(false);
                StarState state = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                if (performance == null)
                {
                    StarWritingNotesState writingState = await _writingService.GetStateAsync(starId, cancellationToken).ConfigureAwait(false);
                    bool requestedConstellation = string.Equals(
                        StarWritingOutputTypes.Normalize(command.WritingOutputType),
                        StarWritingOutputTypes.ConstellationStory,
                        StringComparison.OrdinalIgnoreCase);
                    string emptyMessage = requestedConstellation
                        ? starId + " has no unplayed persisted Constellation Stories waiting on the shelf."
                        : starId + " has no untold persisted Sparks waiting to be told.";
                    OnUiThread(() =>
                    {
                        if (string.Equals(SelectedStarId, starId, StringComparison.OrdinalIgnoreCase))
                        {
                            PendingWritingCount = writingState.PendingCount;
                            PersistedWritingCount = writingState.PersistedWritings.Count;
                            Status = emptyMessage;
                        }
                    });
                    MobileBridgeDispatchEvent emptyEvent = _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
                    {
                        EventType = "StarWritingPerformanceReady",
                        CommandId = command.CommandId,
                        SourceClientId = command.SourceClientId,
                        SessionId = sessionId,
                        StarId = starId,
                        SelectedStarId = SelectedStarId,
                        IsMet = state.IsMet,
                        IsAlly = state.IsAlly,
                        IsGirlfriend = state.IsGirlfriend,
                        PendingWritingCount = writingState.PendingCount,
                        PersistedWritingCount = writingState.PersistedWritings.Count,
                        WritingItems = new List<string>(),
                        Message = emptyMessage
                    });
                    return new MobileBridgeDispatchResult
                    {
                        Ok = true,
                        CommandId = command.CommandId,
                        CommandType = commandType,
                        StarId = starId,
                        SelectedStarId = SelectedStarId,
                        IsMet = state.IsMet,
                        IsAlly = state.IsAlly,
                        IsGirlfriend = state.IsGirlfriend,
                        PendingWritingCount = writingState.PendingCount,
                        PersistedWritingCount = writingState.PersistedWritings.Count,
                        WritingItems = new List<string>(),
                        State = state,
                        Event = emptyEvent,
                        Message = emptyMessage,
                        SuppressEventPublication = true
                    };
                }

                bool isConstellation = string.Equals(
                    StarWritingOutputTypes.Normalize(performance.OutputType),
                    StarWritingOutputTypes.ConstellationStory,
                    StringComparison.OrdinalIgnoreCase);
                List<string> items = isConstellation
                    ? performance.SelectedWritings.Select(x =>
                        (string.IsNullOrWhiteSpace(x.Title) ? "Constellation Story" : x.Title)
                        + " — " + Math.Round(performance.SelectedOverallValue, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " IU")
                        .ToList()
                    : performance.SelectedWritings.Select(x => x.Writing).ToList();
                string message = isConstellation
                    ? starId + " selected the highest-Overall-IU unplayed Constellation Story, “"
                        + (performance.Response.StoryTitle ?? "Constellation Story") + "” at "
                        + Math.Round(performance.SelectedOverallValue, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                        + " IU, and split the complete story into " + performance.Response.SpokenParts.Count
                        + " spoken queue chunks. " + performance.State.PendingConstellationStoryCount + " unplayed Constellation Stories remain."
                    : starId + " chose " + items.Count + " Spark(s) from a weighted "
                        + performance.CandidateCount + "-item listening set drawn from her top " + performance.ElitePoolCount
                        + " IU Sparks (" + performance.AnchorCount + " peak anchors, " + performance.ContrastCount
                        + " contrast picks); " + performance.State.PendingCount + " untold Sparks remain.";
                OnUiThread(() =>
                {
                    if (string.Equals(SelectedStarId, starId, StringComparison.OrdinalIgnoreCase))
                    {
                        PendingWritingCount = performance.State.PendingCount;
                        PersistedWritingCount = performance.State.PersistedWritings.Count;
                        StarOutput = performance.Response.SpokenText;
                        Status = message;
                    }
                });
                MobileBridgeDispatchEvent evt = _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
                {
                    EventType = "StarWritingPerformanceReady",
                    CommandId = command.CommandId,
                    SourceClientId = command.SourceClientId,
                    SessionId = sessionId,
                    StarId = starId,
                    SelectedStarId = SelectedStarId,
                    IsMet = state.IsMet,
                    IsAlly = state.IsAlly,
                    IsGirlfriend = state.IsGirlfriend,
                    PendingWritingCount = performance.State.PendingCount,
                    PersistedWritingCount = performance.State.PersistedWritings.Count,
                    WritingItems = items,
                    SpokenText = performance.Response.SpokenText,
                    StoryId = performance.Response.StoryId ?? string.Empty,
                    StoryTitle = performance.Response.StoryTitle ?? string.Empty,
                    StoryIsFinished = performance.Response.StoryIsFinished,
                    StoryChunkCount = performance.Response.SpokenParts.Count,
                    Message = message
                });
                MobileBridgeDispatchResult dispatchResult = new MobileBridgeDispatchResult
                {
                    Ok = true,
                    CommandId = command.CommandId,
                    CommandType = commandType,
                    StarId = starId,
                    SelectedStarId = SelectedStarId,
                    IsMet = state.IsMet,
                    IsAlly = state.IsAlly,
                    IsGirlfriend = state.IsGirlfriend,
                    PendingWritingCount = performance.State.PendingCount,
                    PersistedWritingCount = performance.State.PersistedWritings.Count,
                    WritingItems = items,
                    SpokenText = performance.Response.SpokenText,
                    StarOutput = performance.Response.SpokenText,
                    StoryId = performance.Response.StoryId ?? string.Empty,
                    StoryTitle = performance.Response.StoryTitle ?? string.Empty,
                    StoryIsFinished = performance.Response.StoryIsFinished,
                    StoryChunkCount = performance.Response.SpokenParts.Count,
                    VoiceRequested = command.ReturnAudio,
                    VoiceSocketConnected = _services.MobileVoice.IsConnected,
                    State = state,
                    Event = evt,
                    Message = message,
                    SuppressEventPublication = true
                };
                if (command.ReturnAudio)
                {
                    MobileBridgeResponse mobileVoiceResponse = new MobileBridgeResponse
                    {
                        Ok = true,
                        StarId = starId,
                        RequestType = isConstellation ? "ConstellationStoryPerformance" : "StarWritingPerformance",
                        SpokenText = performance.Response.SpokenText,
                        StoryId = performance.Response.StoryId ?? string.Empty,
                        StoryTitle = performance.Response.StoryTitle ?? string.Empty,
                        StoryIsFinished = performance.Response.StoryIsFinished,
                        StoryChunkCount = performance.Response.SpokenParts.Count,
                        StarOutput = performance.Response.SpokenText,
                        VoiceRequested = true,
                        VoiceSocketConnected = _services.MobileVoice.IsConnected
                    };
                    dispatchResult.AfterTransportResponseAsync = () => SendMobileVoiceAsync(performance.Response, mobileVoiceResponse, CancellationToken.None);
                }
                return dispatchResult;
            }

            case "SetSpeechPlaybackPaused":
            case "ResumeSpeech":
            {
                bool paused = command.PlaybackPaused ?? !commandType.Equals("ResumeSpeech", StringComparison.OrdinalIgnoreCase);
                StarState state = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                MobileBridgeDispatchEvent playbackEvent = _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
                {
                    EventType = "SetSpeechPlaybackPaused",
                    CommandId = command.CommandId,
                    SourceClientId = command.SourceClientId,
                    SessionId = sessionId,
                    StarId = starId,
                    SelectedStarId = SelectedStarId,
                    PlaybackPaused = paused,
                    Message = paused ? "Star speech playback paused." : "Star speech playback resumed."
                });
                return new MobileBridgeDispatchResult
                {
                    Ok = true,
                    CommandId = command.CommandId,
                    CommandType = commandType,
                    StarId = starId,
                    SelectedStarId = SelectedStarId,
                    PlaybackPaused = paused,
                    State = state,
                    Event = playbackEvent,
                    Message = playbackEvent.Message,
                    SuppressEventPublication = true
                };
            }

            case "ClearHistory":
            {
                StarState state = await ResetStarEverywhereAsync(starId, sessionId, cancellationToken).ConfigureAwait(false);
                _stateChangeTracker.Reset(starId);
                StarWritingNotesState writingState = await _writingService.GetStateAsync(starId, cancellationToken).ConfigureAwait(false);
                OnUiThread(() =>
                {
                    ApplyMobileStateToSelectedStarUi(state, "Cleared Android bridge history for " + starId + ".");
                    if (string.Equals(SelectedStarId, starId, StringComparison.OrdinalIgnoreCase))
                    {
                        PendingWritingCount = writingState.PendingCount;
                        PersistedWritingCount = writingState.PersistedWritings.Count;
                        StarOutput = string.Empty;
                        LastTranscript = string.Empty;
                    }
                });
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, "Khavia washed the Android conversation history clean for " + starId + ".", writingState);
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
                        GirlfriendCuteModeEnabled = reset.GirlfriendCuteModeEnabled,
                        GirlfriendCuteModeActive = reset.GirlfriendCuteModeEnabled && (reset.GirlfriendHourlyState?.CuteModeActive ?? false),
                        GirlfriendCuteModeSignature = reset.GirlfriendHourlyState?.CuteModeSignature ?? string.Empty,
                        PendingWritingCount = 0,
                        PersistedWritingCount = 0,
                        Heart = reset.Heart,
                        Trust = reset.Trust,
                        Romance = reset.Romance,
                        EmotionalConnectionPercent = reset.EmotionalConnectionPercent,
                        IsGirlfriendReady = reset.IsGirlfriendReady,
                        Message = "Elusiaa helped fully clear the shared local Star state for " + star + ", including daily JSON/XML cache, relationship scores, and conversation history."
                    });
                }
                if (selectedState == null) selectedState = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                StarWritingNotesState selectedWritingState = await _writingService.GetStateAsync(selectedState.StarId, cancellationToken).ConfigureAwait(false);
                OnUiThread(() =>
                {
                    ApplyMobileStateToSelectedStarUi(selectedState, "Cleared all Android bridge Star data.");
                    PendingWritingCount = selectedWritingState.PendingCount;
                    PersistedWritingCount = selectedWritingState.PersistedWritings.Count;
                    StarOutput = string.Empty;
                    LastTranscript = string.Empty;
                });
                return PublishMobileCommandResult(command, true, commandType, selectedState.StarId, sessionId, selectedState, "Elusiaa swept the shared Star memory shelf clean and refreshed the state.", selectedWritingState);
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
                StarState state = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
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
                    RequestType = "StarInitiative",
                    PlayerText = string.Empty,
                    Area = string.IsNullOrWhiteSpace(command.Area) ? Area : command.Area,
                    Quest = string.IsNullOrWhiteSpace(command.Quest) ? "AndroidProdStarInitiative" : command.Quest,
                    RomanceMode = command.RomanceMode,
                    SpeakOnDesktop = command.SpeakOnDesktop,
                    ReturnAudio = command.PrefetchForListen ? false : command.ReturnAudio,
                    ReturnAudioInline = command.ReturnAudioInline || command.PrefetchForListen,
                    PrefetchForListen = command.PrefetchForListen,
                    ClientName = string.IsNullOrWhiteSpace(command.ClientName) ? "SixStarsMobileAndroid-StarInitiative" : command.ClientName,
                    Reason = string.IsNullOrWhiteSpace(command.Reason) ? "mobile at-rest initiative" : command.Reason
                };
                MobileBridgeResponse initiativeResponse = await ProcessMobileConversationAsync(initiativeRequest, string.Empty, cancellationToken).ConfigureAwait(false);
                StarState state = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                MobileBridgeDispatchResult result = CreateConversationDispatchResult(command, "MaybeStarInitiative", sessionId, starId, state, initiativeResponse);
                if (command.PrefetchForListen)
                {
                    // A prepared Listen response is private cache material for the requesting phone.
                    // Do not publish it to every connected UI before the user presses Listen.
                    result.SuppressEventPublication = true;
                    result.PrefetchForListen = true;
                    result.PrefetchedAtUtc = initiativeResponse.PrefetchedAtUtc ?? DateTimeOffset.UtcNow;
                    result.State = null;
                    result.RecentConversationHistory.Clear();
                    result.Event = null;
                    result.Message = initiativeResponse.Ok
                        ? (initiativeResponse.InitiativeShouldSpeak && initiativeResponse.InlineVoiceChunks.Count > 0
                            ? starId + " prepared an at-rest line and voice for the next Listen tap."
                            : starId + " did not prepare a usable at-rest line yet.")
                        : initiativeResponse.VoiceError;
                    return result;
                }

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
                    Message = initiativeResponse.Ok
                        ? (initiativeResponse.InitiativeShouldSpeak ? starId + " chose to say something at rest." : starId + " had nothing state-backed to say right now.")
                        : initiativeResponse.VoiceError,
                    Error = initiativeResponse.Ok ? string.Empty : initiativeResponse.VoiceError,
                    SpokenText = initiativeResponse.InitiativeShouldSpeak ? (initiativeResponse.SpokenText ?? string.Empty) : string.Empty,
                    InitiativeShouldSpeak = initiativeResponse.InitiativeShouldSpeak,
                    InitiativeSkippedBeforeLlm = initiativeResponse.InitiativeSkippedBeforeLlm,
                    InitiativeStateScore = initiativeResponse.InitiativeStateScore,
                    InitiativePriority = initiativeResponse.InitiativePriority,
                    InitiativeTopic = initiativeResponse.InitiativeTopic ?? string.Empty,
                    InitiativeReason = initiativeResponse.InitiativeReason ?? string.Empty,
                    ServerElapsedMs = initiativeResponse.ServerElapsedMs,
                    RuntimeElapsedMs = initiativeResponse.RuntimeElapsedMs,
                    GateWaitMs = initiativeResponse.GateWaitMs,
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
                StarState state = await _services.MobileConversation.GetStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                StarWritingNotesState writingState = await _writingService.GetStateAsync(starId, cancellationToken).ConfigureAwait(false);
                OnUiThread(() =>
                {
                    ApplyMobileStateToSelectedStarUi(state, "Refreshed Android bridge state for " + starId + ".");
                    PendingWritingCount = writingState.PendingCount;
                    PersistedWritingCount = writingState.PersistedWritings.Count;
                });
                return PublishMobileCommandResult(command, true, "RefreshState", starId, sessionId, state, "Nyxari checked the current state for " + starId + ".", writingState);
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

    private void PublishMobileConversationFailureEventIfNeeded(MobileBridgeRequest mobileRequest, string starId, RequestType requestType, string error)
    {
        if (requestType == RequestType.StarInitiative || requestType == RequestType.Photo) return;
        try
        {
            _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
            {
                EventType = "MobileConversationResponseFailed",
                CommandId = "direct-mobile-failed-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                SourceClientId = string.IsNullOrWhiteSpace(mobileRequest.ClientName) ? "SixStarsMobileAndroid" : mobileRequest.ClientName,
                SessionId = string.IsNullOrWhiteSpace(mobileRequest.SessionId) ? "android-phone-dev" : mobileRequest.SessionId,
                StarId = starId ?? string.Empty,
                SelectedStarId = SelectedStarId,
                Message = "The direct Android conversation request failed inside the desktop runtime.",
                Error = error ?? string.Empty
            });
        }
        catch
        {
        }
    }

    private void PublishMobileConversationResponseEventIfNeeded(MobileBridgeRequest mobileRequest, StarInteractionRequest request, MobileBridgeResponse response, StarInteractionResponse starResponse, StarState stateAfterResponse)
    {
        if (request == null || response == null) return;
        if (request.RequestType == RequestType.StarInitiative || request.RequestType == RequestType.Photo) return;

        try
        {
            _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
            {
                EventType = response.Ok ? "MobileConversationResponseReady" : "MobileConversationResponseFailed",
                CommandId = "direct-mobile-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                SourceClientId = string.IsNullOrWhiteSpace(mobileRequest.ClientName) ? "SixStarsMobileAndroid" : mobileRequest.ClientName,
                SessionId = request.SessionId ?? string.Empty,
                StarId = response.StarId ?? request.StarId ?? string.Empty,
                SelectedStarId = SelectedStarId,
                IsMet = stateAfterResponse.IsMet,
                IsAlly = stateAfterResponse.IsAlly,
                Heart = stateAfterResponse.Heart,
                Trust = stateAfterResponse.Trust,
                Romance = stateAfterResponse.Romance,
                EmotionalConnectionPercent = stateAfterResponse.EmotionalConnectionPercent,
                IsGirlfriendReady = stateAfterResponse.IsGirlfriendReady,
                IsGirlfriend = stateAfterResponse.IsGirlfriend,
                GirlfriendCuteModeEnabled = stateAfterResponse.GirlfriendCuteModeEnabled,
                GirlfriendCuteModeActive = stateAfterResponse.GirlfriendCuteModeEnabled && (stateAfterResponse.GirlfriendHourlyState?.CuteModeActive ?? false),
                GirlfriendCuteModeSignature = stateAfterResponse.GirlfriendHourlyState?.CuteModeSignature ?? string.Empty,
                Message = response.Ok ? "The Star response is ready for Android. If the direct HTTP request was interrupted, the phone can use this queued event." : response.VoiceError,
                Error = response.Ok ? string.Empty : response.VoiceError,
                Transcript = response.Transcript ?? string.Empty,
                SpokenText = response.SpokenText ?? string.Empty,
                StoryId = response.StoryId ?? string.Empty,
                StoryTitle = response.StoryTitle ?? string.Empty,
                StoryMarkdown = response.StoryMarkdown ?? string.Empty,
                StorySummary = response.StorySummary ?? string.Empty,
                StoryPartIndex = response.StoryPartIndex,
                StoryIsFinished = response.StoryIsFinished,
                StoryChunkCount = response.StoryChunkCount,
                TopAffectedEmotions = (response.TopAffectedEmotions ?? new List<TopAffectedEmotion>()).ToList(),
                RecentConversationHistory = (response.RecentConversationHistory ?? new List<MobileConversationHistoryItem>()).ToList(),
                EmotionalEffectVector = response.EmotionalEffectVector ?? string.Empty,
                NegativeEmotionVector = response.NegativeEmotionVector ?? string.Empty,
                LastEmotionVectorsText = response.LastEmotionVectorsText ?? string.Empty,
                InterstarStatusText = InterstarStatusText,
                DailyStateSlotId = response.DailyStateSlotId ?? string.Empty,
                DailyStateLocalDate = response.DailyStateLocalDate ?? string.Empty,
                DailyStateGeneratedAt = response.DailyStateGeneratedAt,
                DailyStateSummary = response.DailyStateSummary ?? string.Empty,
                DailyStateJson = response.DailyStateJson ?? string.Empty,
                DailyStateXml = string.Empty
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[MobileBridge] Could not publish direct conversation response event: " + ex.Message);
        }
    }

    private MobileBridgeDispatchResult CreateConversationDispatchResult(MobileBridgeDispatchCommand command, string commandType, string sessionId, string starId, StarState state, MobileBridgeResponse response)
    {
        // ProcessMobileConversationAsync intentionally defers Android TTS until its correlated
        // transport response is written. Dispatch-based operations used by Listen/initiative/photo
        // wrap that response, so move the lifecycle callbacks to the wrapper instead of dropping
        // them. Dropping these callbacks produced text with no phone audio.
        Func<Task>? afterTransportResponse = response.AfterTransportResponseAsync;
        Func<Task>? onTransportFailure = response.OnTransportFailureAsync;
        response.AfterTransportResponseAsync = null;
        response.OnTransportFailureAsync = null;

        return new MobileBridgeDispatchResult
        {
            AfterTransportResponseAsync = afterTransportResponse,
            OnTransportFailureAsync = onTransportFailure,
            Ok = response.Ok,
            CommandId = command.CommandId,
            CommandType = commandType,
            StarId = starId,
            SelectedStarId = SelectedStarId,
            IsMet = state.IsMet,
            IsAlly = state.IsAlly,
            IsGirlfriend = state.IsGirlfriend,
            GirlfriendCuteModeEnabled = state.GirlfriendCuteModeEnabled,
            GirlfriendCuteModeActive = state.GirlfriendCuteModeEnabled && (state.GirlfriendHourlyState?.CuteModeActive ?? false),
            GirlfriendCuteModeSignature = state.GirlfriendHourlyState?.CuteModeSignature ?? string.Empty,
            State = state,
            Message = response.Ok ? "Conversation response ready." : response.VoiceError,
            Error = response.Ok ? string.Empty : response.VoiceError,
            RequestType = response.RequestType,
            Transcript = response.Transcript,
            SpokenText = response.SpokenText,
            StarOutput = response.StarOutput,
            ConversationSummary = response.ConversationSummary,
            ChangesSummary = response.ChangesSummary,
            InitiativeShouldSpeak = response.InitiativeShouldSpeak,
            InitiativeSkippedBeforeLlm = response.InitiativeSkippedBeforeLlm,
            InitiativeStateScore = response.InitiativeStateScore,
            InitiativePriority = response.InitiativePriority,
            InitiativeTopic = response.InitiativeTopic ?? string.Empty,
            InitiativeReason = response.InitiativeReason ?? string.Empty,
            VoiceRequested = response.VoiceRequested,
            VoiceSocketConnected = response.VoiceSocketConnected,
            VoiceChunksSent = response.VoiceChunksSent,
            VoiceBytesSent = response.VoiceBytesSent,
            VoiceChunksCached = response.VoiceChunksCached,
            VoiceBytesCached = response.VoiceBytesCached,
            InlineVoiceChunks = (response.InlineVoiceChunks ?? new List<MobileInlineVoiceChunk>()).ToList(),
            PrefetchForListen = response.PrefetchForListen,
            PrefetchedAtUtc = response.PrefetchedAtUtc,
            VoiceMessage = response.VoiceMessage,
            VoiceError = response.VoiceError,
            ServerElapsedMs = response.ServerElapsedMs,
            RuntimeElapsedMs = response.RuntimeElapsedMs,
            GateWaitMs = response.GateWaitMs,
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

    private MobileBridgeDispatchResult PublishMobileCommandResult(MobileBridgeDispatchCommand command, bool ok, string commandType, string starId, string sessionId, StarState state, string message, StarWritingNotesState? writingState = null)
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
            GirlfriendCuteModeEnabled = state.GirlfriendCuteModeEnabled,
            GirlfriendCuteModeActive = state.GirlfriendCuteModeEnabled && (state.GirlfriendHourlyState?.CuteModeActive ?? false),
            GirlfriendCuteModeSignature = state.GirlfriendHourlyState?.CuteModeSignature ?? string.Empty,
            BackgroundWritingLoopEnabled = BackgroundWritingLoopEnabled,
            BackgroundWritingLoopStatus = BackgroundWritingLoopStatus,
            XPostingEnabled = XPostingEnabled,
            XPostingStatus = XPostingStatus,
            PendingWritingCount = writingState?.PendingCount,
            PersistedWritingCount = writingState?.PersistedWritings?.Count,
            UnpostedWritingCount = writingState?.UnpostedCount,
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
            EventType = commandType == "SelectStar"
                ? "SelectedStarChanged"
                : commandType == "SetBackgroundWritingLoopEnabled"
                    ? "BackgroundWritingLoopChanged"
                    : (commandType == "SetXPostingEnabled" || commandType == "PostStarWritingsToXNow")
                        ? "XPostingChanged"
                    : (commandType == "SetGirlfriendCuteMode" || commandType == "ActivateCuteModeNow")
                        ? "GirlfriendCuteModeChanged"
                        : (commandType == "ClearHistory" || commandType == "ClearAllData")
                            ? "StarHistoryCleared"
                            : "StarStateChanged",
            CommandId = command.CommandId,
            SourceClientId = command.SourceClientId,
            SessionId = sessionId,
            StarId = starId,
            SelectedStarId = SelectedStarId,
            IsMet = state.IsMet,
            IsAlly = state.IsAlly,
            IsGirlfriend = state.IsGirlfriend,
            GirlfriendCuteModeEnabled = state.GirlfriendCuteModeEnabled,
            GirlfriendCuteModeActive = state.GirlfriendCuteModeEnabled && (state.GirlfriendHourlyState?.CuteModeActive ?? false),
            GirlfriendCuteModeSignature = state.GirlfriendHourlyState?.CuteModeSignature ?? string.Empty,
            BackgroundWritingLoopEnabled = BackgroundWritingLoopEnabled,
            BackgroundWritingLoopStatus = BackgroundWritingLoopStatus,
            XPostingEnabled = XPostingEnabled,
            XPostingStatus = XPostingStatus,
            PendingWritingCount = writingState?.PendingCount,
            PersistedWritingCount = writingState?.PersistedWritings?.Count,
            UnpostedWritingCount = writingState?.UnpostedCount,
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
            StarState state = await _services.MobileConversation.GetStateAsync("android-phone-dev", starId, CancellationToken.None).ConfigureAwait(false);
            StarWritingNotesState writingState = await _writingService.GetStateAsync(starId, CancellationToken.None).ConfigureAwait(false);
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
                GirlfriendCuteModeEnabled = state.GirlfriendCuteModeEnabled,
                GirlfriendCuteModeActive = state.GirlfriendCuteModeEnabled && (state.GirlfriendHourlyState?.CuteModeActive ?? false),
                GirlfriendCuteModeSignature = state.GirlfriendHourlyState?.CuteModeSignature ?? string.Empty,
                PendingWritingCount = writingState.PendingCount,
                PersistedWritingCount = writingState.PersistedWritings.Count,
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

    private static object CreateMobileStarStateDto(StarState state, StarWritingNotesState writingState)
    {
        return new
        {
            starId = state.StarId,
            isMet = state.IsMet,
            isAlly = state.IsAlly,
            isGirlfriend = state.IsGirlfriend,
            girlfriendCuteModeEnabled = state.GirlfriendCuteModeEnabled,
            girlfriendCuteModeActive = state.GirlfriendCuteModeEnabled && (state.GirlfriendHourlyState?.CuteModeActive ?? false),
            girlfriendCuteModeSignature = state.GirlfriendHourlyState?.CuteModeSignature ?? string.Empty,
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
            interactionCount = state.InteractionCount,
            pendingWritingCount = writingState?.PendingCount ?? 0,
            persistedWritingCount = writingState?.PersistedWritings?.Count ?? 0,
            unpostedWritingCount = writingState?.UnpostedCount ?? 0
        };
    }

    private void PublishMobileBridgeStateEventFromDesktop(string eventType, StarState state, string message, int? pendingWritingCount = null, int? persistedWritingCount = null)
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
                GirlfriendCuteModeEnabled = state.GirlfriendCuteModeEnabled,
                GirlfriendCuteModeActive = state.GirlfriendCuteModeEnabled && (state.GirlfriendHourlyState?.CuteModeActive ?? false),
                GirlfriendCuteModeSignature = state.GirlfriendHourlyState?.CuteModeSignature ?? string.Empty,
                BackgroundWritingLoopEnabled = BackgroundWritingLoopEnabled,
                BackgroundWritingLoopStatus = BackgroundWritingLoopStatus,
                XPostingEnabled = XPostingEnabled,
                XPostingStatus = XPostingStatus,
                PendingWritingCount = pendingWritingCount,
                PersistedWritingCount = persistedWritingCount,
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
                RequestToShowBoobs = response.RequestToShowBoobs,
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
            if (!_services.MobileVoice.IsConnected)
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
            await _services.MobileVoice.SendStatusAsync("Elusiaa is sending the desktop test tone. sha256=" + ShortHash(checksum), cancellationToken).ConfigureAwait(false);
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

            MobileAudioSendResult sent = await _services.MobileVoice.SendChunkAsync(result, 0, 1, cancellationToken).ConfigureAwait(false);
            await _services.MobileVoice.SendStatusAsync("Elusiaa finished the desktop test tone send. sha256=" + ShortHash(checksum) + ", clients=" + sent.SentClients, cancellationToken).ConfigureAwait(false);
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
        using IDisposable writingLoopLease = BeginBackgroundWritingForegroundActivity("an Android voice interaction");
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
                Language = Environment.GetEnvironmentVariable("SIX_STARS_WHISPER_LANGUAGE") ?? _appConfig.Whisper.Language,
                Prompt = CreateWhisperNamePrompt()
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
        request.GirlfriendProposal = GirlfriendProposalIntentDetector.IsProposal(transcriptText);
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

    private MobileBridgeResponse CreateMobileBusySkippedInitiativeResponse(MobileBridgeRequest mobileRequest, string starId)
    {
        string reason = "At-rest Star initiative skipped because a foreground player request is already using the desktop runtime. The quiet check will try again later.";
        OnUiThread(() =>
        {
            MobileBridgeStatus = reason;
            AppendMobileBridgeVoiceDebug("At-rest Star initiative skipped", starId + ": " + reason);
        });

        return new MobileBridgeResponse
        {
            Ok = true,
            StarId = starId,
            RequestType = RequestType.StarInitiative.ToString(),
            InitiativeShouldSpeak = false,
            InitiativeSkippedBeforeLlm = true,
            InitiativeStateScore = 0,
            InitiativePriority = 0,
            InitiativeReason = reason,
            VoiceRequested = false,
            VoiceSocketConnected = _services.MobileVoice.IsConnected,
            VoiceMessage = reason
        };
    }

    private async Task<MobileBridgeResponse> ProcessMobileConversationAsync(MobileBridgeRequest mobileRequest, string transcript, CancellationToken cancellationToken)
    {
        System.Diagnostics.Stopwatch totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        long gateWaitMs = 0;
        long runtimeElapsedMs = 0;
        string backgroundAnalysisId = string.Empty;
        string starId = ResolveDefaultStarId(mobileRequest.StarId);
        RequestType requestType = ParseMobileRequestType(mobileRequest.RequestType);
        if (HasMobilePhoto(mobileRequest)) requestType = RequestType.Photo;
        mobileRequest.GirlfriendProposal = mobileRequest.GirlfriendProposal
            || GirlfriendProposalIntentDetector.IsProposal(mobileRequest.PlayerText);
        if (mobileRequest.GirlfriendProposal && requestType != RequestType.Photo && requestType != RequestType.StarInitiative)
        {
            requestType = RequestType.Chat;
        }
        bool isAtRestInitiative = requestType == RequestType.StarInitiative;
        bool isPrefetchInitiative = isAtRestInitiative && mobileRequest.PrefetchForListen;
        bool isManualInitiative = isAtRestInitiative && !isPrefetchInitiative
            && (ContainsIgnoreCase(mobileRequest.ClientName, "Manual")
                || ContainsIgnoreCase(mobileRequest.ClientName, "ListenButton")
                || ContainsIgnoreCase(mobileRequest.Reason, "manual")
                || ContainsIgnoreCase(mobileRequest.Reason, "button"));
        using IDisposable? writingLoopLease = !isPrefetchInitiative && (!isAtRestInitiative || isManualInitiative)
            ? BeginBackgroundWritingForegroundActivity("an Android foreground interaction with " + starId)
            : null;
        MobileConversationService.Lease? mobileLease = null;
        MobileLiveVoiceRun? livePhoneVoice = null;
        System.Diagnostics.Stopwatch gateStopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (isPrefetchInitiative)
        {
            // Probe the foreground gate, but do not hold it while the low-priority prefetch waits in
            // the API background dispatcher. A player message arriving one millisecond later must be
            // able to take the mobile foreground gate immediately.
            using MobileConversationService.Lease? probeLease = await _services.MobileConversation.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
            gateWaitMs = gateStopwatch.ElapsedMilliseconds;
            if (probeLease == null)
            {
                MobileBridgeResponse skipped = CreateMobileBusySkippedInitiativeResponse(mobileRequest, starId);
                skipped.GateWaitMs = gateWaitMs;
                skipped.ServerElapsedMs = totalStopwatch.ElapsedMilliseconds;
                return skipped;
            }
        }
        else if (isAtRestInitiative && !isManualInitiative)
        {
            // Automatic visible at-rest initiative must never get in front of a real player action.
            // If the mobile runtime is busy, skip this quiet check and try later.
            mobileLease = await _services.MobileConversation.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
            gateWaitMs = gateStopwatch.ElapsedMilliseconds;
            if (mobileLease == null)
            {
                MobileBridgeResponse skipped = CreateMobileBusySkippedInitiativeResponse(mobileRequest, starId);
                skipped.GateWaitMs = gateWaitMs;
                skipped.ServerElapsedMs = totalStopwatch.ElapsedMilliseconds;
                return skipped;
            }
        }
        else
        {
            // Real text/voice/photo and the user-pressed Listen button preempt automatic at-rest or
            // background work. Android uses separate client queues, so this request reaches the
            // desktop immediately instead of waiting behind the older initiative HTTP call.
            string priorityLabel = isManualInitiative ? "Android manual Listen request" : "Android foreground request";
            try { _operationController.CancelAllRuntimeOperations(priorityLabel + " preempted mobile background/at-rest work for " + starId + "."); } catch { }
            try { _services.MobileConversation.CancelBackgroundAnalysis(priorityLabel + " preempted background analysis for " + starId + "."); } catch { }
            mobileLease = await _services.MobileConversation.AcquireAsync(cancellationToken).ConfigureAwait(false);
            gateWaitMs = gateStopwatch.ElapsedMilliseconds;
        }

        try
        {
            bool wantsInlineAudio = mobileRequest.ReturnAudioInline && isAtRestInitiative;
            bool wantsPhoneAudio = mobileRequest.ReturnAudio && !wantsInlineAudio;
            DateTimeOffset started = DateTimeOffset.Now;

            OnUiThread(() =>
            {
                MobileBridgeLastRequest = started.ToString("HH:mm:ss") + " " + mobileRequest.ClientName + " -> " + starId + " " + requestType + ": " + TruncateForUi(mobileRequest.PlayerText, 180);
                MobileBridgeStatus = mobileRequest.PrefetchForListen
                    ? "Preparing a private at-rest Listen cache for " + starId + "."
                    : "Android request is using the desktop runtime for " + starId + ".";
                AppendMobileBridgeVoiceDebug(mobileRequest.PrefetchForListen ? "Listen cache request arrived" : "Phone request arrived", starId + " " + requestType + ", phoneAudio=" + wantsPhoneAudio + ", inlineAudio=" + wantsInlineAudio + ", textChars=" + (mobileRequest.PlayerText?.Length ?? 0));
                if (!mobileRequest.PrefetchForListen) Status = "Android bridge request received for " + starId + ".";
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
            CancellationToken runtimeToken = isPrefetchInitiative
                ? cancellationToken
                : _operationController.BeginNewOperation("android bridge request to " + starId);
            using CancellationTokenSource linkedCts = isPrefetchInitiative
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runtimeToken);

            if (wantsPhoneAudio && ShouldUseLiveForegroundSpeech(request.RequestType))
            {
                livePhoneVoice = await TryStartMobileLiveVoiceAsync(request, linkedCts.Token).ConfigureAwait(false);
                if (livePhoneVoice != null)
                {
                    OnUiThread(() => AppendMobileBridgeVoiceDebug("Live TTS armed before LLM", "Each § speech chunk will enter Qwen TTS immediately; Android audio delivery remains gated until the correlated text response has been delivered."));
                }
            }

            System.Diagnostics.Stopwatch runtimeStopwatch = System.Diagnostics.Stopwatch.StartNew();
            StarInteractionResponse starResponse = isPrefetchInitiative
                ? await _services.MobileConversation.ProcessWhenForegroundIdleAsync(
                    request,
                    "Prepare " + starId + "'s private Android Listen response only after foreground conversation is idle.",
                    linkedCts.Token).ConfigureAwait(false)
                : await _services.MobileConversation.ProcessAsync(request, linkedCts.Token).ConfigureAwait(false);
            StarState stateAfterResponse = await _services.MobileConversation.GetStateAsync(request.SessionId, starResponse.StarId ?? request.StarId, linkedCts.Token).ConfigureAwait(false);
            backgroundAnalysisId = starResponse.BackgroundAnalysisId ?? string.Empty;
            runtimeElapsedMs = runtimeStopwatch.ElapsedMilliseconds;

            if (livePhoneVoice != null && livePhoneVoice.Pipeline.TextChunksEnqueued == 0)
            {
                foreach (string part in starResponse.SpokenParts.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    livePhoneVoice.Pipeline.EnqueueTextChunk(part);
                }
            }

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

            MobileBridgeResponse response = CreateMobileResponse(mobileRequest, request, transcript, starResponse, stateAfterResponse);
            if (livePhoneVoice != null)
            {
                response.VoiceRequested = true;
                response.VoiceSocketConnected = livePhoneVoice.HadTargets;
                response.VoiceChunksSent = livePhoneVoice.ChunksSent;
                response.VoiceBytesSent = livePhoneVoice.BytesSent;
                response.VoiceError = livePhoneVoice.ErrorMessage;
                response.VoiceMessage = string.IsNullOrWhiteSpace(livePhoneVoice.ErrorMessage)
                    ? "Live TTS was pre-generating while the LLM was running; Android audio delivery begins immediately after text transport."
                    : "Live Android voice streaming ended with an error after " + livePhoneVoice.ChunksSent + " chunk(s).";
            }
            response.GateWaitMs = gateWaitMs;
            response.RuntimeElapsedMs = runtimeElapsedMs;
            response.ServerElapsedMs = totalStopwatch.ElapsedMilliseconds;
            if (requestType == RequestType.StarInitiative && (!starResponse.InitiativeShouldSpeak || string.IsNullOrWhiteSpace(starResponse.SpokenText)))
            {
                wantsPhoneAudio = false;
                wantsInlineAudio = false;
                response.VoiceRequested = false;
            }

            if (wantsInlineAudio)
            {
                response.PrefetchForListen = mobileRequest.PrefetchForListen;
                response.PrefetchedAtUtc = DateTimeOffset.UtcNow;
                response.InlineVoiceChunks = await GenerateMobileInlineVoiceChunksAsync(starResponse, linkedCts.Token).ConfigureAwait(false);
                response.VoiceChunksCached = response.InlineVoiceChunks.Count;
                response.VoiceBytesCached = response.InlineVoiceChunks.Sum(x => string.IsNullOrWhiteSpace(x.AudioBase64) ? 0 : (x.AudioBase64.Length * 3 / 4));
                response.VoiceRequested = response.InlineVoiceChunks.Count > 0;
                response.VoiceSocketConnected = false;
                response.VoiceMessage = response.InlineVoiceChunks.Count > 0
                    ? "Prepared " + response.InlineVoiceChunks.Count + " voice chunk(s) inside the private Listen cache response."
                    : "The at-rest line returned without cacheable voice audio.";
            }

            OnUiThread(() => AppendMobileBridgeVoiceDebug(requestType == RequestType.StarInitiative ? "Star initiative check ready" : "Text response ready", (starResponse.StarId ?? starId) + " spokenChars=" + (response.SpokenText?.Length ?? 0) + ", phoneAudio=" + wantsPhoneAudio + ", inlineAudio=" + wantsInlineAudio + ", cachedChunks=" + response.VoiceChunksCached + ", initiativeShouldSpeak=" + starResponse.InitiativeShouldSpeak));

            if (!mobileRequest.PrefetchForListen) OnUiThread(() =>
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
                        response.ServerElapsedMs,
                        response.RuntimeElapsedMs,
                        response.GateWaitMs,
                        storyMarkdownChars = response.StoryMarkdown?.Length ?? 0,
                        storyChunkCount = response.StoryChunkCount,
                        storyMarkdownReady = response.StoryMarkdownReady,
                        initiativeShouldSpeak = response.InitiativeShouldSpeak,
                        initiativeSkippedBeforeLlm = response.InitiativeSkippedBeforeLlm,
                        initiativeStateScore = response.InitiativeStateScore,
                        initiativeTopic = response.InitiativeTopic,
                        initiativeReason = response.InitiativeReason,
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

            if (wantsInlineAudio)
            {
                ReleasePendingBackgroundAnalysis(backgroundAnalysisId, "Prepared inline Android Listen cache audio.");
            }
            else if (livePhoneVoice != null)
            {
                // Qwen TTS has already been allowed to work during LLM generation, but preserve the
                // Android transport contract that has proven stable: deliver the correlated text
                // response first, then start the ordered phone stream and drain the warmed WAV queue.
                MobileLiveVoiceRun exactLiveRun = livePhoneVoice;
                string exactAnalysisId = backgroundAnalysisId;
                response.VoiceSocketConnected = exactLiveRun.HadTargets && _services.MobileVoice.IsConnected;
                response.VoiceMessage = "Live TTS warmed during LLM generation; Android voice starts immediately after this text response is delivered.";
                response.AfterTransportResponseAsync = async () =>
                {
                    await FinishMobileLiveVoiceAsync(exactLiveRun, exactAnalysisId).ConfigureAwait(false);
                    response.VoiceChunksSent = exactLiveRun.ChunksSent;
                    response.VoiceBytesSent = exactLiveRun.BytesSent;
                    response.VoiceError = exactLiveRun.ErrorMessage;
                    OnUiThread(() => AppendMobileBridgeVoiceDebug(
                        "Live phone voice drained after text transport",
                        "chunks=" + exactLiveRun.ChunksSent + ", bytes=" + exactLiveRun.BytesSent + ", error=" + exactLiveRun.ErrorMessage));
                };
                response.OnTransportFailureAsync = () =>
                {
                    AbortMobileLiveVoice(exactLiveRun, "Android text response transport failed while live voice was active.");
                    ReleasePendingBackgroundAnalysis(exactAnalysisId, "Android response transport failed during live voice.");
                    return Task.CompletedTask;
                };
                OnUiThread(() => AppendMobileBridgeVoiceDebug(
                    "Live phone voice waiting for text transport",
                    "Qwen is allowed to pre-generate; chunks sent before transport=" + exactLiveRun.ChunksSent + "."));
            }
            else if (wantsPhoneAudio)
            {
                response.VoiceSocketConnected = _services.MobileVoice.IsConnected;
                response.VoiceMessage = response.VoiceSocketConnected
                    ? "Text will be delivered first. Voice generation starts immediately after the correlated response is written to Android."
                    : "Text will be delivered first, but no Android audio socket is connected yet.";

                string exactAnalysisId = backgroundAnalysisId;
                response.AfterTransportResponseAsync = async () =>
                {
                    Task phoneVoiceTask = SendMobileVoiceAsync(starResponse, response, CancellationToken.None, sharedStoryVoiceStream, exactAnalysisId);
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

                    try
                    {
                        await phoneVoiceTask.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        OnUiThread(() => MobileBridgeStatus = "Android background voice error: " + ex.Message);
                        ReleasePendingBackgroundAnalysis(exactAnalysisId, "Android background voice failed: " + ex.Message);
                    }
                };
                response.OnTransportFailureAsync = () =>
                {
                    ReleasePendingBackgroundAnalysis(exactAnalysisId, "Android response transport failed before voice could start.");
                    return Task.CompletedTask;
                };

                OnUiThread(() => AppendMobileBridgeVoiceDebug("Text response ready for phone", response.VoiceSocketConnected ? "TTS is deferred until the WebSocket/HTTP response has been sent." : "No phone audio socket was connected."));
            }
            else if (mobileRequest.SpeakOnDesktop || _appConfig.MobileBridge.SpeakResponsesOnDesktop)
            {
                await SpeakStarResponseAsync(starResponse, linkedCts.Token).ConfigureAwait(false);
                ReleasePendingBackgroundAnalysis(backgroundAnalysisId, "Android-requested desktop speech completed.");
            }
            else
            {
                ReleasePendingBackgroundAnalysis(backgroundAnalysisId, "Android bridge response returned without voice playback.");
            }

            if (!wantsInlineAudio) response.VoiceSocketConnected = _services.MobileVoice.IsConnected;
            if (!mobileRequest.PrefetchForListen)
            {
                if (mobileRequest.DeferConversationResponseEventUntilTransportFailure)
                {
                    // A persistent WebSocket request already returns this exact reply through its
                    // correlated response. Publishing the recovery event before that transport made
                    // Android render a silent first copy and then handle the direct copy that starts
                    // voice. Preserve recovery semantics by publishing only if direct transport fails.
                    Func<Task>? existingTransportFailure = response.OnTransportFailureAsync;
                    response.OnTransportFailureAsync = async () =>
                    {
                        try
                        {
                            if (existingTransportFailure != null)
                            {
                                await existingTransportFailure().ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            PublishMobileConversationResponseEventIfNeeded(mobileRequest, request, response, starResponse, stateAfterResponse);
                        }
                    };
                }
                else
                {
                    PublishMobileConversationResponseEventIfNeeded(mobileRequest, request, response, starResponse, stateAfterResponse);
                }
            }
            OnUiThread(() =>
            {
                MobileBridgeStatus = wantsInlineAudio
                    ? "Prepared Android Listen cache: " + response.VoiceChunksCached + " chunk(s), " + response.VoiceBytesCached + " bytes."
                    : livePhoneVoice != null
                        ? "Android text response ready; warmed live TTS will start on the phone immediately after transport."
                        : wantsPhoneAudio
                            ? "Android text response returned. Phone voice generation continues in the background."
                            : "Android response complete. Voice chunks sent: " + response.VoiceChunksSent + ", bytes: " + response.VoiceBytesSent + ".";
            });
            response.ServerElapsedMs = totalStopwatch.ElapsedMilliseconds;
            return response;
        }
        catch (OperationCanceledException)
        {
            AbortMobileLiveVoice(livePhoneVoice, "Android bridge request was cancelled.");
            ReleasePendingBackgroundAnalysis(backgroundAnalysisId, "Android bridge request was cancelled after the foreground response was created.");
            MobileBridgeResponse cancelled = new MobileBridgeResponse
            {
                Ok = false,
                StarId = starId,
                RequestType = requestType.ToString(),
                VoiceError = "Android bridge request was cancelled.",
                GateWaitMs = gateWaitMs,
                RuntimeElapsedMs = runtimeElapsedMs,
                ServerElapsedMs = totalStopwatch.ElapsedMilliseconds
            };
            PublishMobileConversationFailureEventIfNeeded(mobileRequest, starId, requestType, cancelled.VoiceError);
            return cancelled;
        }
        catch (Exception ex)
        {
            AbortMobileLiveVoice(livePhoneVoice, "Android bridge failed: " + ex.Message);
            ReleasePendingBackgroundAnalysis(backgroundAnalysisId, "Android bridge failed after the foreground response was created: " + ex.Message);
            OnUiThread(() => MobileBridgeStatus = "Android bridge error: " + ex.Message);
            MobileBridgeResponse failed = new MobileBridgeResponse
            {
                Ok = false,
                StarId = starId,
                RequestType = requestType.ToString(),
                VoiceError = ex.Message,
                GateWaitMs = gateWaitMs,
                RuntimeElapsedMs = runtimeElapsedMs,
                ServerElapsedMs = totalStopwatch.ElapsedMilliseconds,
                Notes = { ex.GetType().Name }
            };
            PublishMobileConversationFailureEventIfNeeded(mobileRequest, starId, requestType, ex.Message);
            return failed;
        }
        finally
        {
            IsProcessingRuntime = false;
            mobileLease?.Dispose();
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
        context.Flags["phoneAudioSocketConnected"] = _services.MobileVoice.IsConnected.ToString().ToLowerInvariant();
        context.Flags["speakOnDesktop"] = request.SpeakOnDesktop.ToString().ToLowerInvariant();
        context.Flags["returnAudioToPhone"] = request.ReturnAudio.ToString().ToLowerInvariant();
        context.Flags["returnAudioInline"] = request.ReturnAudioInline.ToString().ToLowerInvariant();
        context.Flags["prefetchForListen"] = request.PrefetchForListen.ToString().ToLowerInvariant();
        if (request.GirlfriendProposal || GirlfriendProposalIntentDetector.IsProposal(request.PlayerText))
        {
            context.Flags["girlfriendProposalDetected"] = "true";
            context.RecentEvents.Add("The mobile transport explicitly preserved a girlfriend-proposal intent for the relationship runtime.");
        }
        if (IsMobileStarInitiativeRequest(request))
        {
            context.Flags["starInitiative"] = "true";
            string initiativeReason = !string.IsNullOrWhiteSpace(request.Reason) ? request.Reason : (string.IsNullOrWhiteSpace(request.Quest) ? "mobile at-rest initiative" : request.Quest);
            context.Flags["initiativeReason"] = initiativeReason;
            if (request.PrefetchForListen)
            {
                context.Flags["starInitiativePrepareForListen"] = "true";
            }
            else if (ContainsIgnoreCase(request.ClientName, "Manual")
                || ContainsIgnoreCase(initiativeReason, "manual")
                || ContainsIgnoreCase(initiativeReason, "button"))
            {
                context.Flags["starInitiativeForce"] = "true";
            }
            context.RecentEvents.Add("At-rest Star initiative state check from Android/desktop bridge; this is not a player message and will not change relationship/emotion state.");
        }
        if (!string.IsNullOrWhiteSpace(request.SpeechIntentId) || !string.IsNullOrWhiteSpace(request.SpeechIntentGoal))
        {
            context.Flags["preparedSpeechIntent"] = "true";
            context.Flags["preparedSpeechIntentId"] = request.SpeechIntentId ?? string.Empty;
            context.Flags["preparedSpeechIntentGoal"] = request.SpeechIntentGoal ?? string.Empty;
            context.Flags["preparedSpeechIntentTopic"] = request.SpeechIntentTopic ?? string.Empty;
            context.Flags["preparedSpeechIntentPriority"] = request.SpeechIntentPriority ?? string.Empty;
            context.Flags["preparedSpeechIntentScore"] = request.SpeechIntentScore.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            context.RecentEvents.Add("A durable wants-to-say intention is being prepared for reliable later playback. She should speak the supplied goal naturally now without mentioning systems.");
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
        return string.Equals(request.RequestType, "StarInitiative", StringComparison.OrdinalIgnoreCase)
            || ContainsIgnoreCase(request.ClientName, "StarInitiative")
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

    private async Task<MobileLiveVoiceRun?> TryStartMobileLiveVoiceAsync(StarInteractionRequest request, CancellationToken cancellationToken)
    {
        if (request == null || !_useStreamingTts || !ShouldUseLiveForegroundSpeech(request.RequestType)) return null;
        if (!_services.MobileVoice.IsConnected) return null;

        StarInteractionResponse template = CreateLiveVoiceTemplateResponse(request.StarId);
        MobileVoiceStreamScope stream = BeginMobileVoiceStream(template, false, "Android delimiter-streamed foreground reply");
        if (stream.AudioSession == null || stream.AudioSession.TargetClientIds.Count == 0)
        {
            EndMobileVoiceStream(stream, "no connected Android audio targets for live speech");
            return null;
        }

        CancellationTokenSource voiceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stream.CancellationToken);
        IStreamingVoiceGenerator? generator = null;
        LiveStarVoiceResponsePipeline? pipeline = null;
        try
        {
            generator = CreateStreamingVoiceGenerator();
            pipeline = new LiveStarVoiceResponsePipeline(
                generator,
                new NullWavePlaybackEndpoint(),
                _speechPipeline,
                _interruptionManager)
            {
                AutoPlayChunks = false
            };

            MobileLiveVoiceRun run = new MobileLiveVoiceRun(stream, pipeline, voiceCts)
            {
                HadTargets = true
            };

            pipeline.GenerationError += (_, args) =>
            {
                run.ErrorMessage = args.ErrorMessage ?? "Live TTS generation error.";
            };
            pipeline.ChunkReadyForConsumer += (_, args) =>
            {
                try
                {
                    if (!IsMobileVoiceStreamCurrent(stream) || stream.CancellationToken.IsCancellationRequested) return;

                    // Preserve the stable Android wire order. This blocks only the pipeline's
                    // audio-consumer lane; Qwen generation continues independently and can queue
                    // WAVs while the LLM is still finishing the reply.
                    run.TransportReady.Wait(stream.CancellationToken);

                    int chunkIndex = args.Sequence;
                    int knownTotal = Math.Max(chunkIndex + 1, run.ExpectedChunks);
                    MobileAudioFrameContext frameContext = new MobileAudioFrameContext
                    {
                        IsStory = false,
                        TtsPartIndex = chunkIndex,
                        TtsTotalParts = knownTotal
                    };
                    MobileAudioSendResult sent = _services.MobileVoice.SendChunkAsync(
                        args.Result,
                        chunkIndex,
                        knownTotal,
                        stream.CancellationToken,
                        stream.AudioSession,
                        frameContext).GetAwaiter().GetResult();
                    if (sent.SentClients > 0)
                    {
                        run.ChunksSent++;
                        run.BytesSent += args.Result.WavAudio.Length;
                    }
                    string checksum = Sha256Hex(args.Result.WavAudio);
                    OnUiThread(() => AppendMobileBridgeVoiceDebug(
                        "Live voice chunk sent",
                        "stream=" + stream.Ordinal + ", chunk=" + (chunkIndex + 1) + ", clients=" + sent.SentClients + ", bytes=" + args.Result.WavAudio.Length + ", sha256=" + ShortHash(checksum)));
                }
                catch (OperationCanceledException)
                {
                    run.ErrorMessage = "Android live voice stream was replaced by a newer request.";
                }
                catch (Exception ex)
                {
                    run.ErrorMessage = "Phone live audio send failed: " + ex.Message;
                }
            };

            pipeline.Start(request.SessionId, template, voiceCts.Token);

            // Prewarm only the first completed speech chunk while the LLM is still generating.
            // LM Studio and Qwen share the same GPU on the target machine; generating every TTS
            // chunk concurrently with the rest of the LLM reply can slow the foreground model
            // enough to erase the streaming win. One warm first chunk preserves instant post-text
            // speech while later chunks wait in cheap RAM until the text response is complete.
            request.SpokenChunkReady = run.EnqueueForegroundTextChunk;
            return run;
        }
        catch
        {
            try { pipeline?.Dispose(); } catch { }
            if (pipeline == null)
            {
                try { generator?.Dispose(); } catch { }
            }
            voiceCts.Dispose();
            EndMobileVoiceStream(stream, "live foreground voice could not start");
            throw;
        }
    }

    private async Task FinishMobileLiveVoiceAsync(MobileLiveVoiceRun run, string backgroundAnalysisId)
    {
        if (run == null) return;
        try
        {
            if (run.Stream.AudioSession != null && !run.Stream.CancellationToken.IsCancellationRequested)
            {
                await _services.MobileVoice.SendControlAsync(
                    run.Stream.AudioSession,
                    "start",
                    "Starting warmed Android voice stream " + run.Stream.StreamId + " after text transport. Qwen may already have speech queued.",
                    run.VoiceCts.Token).ConfigureAwait(false);
            }

            // The first WAV may already be warm. Once text transport is complete, feed all
            // remaining delimiter chunks to Qwen as ONE multi-part request. Qwen still returns one
            // ordered WAV per immutable LLM part, but this restores the stable pre-streaming TTS
            // request shape instead of repeatedly starting a fresh synthesis request per sentence.
            List<string> deferredChunks = run.DrainDeferredTextChunks();
            if (deferredChunks.Count > 0)
            {
                run.Pipeline.EnqueueTextChunks(deferredChunks);
            }
            run.ExpectedChunks = run.Pipeline.TextChunksEnqueued;

            // Release the ordered Android consumer only after the complete expected chunk count is
            // known, so every phone frame carries authoritative response-total metadata.
            run.TransportReady.Set();

            await run.Pipeline.CompleteInputAsync().ConfigureAwait(false);
            if (run.ExpectedChunks > 0 && run.ChunksSent != run.ExpectedChunks)
            {
                throw new InvalidOperationException(
                    "Live Android voice completed with " + run.ChunksSent + " sent WAV(s) for " + run.ExpectedChunks + " streamed speech chunk(s).");
            }
            if (run.Stream.AudioSession != null && !run.Stream.CancellationToken.IsCancellationRequested)
            {
                await _services.MobileVoice.SendControlAsync(
                    run.Stream.AudioSession,
                    "end",
                    "Live Android voice stream completed after " + run.ChunksSent + " chunk(s).",
                    CancellationToken.None).ConfigureAwait(false);
            }
            ReleasePendingBackgroundAnalysis(backgroundAnalysisId, "Live Android delimiter-streamed voice generation/sending completed.");
        }
        catch (OperationCanceledException)
        {
            run.ErrorMessage = "Android live voice stream was replaced by a newer request.";
            ReleasePendingBackgroundAnalysis(backgroundAnalysisId, "Live Android voice stream was replaced by a newer request.");
        }
        catch (Exception ex)
        {
            run.ErrorMessage = "Android live voice failed: " + ex.Message;
            ReleasePendingBackgroundAnalysis(backgroundAnalysisId, "Live Android voice failed: " + ex.Message);
        }
        finally
        {
            // Never leave the consumer lane parked if start/transport failed.
            try { run.TransportReady.Set(); } catch { }
            run.Pipeline.Dispose();
            try { run.TransportReady.Dispose(); } catch { }
            run.VoiceCts.Dispose();
            EndMobileVoiceStream(run.Stream, "live foreground voice completed or was replaced");
            run.Finished = true;
        }
    }

    private void AbortMobileLiveVoice(MobileLiveVoiceRun? run, string reason)
    {
        if (run == null || run.Finished) return;
        try { run.TransportReady.Set(); } catch { }
        try { run.Pipeline.CancelCurrentSpeech(); } catch { }
        try { run.Pipeline.Dispose(); } catch { }
        try { run.TransportReady.Dispose(); } catch { }
        try { run.VoiceCts.Dispose(); } catch { }
        try { EndMobileVoiceStream(run.Stream, reason); } catch { }
        run.Finished = true;
    }

    private sealed class MobileLiveVoiceRun
    {
        public MobileLiveVoiceRun(MobileVoiceStreamScope stream, LiveStarVoiceResponsePipeline pipeline, CancellationTokenSource voiceCts)
        {
            Stream = stream;
            Pipeline = pipeline;
            VoiceCts = voiceCts;
        }

        public MobileVoiceStreamScope Stream { get; }
        public LiveStarVoiceResponsePipeline Pipeline { get; }
        public CancellationTokenSource VoiceCts { get; }
        private readonly object _textChunkGate = new object();
        private readonly List<string> _deferredTextChunks = new List<string>();
        private bool _prewarmChunkQueued;

        public bool HadTargets { get; set; }
        public int ChunksSent { get; set; }
        public int BytesSent { get; set; }
        public int ExpectedChunks { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public bool Finished { get; set; }
        public System.Threading.ManualResetEventSlim TransportReady { get; } = new System.Threading.ManualResetEventSlim(false);

        public void EnqueueForegroundTextChunk(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            bool prewarmNow = false;
            lock (_textChunkGate)
            {
                if (!_prewarmChunkQueued)
                {
                    _prewarmChunkQueued = true;
                    prewarmNow = true;
                }
                else
                {
                    _deferredTextChunks.Add(text);
                }
            }

            if (prewarmNow)
            {
                Pipeline.EnqueueTextChunk(text);
            }
        }

        public List<string> DrainDeferredTextChunks()
        {
            lock (_textChunkGate)
            {
                List<string> copy = new List<string>(_deferredTextChunks);
                _deferredTextChunks.Clear();
                return copy;
            }
        }
    }

    private MobileVoiceStreamScope BeginMobileVoiceStream(StarInteractionResponse response, bool isStory, string reason)
    {
        CancellationTokenSource cts = new CancellationTokenSource();
        CancellationTokenSource? previous = null;
        CancellationTokenSource? preparedBackgroundVoice = null;
        long ordinal;
        lock (_mobileVoiceStreamGate)
        {
            previous = _mobileVoiceStreamCts;
            preparedBackgroundVoice = _backgroundPreparedSpeechCts;
            _backgroundPreparedSpeechCts = null;
            if (_pendingMobileStoryVoiceStream != null && ReferenceEquals(_pendingMobileStoryVoiceStream.CancellationSource, previous))
            {
                _pendingMobileStoryVoiceStream = null;
                _pendingMobileStoryThinkingVoiceTask = null;
            }
            _mobileVoiceStreamCts = cts;
            ordinal = ++_mobileVoiceStreamOrdinal;
        }

        try { previous?.Cancel(); } catch { }
        try { preparedBackgroundVoice?.Cancel(); } catch { }
        if (preparedBackgroundVoice != null)
        {
            OnUiThread(() => AppendMobileBridgeVoiceDebug(
                "Foreground voice preempted prepared cache TTS",
                "A live Android voice stream took priority over background prepared-speech synthesis."));
        }

        string streamId = "android-voice-" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        MobileAudioStreamSession? audioSession = _services.MobileVoice.CreateStreamSession(
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

    private MobileBridgeResponse CreateMobileResponse(MobileBridgeRequest mobileRequest, StarInteractionRequest runtimeRequest, string transcript, StarInteractionResponse response, StarState? stateAfterResponse = null)
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
            InitiativeShouldSpeak = response.InitiativeShouldSpeak,
            InitiativeSkippedBeforeLlm = response.InitiativeSkippedBeforeLlm,
            InitiativeStateScore = response.InitiativeStateScore,
            InitiativePriority = response.InitiativePriority,
            InitiativeTopic = response.InitiativeTopic ?? string.Empty,
            InitiativeReason = response.InitiativeReason ?? string.Empty,
            HeartDelta = response.StateDeltas.Heart,
            TrustDelta = response.StateDeltas.Trust,
            RomanceDelta = response.StateDeltas.Romance,
            BenaveilDelta = response.StateDeltas.BenaveilInfluence,
            EmotionalConnectionDelta = response.StateDeltas.EmotionalConnectionPercent,
            EmotionalConnectionAfter = response.EmotionalConnectionPercentAfter,
            IsGirlfriendReadyAfter = stateAfterResponse?.IsGirlfriendReady ?? response.IsGirlfriendReadyAfter,
            IsGirlfriendAfter = stateAfterResponse?.IsGirlfriend
                ?? response.RelationshipEvents.Any(x => x.IndexOf("Accepted girlfriend proposal", StringComparison.OrdinalIgnoreCase) >= 0),
            VoiceRequested = (mobileRequest.ReturnAudio || mobileRequest.ReturnAudioInline) && response.InitiativeShouldSpeak && !string.IsNullOrWhiteSpace(response.SpokenText),
            PrefetchForListen = mobileRequest.PrefetchForListen,
            PrefetchedAtUtc = mobileRequest.PrefetchForListen ? DateTimeOffset.UtcNow : null,
            VoiceSocketConnected = _services.MobileVoice.IsConnected,
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
            VoiceMessage = requestType == RequestType.StarInitiative && !response.InitiativeShouldSpeak
                ? FirstNonEmptyForMobile(response.InitiativeReason, "The Star had nothing state-backed to say right now.")
                : mobileRequest.ReturnAudioInline
                    ? "Voice will be generated inline for a private Android Listen cache."
                    : mobileRequest.ReturnAudio
                        ? "Voice is streamed over the phone audio socket on port " + ResolveMobileBridgeAudioPort() + "."
                        : "Phone voice was not requested."
        };
    }

    private async Task PrepareHighPrioritySpeechForStarAsync(string starId, string reason)
    {
        await _preparedSpeechGenerationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            string normalizedStarId = ResolveDefaultStarId(starId);
            if (ShouldDeferPreparedSpeechForForeground())
            {
                OnUiThread(() => AppendMobileBridgeVoiceDebug(
                    "Prepared speech yielded to foreground",
                    normalizedStarId + " preparation was deferred because a foreground interaction or Android voice stream is active."));
                SchedulePreparedSpeechRetry(normalizedStarId, "foreground speech/activity has priority");
                return;
            }
            for (int prepared = 0; prepared < Math.Max(1, _appConfig.StarSpeechIntents.MaximumPreparedItemsPerStar); prepared++)
            {
                StarSpeechIntent? intent = await _runtime.GetNextSpeechIntentToPrepareAsync(normalizedStarId, CancellationToken.None).ConfigureAwait(false);
                if (intent == null) return;
                StarSpeechIntent? locked = await _runtime.MarkSpeechIntentPreparingAsync(intent.IntentId, CancellationToken.None).ConfigureAwait(false);
                if (locked == null) return;
                try
                {
                    if (ShouldDeferPreparedSpeechForForeground())
                    {
                        await _runtime.MarkSpeechIntentDeferredAsync(intent.IntentId, "Foreground speech/activity has priority over prepared speech generation.", CancellationToken.None).ConfigureAwait(false);
                        SchedulePreparedSpeechRetry(normalizedStarId, "foreground speech/activity became active");
                        return;
                    }

                    MobileBridgeRequest request = new MobileBridgeRequest
                    {
                        SessionId = "android-phone-prod",
                        StarId = normalizedStarId,
                        RequestType = RequestType.StarInitiative.ToString(),
                        PlayerText = string.Empty,
                        Area = Area,
                        Quest = "PreparedSpeechIntent",
                        RomanceMode = true,
                        SpeakOnDesktop = false,
                        ReturnAudio = false,
                        ReturnAudioInline = true,
                        PrefetchForListen = true,
                        ClientName = "PreparedSpeechBackground",
                        Reason = "durable speech intent background preparation: " + reason,
                        SpeechIntentId = intent.IntentId,
                        SpeechIntentGoal = intent.SpeechGoal,
                        SpeechIntentTopic = intent.Topic,
                        SpeechIntentPriority = intent.Priority,
                        SpeechIntentScore = intent.EffectiveScore
                    };

                    CancellationTokenSource preparedOperationCts = new CancellationTokenSource();
                    bool foregroundWonRace = false;
                    lock (_mobileVoiceStreamGate)
                    {
                        if (_mobileVoiceStreamCts != null && !_mobileVoiceStreamCts.IsCancellationRequested)
                        {
                            foregroundWonRace = true;
                        }
                        else
                        {
                            try { _backgroundPreparedSpeechCts?.Cancel(); } catch { }
                            try { _backgroundPreparedSpeechCts?.Dispose(); } catch { }
                            _backgroundPreparedSpeechCts = preparedOperationCts;
                        }
                    }

                    if (foregroundWonRace)
                    {
                        preparedOperationCts.Dispose();
                        await _runtime.MarkSpeechIntentDeferredAsync(intent.IntentId, "Foreground Android voice became active before prepared speech generation could start.", CancellationToken.None).ConfigureAwait(false);
                        SchedulePreparedSpeechRetry(normalizedStarId, "foreground Android voice won the prepared-speech start race");
                        return;
                    }

                    MobileBridgeResponse response;
                    try
                    {
                        response = await ProcessMobileConversationAsync(request, string.Empty, preparedOperationCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_mobileVoiceStreamGate)
                        {
                            if (ReferenceEquals(_backgroundPreparedSpeechCts, preparedOperationCts))
                            {
                                _backgroundPreparedSpeechCts = null;
                            }
                        }
                        preparedOperationCts.Dispose();
                    }
                    if (!response.Ok || !response.InitiativeShouldSpeak || response.InlineVoiceChunks == null || response.InlineVoiceChunks.Count == 0)
                    {
                        if (ShouldDeferPreparedSpeechForForeground())
                        {
                            await _runtime.MarkSpeechIntentDeferredAsync(intent.IntentId, "Foreground speech/activity preempted prepared speech generation.", CancellationToken.None).ConfigureAwait(false);
                            SchedulePreparedSpeechRetry(normalizedStarId, "foreground speech/activity preempted prepared cache generation");
                            return;
                        }

                        string failure = FirstNonEmptyForMobile(response.VoiceError, response.InitiativeReason, "The Star did not produce prepared speech.");
                        await _runtime.MarkSpeechIntentFailedAsync(intent.IntentId, failure, CancellationToken.None).ConfigureAwait(false);
                        if (locked.PrepareAttempts < Math.Max(1, _appConfig.StarSpeechIntents.MaximumPrepareAttempts))
                        {
                            string retryReason = response.InitiativeSkippedBeforeLlm
                                ? "foreground work temporarily held the AI gate"
                                : "prepared speech generation returned no playable audio";
                            SchedulePreparedSpeechRetry(normalizedStarId, retryReason);
                        }
                        return;
                    }

                    List<PreparedSpeechChunkData> chunks = response.InlineVoiceChunks
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.AudioBase64))
                        .Select(x => new PreparedSpeechChunkData
                        {
                            ChunkIndex = x.ChunkIndex,
                            TotalChunks = x.TotalChunks,
                            MimeType = x.MimeType,
                            Audio = Convert.FromBase64String(x.AudioBase64),
                            Sha256 = x.Sha256,
                            SpeechActivitySegmentsMs = x.SpeechActivitySegmentsMs
                        }).ToList();
                    PreparedSpeechArtifact artifact = await _runtime.SavePreparedSpeechAsync(intent.IntentId, normalizedStarId, response.SpokenText, "qwen3-tts-current", chunks, CancellationToken.None).ConfigureAwait(false);
                    StarSpeechIntent? ready = await _runtime.MarkSpeechIntentReadyAsync(intent.IntentId, artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
                    _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
                    {
                        EventType = "PreparedSpeechReady",
                        SessionId = "android-phone-prod",
                        StarId = normalizedStarId,
                        SelectedStarId = SelectedStarId,
                        Message = normalizedStarId + " has an important prepared thought ready to say.",
                        SpeechIntentId = intent.IntentId,
                        PreparedArtifactId = artifact.ArtifactId,
                        SpeechIntentPriority = ready?.Priority ?? intent.Priority,
                        SpeechIntentEffectiveScore = ready?.EffectiveScore ?? intent.EffectiveScore,
                        SpeechIntentStatus = ready?.Status ?? StarSpeechIntentStatuses.Ready
                    });
                }
                catch (OperationCanceledException)
                {
                    // This background preparation uses its own CTS. Live Android voice cancels that
                    // CTS so foreground speech can take over immediately; cancellation is a defer,
                    // not a failed preparation attempt.
                    await _runtime.MarkSpeechIntentDeferredAsync(intent.IntentId, "Foreground speech/activity cancelled prepared speech generation.", CancellationToken.None).ConfigureAwait(false);
                    OnUiThread(() => AppendMobileBridgeVoiceDebug("Prepared speech preempted", normalizedStarId + " cache generation yielded to foreground speech/activity."));
                    SchedulePreparedSpeechRetry(normalizedStarId, "foreground speech/activity cancelled prepared cache generation");
                    return;
                }
                catch (Exception ex)
                {
                    await _runtime.MarkSpeechIntentFailedAsync(intent.IntentId, ex.Message, CancellationToken.None).ConfigureAwait(false);
                    OnUiThread(() => AppendMobileBridgeVoiceDebug("Prepared speech generation failed", normalizedStarId + ": " + ex.Message));
                    if (locked.PrepareAttempts < Math.Max(1, _appConfig.StarSpeechIntents.MaximumPrepareAttempts))
                    {
                        SchedulePreparedSpeechRetry(normalizedStarId, "prepared speech generation failed");
                    }
                    return;
                }
            }
        }
        finally
        {
            _preparedSpeechGenerationGate.Release();
        }
    }

    private bool ShouldDeferPreparedSpeechForForeground()
    {
        if (IsForegroundStarInteractionActive()) return true;
        lock (_mobileVoiceStreamGate)
        {
            return _mobileVoiceStreamCts != null && !_mobileVoiceStreamCts.IsCancellationRequested;
        }
    }

    private void SchedulePreparedSpeechRetry(string starId, string reason)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _appConfig.StarSpeechIntents.PrepareRetryDelaySeconds))).ConfigureAwait(false);
            await PrepareHighPrioritySpeechForStarAsync(starId, "retry after " + reason).ConfigureAwait(false);
        });
    }

    private async Task<List<MobileInlineVoiceChunk>> GenerateMobileInlineVoiceChunksAsync(StarInteractionResponse starResponse, CancellationToken cancellationToken)
    {
        // Prepared Listen audio is background cache work. It must never register itself as the
        // currently interruptible foreground speech, because SpeechInterruptionManager starts a new
        // generation by cancelling the previous one. That allowed a prepared-thought TTS request to
        // cancel a live Spark/Story/Chat voice stream while the phone still showed the text.
        lock (_mobileVoiceStreamGate)
        {
            if (_mobileVoiceStreamCts != null && !_mobileVoiceStreamCts.IsCancellationRequested)
            {
                OnUiThread(() => AppendMobileBridgeVoiceDebug(
                    "Prepared cache TTS deferred",
                    (starResponse.StarId ?? "Star") + " cache audio yielded because foreground Android voice is active."));
                return new List<MobileInlineVoiceChunk>();
            }
        }

        PrepareVoiceCloneReferenceForTts(starResponse);
        List<byte[]> generated = new List<byte[]>();
        object generatedGate = new object();

        using IStreamingVoiceGenerator generator = CreateStreamingVoiceGenerator();
        // This is offline/cache synthesis, not audible Star speech. Do not attach the shared
        // playback observer or SpeechInterruptionManager. Foreground streams own those.
        using StreamingStarVoiceResponsePipeline pipeline = new StreamingStarVoiceResponsePipeline(
            generator,
            new NullWavePlaybackEndpoint(),
            null,
            null)
        {
            AutoPlayChunks = false
        };

        pipeline.ChunkReadyForConsumer += (_, args) =>
        {
            byte[] source = args.Result?.WavAudio ?? Array.Empty<byte>();
            if (source.Length == 0) return;
            byte[] copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            lock (generatedGate) generated.Add(copy);
        };

        string generationError = string.Empty;
        pipeline.GenerationError += (_, args) => generationError = args.ErrorMessage ?? "TTS generation error.";
        await pipeline.SpeakStreamingAsync(DefaultSessionId, starResponse, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(generationError)) throw new InvalidOperationException(generationError);

        List<byte[]> ordered;
        lock (generatedGate) ordered = generated.ToList();
        int total = ordered.Count;
        List<MobileInlineVoiceChunk> chunks = new List<MobileInlineVoiceChunk>(total);
        for (int index = 0; index < total; index++)
        {
            byte[] wav = ordered[index];
            MobileSpeechActivityResult markers = MobileAudioSpeechActivityMarkers.AnalyzeWav(wav);
            chunks.Add(new MobileInlineVoiceChunk
            {
                ChunkIndex = index,
                TotalChunks = total,
                MimeType = "audio/wav",
                AudioBase64 = Convert.ToBase64String(wav),
                Sha256 = Sha256Hex(wav),
                SpeechActivitySegmentsMs = markers.Segments
            });
        }

        return chunks;
    }

    private async Task SendMobileVoiceAsync(StarInteractionResponse starResponse, MobileBridgeResponse mobileResponse, CancellationToken cancellationToken, MobileVoiceStreamScope? existingStream = null, string backgroundAnalysisId = "")
    {
        if (existingStream == null && !_services.MobileVoice.IsConnected)
        {
            OnUiThread(() => AppendMobileBridgeVoiceDebug("Waiting for Android phone audio", "Giving a foreground phone-audio reconnect up to six seconds before snapshotting the writing/story voice targets."));
            await _services.MobileVoice.WaitForConnectionAsync(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        }

        bool ownsStream = existingStream == null;
        MobileVoiceStreamScope voiceStream = existingStream ?? BeginMobileVoiceStream(starResponse, _storySpeechAdapter.ShouldUseStoryAutoplay(starResponse), "Android phone voice request");
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, voiceStream.CancellationToken);
        CancellationToken voiceToken = linkedCts.Token;

        try
        {
            if (!_services.MobileVoice.IsConnected || voiceStream.AudioSession == null || voiceStream.AudioSession.TargetClientIds.Count == 0)
            {
                mobileResponse.VoiceSocketConnected = false;
                mobileResponse.VoiceError = "No Android audio socket is connected. The phone should connect to TCP " + ResolveMobileBridgeAudioPort() + " after /health.";
                OnUiThread(() => AppendMobileBridgeVoiceDebug("Voice generation skipped", "No Android audio socket was connected at stream start; stale reconnects will not receive this old stream."));
                ReleasePendingBackgroundAnalysis(backgroundAnalysisId, "Android bridge had no phone audio socket; released background analysis after text response.");
                return;
            }

            await _services.MobileVoice.SendControlAsync(voiceStream.AudioSession, "start", "Starting ordered Android voice stream " + voiceStream.StreamId + ". Audio already accepted by the phone remains queued and finishes before newer speech.", voiceToken).ConfigureAwait(false);

            if (_storySpeechAdapter.ShouldUseStoryAutoplay(starResponse))
            {
                await SendMobileStoryVoiceWithPrefetchAsync(starResponse, mobileResponse, voiceToken, voiceStream).ConfigureAwait(false);
                return;
            }

            PrepareVoiceCloneReferenceForTts(starResponse);
            mobileResponse.VoiceSocketConnected = true;
            await _services.MobileVoice.SendStatusAsync("Elusiaa opened the voice stream. Generating " + (starResponse.StarId ?? "Star") + " voice...", voiceToken).ConfigureAwait(false);
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
                        MobileAudioSendResult sent = _services.MobileVoice.SendChunkAsync(args.Result, chunkIndex, totalChunks, voiceToken, voiceStream.AudioSession, frameContext).GetAwaiter().GetResult();
                        chunksSent++;
                        bytesSent += args.Result.WavAudio.Length;
                        _services.MobileVoice.SendStatusAsync("Elusiaa sent phone voice chunk " + chunksSent + "/" + totalChunks + " sha256=" + ShortHash(checksum) + ", bytes=" + args.Result.WavAudio.Length + ".", voiceToken).GetAwaiter().GetResult();
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
                    if (!voiceToken.IsCancellationRequested) _ = _services.MobileVoice.SendErrorAsync(mobileResponse.VoiceError, CancellationToken.None);
                };

                await mobilePipeline.SpeakStreamingAsync(DefaultSessionId, starResponse, voiceToken).ConfigureAwait(false);
                await _services.MobileVoice.SendStatusAsync("Elusiaa closed the voice stream. Finished generating and sending " + chunksSent + " chunk(s), bytes=" + bytesSent + ".", voiceToken).ConfigureAwait(false);
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
                MobileAudioSendResult sent = await _services.MobileVoice.SendChunkAsync(result, 0, 1, voiceToken, voiceStream.AudioSession, frameContext).ConfigureAwait(false);
                await _services.MobileVoice.SendStatusAsync("Elusiaa sent one complete voice WAV. sha256=" + ShortHash(checksum) + ", bytes=" + result.WavAudio.Length + ".", voiceToken).ConfigureAwait(false);
                mobileResponse.VoiceChunksSent = sent.SentClients > 0 ? 1 : 0;
                mobileResponse.VoiceBytesSent = result.WavAudio.Length;
                mobileResponse.VoiceMessage = "Queued one non-streaming voice WAV on the Android phone audio socket.";
                OnUiThread(() => AppendMobileBridgeVoiceDebug("Non-streaming voice sent", "stream=" + voiceStream.Ordinal + ", clients=" + sent.SentClients + ", bytes=" + result.WavAudio.Length + ", sha256=" + ShortHash(checksum)));
            }

            if (!voiceToken.IsCancellationRequested)
            {
                ReleasePendingBackgroundAnalysis(backgroundAnalysisId, "Android phone voice chunks were generated/sent.");
            }
        }
        catch (OperationCanceledException)
        {
            mobileResponse.VoiceError = "Android phone voice stream was replaced by a newer request.";
            try
            {
                if (voiceStream.AudioSession != null)
                {
                    await _services.MobileVoice.SendControlAsync(voiceStream.AudioSession, "cancel", "This Android voice stream was cancelled because a newer stream started.", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch { }
            OnUiThread(() => AppendMobileBridgeVoiceDebug("Android voice stream cancelled", "stream=" + voiceStream.Ordinal + " was replaced."));
            ReleasePendingBackgroundAnalysis(backgroundAnalysisId, "Android voice stream was replaced by a newer request.");
        }
        catch (Exception ex)
        {
            mobileResponse.VoiceError = "Android phone voice failed: " + ex.Message;
            try
            {
                if (_services.MobileVoice.IsConnected)
                {
                    await _services.MobileVoice.SendErrorAsync(mobileResponse.VoiceError, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch { }
            OnUiThread(() => AppendMobileBridgeVoiceDebug("Android voice stream failed", "stream=" + voiceStream.Ordinal + ", error=" + ex.Message));
            ReleasePendingBackgroundAnalysis(backgroundAnalysisId, "Android phone voice failed: " + ex.Message);
        }
        finally
        {
            if (ownsStream)
            {
                try
                {
                    if (voiceStream.AudioSession != null && !voiceStream.CancellationToken.IsCancellationRequested)
                    {
                        await _services.MobileVoice.SendControlAsync(voiceStream.AudioSession, "end", "Android voice stream completed.", CancellationToken.None).ConfigureAwait(false);
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
            if (stream.AudioSession != null && !stream.CancellationToken.IsCancellationRequested)
            {
                await _services.MobileVoice.SendControlAsync(stream.AudioSession, "end", "Android story voice stream completed.", CancellationToken.None).ConfigureAwait(false);
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

            await SendMobileVoiceAsync(response, mobileResponse, CancellationToken.None, sharedStoryVoiceStream, response.BackgroundAnalysisId).ConfigureAwait(false);
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
        string sessionId = ExtractUnityEventData(response, "sessionId", "android-phone-prod");
        string starId = response.StarId ?? SelectedStarId;
        StarState persistedState = await _services.MobileConversation.GetStateAsync(sessionId, starId, CancellationToken.None).ConfigureAwait(false);
        PublishMobileGirlfriendProposalEvent(response, persistedState, "GirlfriendProposalResponseReady", "The full girlfriend proposal response and persisted relationship state are ready for Android.");
        OnUiThread(() =>
        {
            StarOutput = FormatStarOutput(response);
            Status = "Generated Android girlfriend proposal response is ready. Streaming narration to phone.";
            AppendMobileBridgeVoiceDebug("Girlfriend proposal ready", (response.StarId ?? "Star") + " response chars=" + (response.SpokenText?.Length ?? 0));
        });

        StarInteractionRequest runtimeRequest = new StarInteractionRequest
        {
            StarId = starId,
            RequestType = RequestType.Chat,
            SessionId = sessionId
        };
        MobileBridgeResponse mobileResponse = CreateMobileResponse(new MobileBridgeRequest
        {
            StarId = starId,
            RequestType = "Chat",
            ReturnAudio = true,
            SessionId = sessionId,
            GirlfriendProposal = true
        }, runtimeRequest, string.Empty, response, persistedState);

        try
        {
            await SendMobileVoiceAsync(response, mobileResponse, CancellationToken.None, null, response.BackgroundAnalysisId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnUiThread(() => MobileBridgeStatus = "Android generated girlfriend proposal voice error: " + ex.Message);
            try { ReleasePendingBackgroundAnalysis("Android generated girlfriend proposal voice failed: " + ex.Message); } catch { }
        }
    }

    private void PublishMobileGirlfriendProposalEvent(StarInteractionResponse response, StarState persistedState, string eventType, string message)
    {
        if (response == null || persistedState == null) return;
        string sessionId = ExtractUnityEventData(response, "sessionId", "android-phone-prod");
        _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
        {
            EventType = eventType,
            SourceClientId = "desktop-runtime",
            SessionId = sessionId,
            StarId = persistedState.StarId,
            SelectedStarId = SelectedStarId,
            IsMet = persistedState.IsMet,
            IsAlly = persistedState.IsAlly,
            Heart = persistedState.Heart,
            Trust = persistedState.Trust,
            Romance = persistedState.Romance,
            EmotionalConnectionPercent = persistedState.EmotionalConnectionPercent,
            IsGirlfriendReady = persistedState.IsGirlfriendReady,
            IsGirlfriend = persistedState.IsGirlfriend,
            GirlfriendCuteModeEnabled = persistedState.GirlfriendCuteModeEnabled,
            GirlfriendCuteModeActive = persistedState.GirlfriendCuteModeEnabled && (persistedState.GirlfriendHourlyState?.CuteModeActive ?? false),
            GirlfriendCuteModeSignature = persistedState.GirlfriendHourlyState?.CuteModeSignature ?? string.Empty,
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
        await _services.MobileVoice.SendStatusAsync("Story voice mode: streaming the first ready sentence immediately and asking Qwen for the next passage as soon as the phone starts receiving audio.", cancellationToken).ConfigureAwait(false);
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
                        _ = _services.MobileVoice.SendStatusAsync((createdNext.StarId ?? "Star") + " is already preparing story passage " + (createdNext.StoryPartIndex + 1) + " while the phone plays passage " + (currentResponse.StoryPartIndex + 1) + ".", CancellationToken.None);
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
                await _services.MobileVoice.SendStatusAsync("Story voice stream finished. Queued " + storyChunksQueued + " story passage(s), voice chunks=" + totalVoiceChunks + ", bytes=" + totalBytes + ".", cancellationToken).ConfigureAwait(false);
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
                MobileAudioSendResult sent = _services.MobileVoice.SendChunkAsync(args.Result, chunkIndex, totalTtsChunks, cancellationToken, voiceStream.AudioSession, frameContext).GetAwaiter().GetResult();
                voiceChunks++;
                bytes += args.Result.WavAudio.Length;
                _services.MobileVoice.SendStatusAsync("Story voice chunk " + (response.StoryPartIndex + 1) + "/" + totalStoryChunks + " streamed to phone immediately, ttsPart=" + voiceChunks + "/" + totalTtsChunks + ", sha256=" + ShortHash(checksum) + ".", cancellationToken).GetAwaiter().GetResult();
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
            if (!cancellationToken.IsCancellationRequested) _ = _services.MobileVoice.SendErrorAsync(args.ErrorMessage ?? "TTS generation error.", CancellationToken.None);
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
            MobileAudioSendResult sent = await _services.MobileVoice.SendChunkAsync(chunk, i, voiceChunks.Count, cancellationToken, voiceStream.AudioSession, frameContext).ConfigureAwait(false);
            bytes += chunk.WavAudio.Length;
            await _services.MobileVoice.SendStatusAsync("Story voice chunk " + (response.StoryPartIndex + 1) + "/" + totalStoryChunks + " sent to phone, ttsPart=" + (i + 1) + "/" + voiceChunks.Count + ", sha256=" + ShortHash(checksum) + ".", cancellationToken).ConfigureAwait(false);
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
