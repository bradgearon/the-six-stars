using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.DailyState;
using Wds.SixStars.Api.Screen;
using Wds.SixStars.Api.Stars;
using Wds.SixStars.Api.State;

namespace Wds.SixStars.Api.Engine
{
    public sealed class SixStarsRuntime
    {
        private readonly IStarProfileProvider _profileProvider;
        private readonly IStarAgent _agent;
        private readonly IRelationshipDeltaCalculator _deltaCalculator;
        private readonly IStarMemoryApi _memoryApi;
        private readonly IScreenContextStore? _screenContextStore;
        private readonly int _recentScreenCount;
        private readonly int _recentHistoryCount;
        private readonly object _backgroundGate = new object();
        private CancellationTokenSource? _backgroundAnalysisCts;
        private CancellationTokenSource? _backgroundStoryGenerationCts;
        private readonly SemaphoreSlim _backgroundAnalysisQueue = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, PendingBackgroundAnalysis> _pendingBackgroundAnalysis = new Dictionary<string, PendingBackgroundAnalysis>(StringComparer.OrdinalIgnoreCase);
        private readonly StarDailyStateService? _dailyStateService;
        private readonly object _interstarGate = new object();
        private CancellationTokenSource? _interstarCycleCts;
        private readonly SemaphoreSlim _interstarUpdateQueue = new SemaphoreSlim(1, 1);
        private readonly TimeSpan _interstarSettleDelay = ResolveInterstarSettleDelay();
        private readonly Dictionary<string, DateTimeOffset> _interstarCooldowns = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        private static readonly object StoryRandomGate = new object();
        private static readonly Random StoryRandom = new Random();
        private readonly ConversationDebugLogRecorder _conversationDebugLogRecorder = new ConversationDebugLogRecorder();

        public event EventHandler<StarInteractionResponse>? BackgroundAnalysisCompleted;
        public event EventHandler<Exception>? BackgroundAnalysisFailed;
        public event EventHandler<StarInteractionResponse>? StoryGenerationCompleted;
        public event EventHandler<Exception>? StoryGenerationFailed;
        public event EventHandler<StarInteractionResponse>? GirlfriendProposalCompleted;
        public event EventHandler<Exception>? GirlfriendProposalFailed;
        public event EventHandler<StarDailyStateUpdatedEventArgs>? DailyStateUpdated;
        public event EventHandler<Exception>? DailyStateUpdateFailed;
        public event EventHandler<InterstarUpdateStatusEventArgs>? InterstarUpdateStatusChanged;
        public event EventHandler<StarInteractionResponse>? InterstarMicroUpdateCompleted;
        public event EventHandler<InterstarUpdateCycleResult>? InterstarUpdateCycleCompleted;
        public event EventHandler<Exception>? InterstarUpdateFailed;

        public string ConversationDebugLogDirectory => _conversationDebugLogRecorder.LogDirectory;
        public string LatestConversationDebugLogPath => _conversationDebugLogRecorder.LatestLogPath;
        public string ReadLatestConversationDebugLogText() => _conversationDebugLogRecorder.ReadLatestLogText();

        public SixStarsRuntime(
            IStarProfileProvider profileProvider,
            IStarAgent agent,
            IRelationshipDeltaCalculator deltaCalculator,
            IStarStateStore stateStore)
            : this(profileProvider, agent, deltaCalculator, new StarMemoryApi(stateStore), null)
        {
        }

        public SixStarsRuntime(
            IStarProfileProvider profileProvider,
            IStarAgent agent,
            IRelationshipDeltaCalculator deltaCalculator,
            IStarStateStore stateStore,
            IScreenContextStore? screenContextStore,
            int recentScreenCount = 5,
            int recentHistoryCount = 10,
            int maxMemoryLogEntries = 160,
            int maxImportantQuotes = 80,
            int maxMemoryNotes = 160,
            DailyStarStateOptions? dailyStateOptions = null)
            : this(
                profileProvider,
                agent,
                deltaCalculator,
                new StarMemoryApi(stateStore, new StarMemoryOptions
                {
                    MaxMemoryLogEntries = maxMemoryLogEntries,
                    MaxImportantQuotes = maxImportantQuotes,
                    MaxMemoryNotes = maxMemoryNotes,
                    MaxRecentHistoryForPrompt = recentHistoryCount
                }),
                screenContextStore,
                recentScreenCount,
                recentHistoryCount,
                dailyStateOptions)
        {
        }

        public SixStarsRuntime(
            IStarProfileProvider profileProvider,
            IStarAgent agent,
            IRelationshipDeltaCalculator deltaCalculator,
            IStarMemoryApi memoryApi,
            IScreenContextStore? screenContextStore = null,
            int recentScreenCount = 5,
            int recentHistoryCount = 10,
            DailyStarStateOptions? dailyStateOptions = null)
        {
            _profileProvider = profileProvider;
            _agent = agent;
            _deltaCalculator = deltaCalculator;
            _memoryApi = memoryApi;
            _screenContextStore = screenContextStore;
            _recentScreenCount = Math.Max(1, recentScreenCount);
            _recentHistoryCount = Math.Max(1, recentHistoryCount);

            DailyStarStateOptions dailyOptions = dailyStateOptions ?? new DailyStarStateOptions();
            if (dailyOptions.Enabled && agent is IStarDailyStateGenerator dailyGenerator)
            {
                _dailyStateService = new StarDailyStateService(
                    profileProvider,
                    memoryApi,
                    dailyGenerator,
                    new StarDailyStateFileStore(dailyOptions.StateRoot),
                    dailyOptions);
                _dailyStateService.DailyStateUpdated += (_, args) => DailyStateUpdated?.Invoke(this, args);
                _dailyStateService.DailyStateUpdateFailed += (_, ex) => DailyStateUpdateFailed?.Invoke(this, ex);
            }
        }


        private static TimeSpan ResolveInterstarSettleDelay()
        {
            string? value = Environment.GetEnvironmentVariable("SIX_STARS_INTERSTAR_SETTLE_SECONDS");
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            {
                if (seconds < 5) seconds = 5;
                if (seconds > 600) seconds = 600;
                return TimeSpan.FromSeconds(seconds);
            }
            return TimeSpan.FromMinutes(1);
        }

        public Task<StarState> GetStarStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            return _memoryApi.GetStarStateAsync(sessionId, starId, cancellationToken);
        }

        public Task<StarInteractionHistoryPage> GetStarInteractionHistoryAsync(string sessionId, string starId, int skip, int take, CancellationToken cancellationToken)
        {
            return _memoryApi.GetStarInteractionHistoryAsync(sessionId, starId, skip, take, cancellationToken);
        }

        public bool HasPendingOrRunningBackgroundAnalysis
        {
            get
            {
                lock (_backgroundGate)
                {
                    return _pendingBackgroundAnalysis.Count > 0 || _backgroundAnalysisQueue.CurrentCount == 0;
                }
            }
        }

        public void StartDailyStateBackgroundUpdates(string sessionId)
        {
            _dailyStateService?.Start(string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId);
        }

        public void StopDailyStateBackgroundUpdates()
        {
            _dailyStateService?.Stop();
        }

        public Task<StarDailyStateRecord?> GetCurrentDailyStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            if (_dailyStateService == null) return Task.FromResult<StarDailyStateRecord?>(null);
            return _dailyStateService.ReadCurrentAsync(sessionId, starId, cancellationToken);
        }

        public Task ClearDailyStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            if (_dailyStateService == null) return Task.CompletedTask;
            return _dailyStateService.ClearStarAsync(string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId, starId, cancellationToken);
        }

        public Task<SixStarsSaveData> ExportSaveDataAsync(string sessionId, CancellationToken cancellationToken)
        {
            return _memoryApi.ExportSaveDataAsync(sessionId, cancellationToken);
        }

        public Task ImportSaveDataAsync(SixStarsSaveData saveData, CancellationToken cancellationToken)
        {
            return _memoryApi.ImportSaveDataAsync(saveData, cancellationToken);
        }

        public async Task<string> GetCurrentGirlfriendStarIdAsync(string sessionId, CancellationToken cancellationToken)
        {
            SixStarsSaveData saveData = await _memoryApi.ExportSaveDataAsync(string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId, cancellationToken).ConfigureAwait(false);
            StarState? girlfriend = (saveData.Stars ?? new List<StarState>())
                .FirstOrDefault(x => x != null && x.IsGirlfriend);
            return girlfriend?.StarId ?? string.Empty;
        }

        public async Task<StarState> SetExclusiveGirlfriendAsync(string sessionId, string starId, bool isGirlfriend, string reason, CancellationToken cancellationToken)
        {
            sessionId = string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId;
            starId = string.IsNullOrWhiteSpace(starId) ? "Elusiaa" : starId;
            SixStarsSaveData saveData = await _memoryApi.ExportSaveDataAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (saveData.Stars == null) saveData.Stars = new List<StarState>();

            StarState? selected = saveData.Stars.FirstOrDefault(x => string.Equals(x.StarId, starId, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
            {
                selected = await _memoryApi.GetStarStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                saveData.Stars.Add(selected);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (isGirlfriend)
            {
                foreach (StarState state in saveData.Stars.Where(x => x != null))
                {
                    if (string.Equals(state.StarId, starId, StringComparison.OrdinalIgnoreCase))
                    {
                        state.IsGirlfriend = true;
                        state.IsGirlfriendReady = true;
                        state.GirlfriendSince = state.GirlfriendSince ?? now;
                        state.IsMet = true;
                        state.IsAlly = true;
                        state.GirlfriendReadinessSummary = "Girlfriend path accepted. This Star is currently the Last Cat's girlfriend.";
                        state.GirlfriendHistory.Insert(0, "Accepted girlfriend path: " + (string.IsNullOrWhiteSpace(reason) ? "direct proposal" : reason));
                        state.RelationshipEvents.Insert(0, "Became the Last Cat's girlfriend through an exclusive girlfriend proposal.");
                        selected = state;
                    }
                    else if (state.IsGirlfriend)
                    {
                        state.IsGirlfriend = false;
                        state.GirlfriendHistory.Insert(0, "Girlfriend path cleared because another Star became the exclusive girlfriend.");
                    }
                }
            }
            else
            {
                selected.IsGirlfriend = false;
                selected.GirlfriendSince = null;
                selected.GirlfriendHistory.Insert(0, "Girlfriend path ended: " + (string.IsNullOrWhiteSpace(reason) ? "breakup" : reason));
                selected.RelationshipEvents.Insert(0, "Girlfriend path ended by host/player action.");
                if (selected.IsAlly && selected.EmotionalConnectionPercent >= 25 && selected.Heart >= 6 && selected.Trust >= 4 && selected.Romance >= 3)
                {
                    selected.IsGirlfriendReady = true;
                    selected.GirlfriendReadinessSummary = "Ready for girlfriend path again if both sides choose it.";
                }
                else
                {
                    selected.IsGirlfriendReady = false;
                    selected.GirlfriendReadinessSummary = "Not currently girlfriend-ready after breakup; rebuild trust, heart, romance, and connection.";
                }
            }

            await _memoryApi.ImportSaveDataAsync(saveData, cancellationToken).ConfigureAwait(false);
            return selected;
        }


        public Task<StarState> ResetStarMemoryAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            return _memoryApi.ResetStarMemoryAsync(sessionId, starId, cancellationToken);
        }

        public Task<StarState> SetStarAllyAsync(string sessionId, string starId, bool isAlly, CancellationToken cancellationToken)
        {
            return _memoryApi.SetStarAllyAsync(sessionId, starId, isAlly, cancellationToken);
        }

        public Task<StarState> SetStarMetAsync(string sessionId, string starId, bool isMet, CancellationToken cancellationToken)
        {
            return _memoryApi.SetStarMetAsync(sessionId, starId, isMet, cancellationToken);
        }

        public Task<StarState> SetStarWorldStateAsync(StarWorldStateUpdateRequest request, CancellationToken cancellationToken)
        {
            return _memoryApi.SetStarWorldStateAsync(request, cancellationToken);
        }

        public Task<StarState> ApplyStarEffectorAsync(string sessionId, string starId, StarRelationshipEffector effector, CancellationToken cancellationToken)
        {
            return _memoryApi.ApplyStarEffectorAsync(sessionId, starId, effector, cancellationToken);
        }

        public Task<StarState> ClearStarEffectorAsync(string sessionId, string starId, string effectorId, CancellationToken cancellationToken)
        {
            return _memoryApi.ClearStarEffectorAsync(sessionId, starId, effectorId, cancellationToken);
        }

        public Task<StarState> AddStarDialogueEntryAsync(StarDialogueMemoryUpdateRequest request, CancellationToken cancellationToken)
        {
            return _memoryApi.AddStarDialogueEntryAsync(request, cancellationToken);
        }

        public Task<StarState> SetStarDialogueStateAsync(StarDialogueProgressUpdateRequest request, CancellationToken cancellationToken)
        {
            return _memoryApi.SetStarDialogueStateAsync(request, cancellationToken);
        }

        public Task<StarState> ClearStarDialogueStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            return _memoryApi.ClearStarDialogueStateAsync(sessionId, starId, cancellationToken);
        }

        public async Task AddScreenCaptureAsync(string sessionId, ScreenCaptureSummary capture, CancellationToken cancellationToken)
        {
            if (_screenContextStore == null)
            {
                return;
            }

            await _screenContextStore.AddAsync(sessionId, capture, cancellationToken).ConfigureAwait(false);
        }

        public void CancelBackgroundAnalysis(string reason = "Cancel background Star analysis")
        {
            lock (_backgroundGate)
            {
                try
                {
                    _backgroundAnalysisCts?.Cancel();
                    _backgroundAnalysisCts?.Dispose();
                    _backgroundStoryGenerationCts?.Cancel();
                    _backgroundStoryGenerationCts?.Dispose();
                    _pendingBackgroundAnalysis.Clear();
                }
                catch
                {
                }
                _backgroundAnalysisCts = new CancellationTokenSource();
                _backgroundStoryGenerationCts = new CancellationTokenSource();
            }
            System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background analysis cancelled: " + reason);
        }

        public async Task<StarInteractionResponse> ProcessAsync(StarInteractionRequest request, CancellationToken cancellationToken)
        {
            CancelPendingInterstarCycle("New Star conversation request started for " + request.StarId + ".");
            NotifyInterstarStatus(request.SessionId, request.StarId, string.Empty, "ConversationStarted", "Conversation with " + request.StarId + " started; any pending interstar ripple timer was cancelled.", 0, 0);

            if (_screenContextStore != null && request.RecentScreenCaptures.Count == 0)
            {
                request.RecentScreenCaptures.AddRange(await _screenContextStore.GetRecentAsync(request.SessionId, _recentScreenCount, cancellationToken).ConfigureAwait(false));
            }

            StarProfile profile = _profileProvider.GetProfile(request.StarId);
            await PopulateKnownStarContextAsync(request, cancellationToken).ConfigureAwait(false);
            StarMemorySnapshot snapshot = await _memoryApi.GetStarMemorySnapshotAsync(request.SessionId, request.StarId, _recentHistoryCount, cancellationToken).ConfigureAwait(false);
            StarState state = snapshot.State;
            await AttachDailyStateContextForRequestAsync(request, state, cancellationToken).ConfigureAwait(false);

            GirlfriendProposalDecision? girlfriendProposal = await TryPrepareGirlfriendProposalAsync(profile, request, state, cancellationToken).ConfigureAwait(false);
            if (girlfriendProposal != null && !IsGirlfriendProposalWorkerRequest(request))
            {
                StarInteractionResponse thinkingResponse = CreateGirlfriendProposalThinkingResponse(profile, request, girlfriendProposal);
                ApplyVoiceDefaults(profile, thinkingResponse);
                QueueGirlfriendProposalDecisionAfterThinkingResponse(profile, request, state, girlfriendProposal, cancellationToken);
                return thinkingResponse;
            }

            StarRequestIntentResult? requestIntent = await ClassifyAndPrepareRequestModeAsync(profile, request, state, cancellationToken).ConfigureAwait(false);

            if (request.RequestType == RequestType.Story && !IsStoryGenerationWorkerRequest(request))
            {
                StarInteractionResponse thinkingResponse = CreateStoryThinkingResponse(profile, request, requestIntent);
                ApplyVoiceDefaults(profile, thinkingResponse);
                QueueStoryGenerationAfterThinkingResponse(profile, request, state, requestIntent, cancellationToken);
                return thinkingResponse;
            }

            StarInteractionResponse response = await _agent.GenerateAsync(profile, request, state, cancellationToken).ConfigureAwait(false);
            if (requestIntent != null)
            {
                response.Debug.Notes.Add("Pre-flight story intent check: " + requestIntent.Source + " confidence=" + requestIntent.Confidence.ToString("0.00") + " story=" + requestIntent.IsStoryRequest + " reason=" + requestIntent.Reason);
            }
            response = await TryResolveInformationNeedAsync(profile, request, state, response, cancellationToken).ConfigureAwait(false);
            ApplyVoiceDefaults(profile, response);
            response.UnityEvents.Add(new UnityEventEnvelope
            {
                Type = "StarConversationForegroundCompleted",
                Target = request.StarId,
                Data =
                {
                    { "sessionId", request.SessionId ?? string.Empty },
                    { "starId", request.StarId ?? string.Empty },
                    { "requestType", request.RequestType.ToString() },
                    { "spokenParts", response.SpokenParts.Count.ToString() },
                    { "note", "Foreground conversation reply finished; background emotion/memory review will settle after voice is ready." }
                }
            });
            NotifyInterstarStatus(request.SessionId, request.StarId, string.Empty, "ForegroundCompleted", "Foreground conversation with " + request.StarId + " finished; waiting for voice-ready background review before interstar updates can queue.", 0, 0);

            if (_agent is IStarTurnAnalyzer analyzer)
            {
                response.StateDeltas = RelationshipDelta.Zero();
                response.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "BackgroundStarAnalysisQueued",
                    Target = request.StarId,
                    Data =
                    {
                        { "speechParts", response.SpokenParts.Count.ToString() },
                        { "note", "Fast spoken response returned immediately; emotion/memory update will wait until voice generation reports ready." }
                    }
                });
                string backgroundAnalysisId = Guid.NewGuid().ToString("N");
                response.BackgroundAnalysisId = backgroundAnalysisId;
                response.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "BackgroundStarAnalysisWaitingForVoice",
                    Target = request.StarId,
                    Data =
                    {
                        { "backgroundAnalysisId", backgroundAnalysisId },
                        { "note", "Call StartBackgroundAnalysisAfterVoiceReady after foreground voice generation completes." }
                    }
                });
                response.Debug.Notes.Add("Fast foreground response returned before durable memory/emotion update. Background review is waiting for voice generation to finish.");
                StorePendingBackgroundAnalysis(backgroundAnalysisId, analyzer, profile, request, state, response);
                return response;
            }

            await ApplyAnalysisAndMemoryUpdateAsync(request, response, cancellationToken).ConfigureAwait(false);
            return response;
        }


        private async Task AttachDailyStateContextForRequestAsync(StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
        {
            if (request == null || request.GameContext == null) return;
            StarDailyStateRecord? record = null;
            if (_dailyStateService != null)
            {
                try
                {
                    record = await _dailyStateService.EnsureCurrentAsync(request.SessionId, request.StarId, state, "selected Star request prompt injection", cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    request.GameContext.RecentEvents.Add("Daily Star state refresh failed before this request: " + ex.Message);
                    DailyStateUpdateFailed?.Invoke(this, ex);
                }
            }

            if (record == null)
            {
                StarDailyStateRecord? fallbackRecord = await TryReadDailyStateFallbackAsync(request.SessionId, request.StarId, state, cancellationToken).ConfigureAwait(false);
                if (fallbackRecord != null)
                {
                    record = fallbackRecord;
                    request.GameContext.RecentEvents.Add("No daily Star state existed yet for session " + request.SessionId + "; using current daily Star state from session " + fallbackRecord.SessionId + ".");
                }
            }

            if (record == null && state.TodayState != null && state.TodayState.IsAvailable)
            {
                request.GameContext.Flags["todayStarStateJson"] = state.TodayState.Json ?? state.TodayState.Xml ?? string.Empty;
                request.GameContext.Flags["todayStarStateXml"] = string.Empty;
                request.GameContext.Flags["todayStarStateSummary"] = state.TodayState.Summary ?? string.Empty;
                request.GameContext.Flags["todayStarStateGeneratedAt"] = state.TodayState.GeneratedAt?.ToString("o") ?? string.Empty;
                request.GameContext.Flags["todayStarStateSlotId"] = state.TodayState.SlotId ?? string.Empty;
                request.GameContext.Flags["todayStarStateLocalDate"] = state.TodayState.LocalDate ?? string.Empty;
                request.GameContext.Flags["todayStarStateFilePath"] = state.TodayState.FilePath ?? string.Empty;
                request.GameContext.RecentEvents.Add("Today's generated Star state JSON is attached from the selected Star state cache.");
                return;
            }

            if (record != null && (!string.IsNullOrWhiteSpace(record.JsonContent) || !string.IsNullOrWhiteSpace(record.XmlContent)))
            {
                AttachDailyStateRecordToRequest(request, record);
            }
        }

        private async Task<StarDailyStateRecord?> TryReadDailyStateFallbackAsync(string sessionId, string starId, StarState currentState, CancellationToken cancellationToken)
        {
            if (_dailyStateService == null) return null;
            string currentSession = string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId;
            string[] candidates =
            {
                "desktop-local-dev",
                "android-phone-dev",
                "android-phone-prod",
                "local-dev"
            };

            foreach (string candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(candidate, currentSession, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    StarDailyStateRecord? record = await _dailyStateService.ReadCurrentAsync(candidate, starId, cancellationToken).ConfigureAwait(false);
                    if (record != null && (!string.IsNullOrWhiteSpace(record.JsonContent) || !string.IsNullOrWhiteSpace(record.XmlContent)))
                    {
                        if (currentState != null && currentState.LastDailyStateSourceUpdateAt.HasValue && record.GeneratedAt < currentState.LastDailyStateSourceUpdateAt.Value)
                        {
                            continue;
                        }
                        return record;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Fallback daily state is optional. Missing daily JSON in another session should not fail this request.
                }
            }

            return null;
        }

        private static void AttachDailyStateRecordToRequest(StarInteractionRequest request, StarDailyStateRecord record)
        {
            string json = !string.IsNullOrWhiteSpace(record.JsonContent) ? record.JsonContent : (record.XmlContent ?? string.Empty);
            if (json.Length > 12000) json = json.Substring(0, 12000) + "\n/* daily-star-state truncated for prompt */";
            request.GameContext.Flags["todayStarStateJson"] = json;
            request.GameContext.Flags["todayStarStateXml"] = string.Empty;
            request.GameContext.Flags["todayStarStateSummary"] = record.Summary ?? string.Empty;
            request.GameContext.Flags["todayStarStateGeneratedAt"] = record.GeneratedAt.ToString("o");
            request.GameContext.Flags["todayStarStateSlotId"] = record.SlotId ?? string.Empty;
            request.GameContext.Flags["todayStarStateLocalDate"] = record.LocalDate ?? string.Empty;
            request.GameContext.Flags["todayStarStateFilePath"] = record.FilePath ?? string.Empty;
            request.GameContext.RecentEvents.Add("Today's generated Star state JSON is attached for " + request.StarId + " from slot " + record.SlotId + ".");
        }

        private async Task<StarInteractionResponse> TryResolveInformationNeedAsync(StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse response, CancellationToken cancellationToken)
        {
            if (request == null || response == null) return response;
            if (request.GameContext.Flags.TryGetValue("informationFollowup", out string? already) && string.Equals(already, "true", StringComparison.OrdinalIgnoreCase))
            {
                return response;
            }

            List<InformationNeedVectorItem> foregroundVector = NormalizeInformationNeedVector(response, request.PlayerText);
            response.InformationNeeds = foregroundVector;
            if (foregroundVector.Count == 0) return response;

            List<InformationNeedAvailabilityItem> availability = BuildInformationNeedAvailability(profile, state, request);
            response.InformationNeedAvailability = availability;

            InformationNeedSelectionResult? aiSelection = null;
            if (_agent is IStarInformationNeedAnalyzer selector)
            {
                try
                {
                    aiSelection = await selector.AnalyzeInformationNeedAsync(profile, request, state, response, foregroundVector, availability, cancellationToken).ConfigureAwait(false);
                    response.Debug.Notes.Add("Information selector AI result: " + SummarizeSelection(aiSelection));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    response.Debug.Notes.Add("Information selector AI failed; deterministic fallback will use foreground vector only. " + ex.Message);
                }
            }

            InformationNeedSelectionResult decision = SelectInformationNeedDeterministically(foregroundVector, availability, aiSelection, response.NeedsMoreInformation, request.PlayerText);
            response.InformationNeedSelection = decision;
            response.NeedsMoreInformation = decision.ShouldRequestMoreInformation;
            response.InformationFollowupContextSummary = SummarizeInformationNeedDecision(decision, foregroundVector, availability, 0);

            if (!decision.ShouldRequestMoreInformation)
            {
                response.Debug.Notes.Add("Information vector was present, but deterministic API selection did not request a deep second pass: " + decision.Reason);
                return response;
            }

            List<InformationNeedVectorItem> selectedNeeds = BuildSelectedInformationNeeds(decision, foregroundVector);
            string context = BuildRequestedInformationContext(profile, state, selectedNeeds, request.PlayerText, 180000, request);
            response.InformationFollowupAttempted = true;
            response.InformationFollowupContextSummary = SummarizeInformationNeedDecision(decision, foregroundVector, availability, context.Length);

            if (string.IsNullOrWhiteSpace(context))
            {
                response.Debug.Notes.Add("Information selector chose " + decision.SelectedDataType + ", but no matching local context was available to attach.");
                response.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "InformationNeedNoLocalContext",
                    Target = request.StarId,
                    Data =
                    {
                        { "selected", SummarizeSelection(decision) },
                        { "vector", SummarizeNeedsForEvent(foregroundVector) },
                        { "note", "The Star requested more information, but local archives did not have a matching slice." }
                    }
                });
                return response;
            }

            return await RequestStarDeepResponseWithDataAsync(profile, request, state, response, foregroundVector, selectedNeeds, decision, context, cancellationToken).ConfigureAwait(false);
        }

        private async Task<StarInteractionResponse> RequestStarDeepResponseWithDataAsync(
            StarProfile profile,
            StarInteractionRequest request,
            StarState state,
            StarInteractionResponse foregroundResponse,
            List<InformationNeedVectorItem> fullVector,
            List<InformationNeedVectorItem> selectedNeeds,
            InformationNeedSelectionResult decision,
            string context,
            CancellationToken cancellationToken)
        {
            StarInteractionRequest followupRequest = CloneRequestForInformationFollowup(request, context, selectedNeeds, fullVector, decision);
            try
            {
                StarInteractionResponse followup = await _agent.GenerateAsync(profile, followupRequest, state, cancellationToken).ConfigureAwait(false);
                followup.NeedsMoreInformation = false;
                followup.InformationNeeds = fullVector;
                followup.InformationNeedSelection = decision;
                followup.InformationNeedAvailability = foregroundResponse.InformationNeedAvailability;
                followup.InformationFollowupAttempted = true;
                followup.InformationFollowupSucceeded = true;
                followup.InformationFollowupContextSummary = foregroundResponse.InformationFollowupContextSummary;
                followup.Debug.Notes.Add("requestStarDeepResponseWithData pass used deterministic information selection plus local archive/lore context.");
                followup.Debug.Notes.Add("Selected information need: " + SummarizeSelection(decision));
                followup.Debug.Notes.Add("Full foreground vector: " + SummarizeNeedsForEvent(fullVector));
                followup.Debug.Notes.Add("Extra context chars: " + context.Length);
                followup.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "InformationFollowupCompleted",
                    Target = request.StarId,
                    Data =
                    {
                        { "selected", SummarizeSelection(decision) },
                        { "vector", SummarizeNeedsForEvent(fullVector) },
                        { "contextChars", context.Length.ToString() },
                        { "note", "The runtime answered with a second LLM pass using deterministically selected local data." }
                    }
                });
                return followup;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                foregroundResponse.InformationFollowupSucceeded = false;
                foregroundResponse.Debug.Notes.Add("Information follow-up failed; returning the original trying-to-remember response. " + ex.Message);
                foregroundResponse.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "InformationFollowupFailed",
                    Target = request.StarId,
                    Data =
                    {
                        { "selected", SummarizeSelection(decision) },
                        { "vector", SummarizeNeedsForEvent(fullVector) },
                        { "error", ex.Message }
                    }
                });
                return foregroundResponse;
            }
        }

        private static StarInteractionRequest CloneRequestForInformationFollowup(StarInteractionRequest request, string context, List<InformationNeedVectorItem> selectedNeeds, List<InformationNeedVectorItem> fullVector, InformationNeedSelectionResult decision)
        {
            StarInteractionRequest clone = new StarInteractionRequest
            {
                SessionId = request.SessionId,
                StarId = request.StarId,
                Speaker = request.Speaker,
                RequestType = request.RequestType,
                PlayerText = request.PlayerText,
                GameContext = CloneGameContext(request.GameContext),
                StoryOptions = CloneStoryOptions(request.StoryOptions),
                AttachedPhoto = ClonePhotoAttachment(request.AttachedPhoto),
                RecentScreenCaptures = request.RecentScreenCaptures.Select(CloneScreenCaptureSummary).ToList()
            };
            clone.GameContext.Flags["informationFollowup"] = "true";
            clone.GameContext.Flags["extraInformationContext"] = context;
            clone.GameContext.Flags["informationNeedsFullVector"] = SummarizeNeedsForEvent(fullVector);
            clone.GameContext.Flags["informationNeedsSelected"] = SummarizeNeedsForEvent(selectedNeeds);
            clone.GameContext.Flags["informationNeedDecision"] = SummarizeSelection(decision);
            clone.GameContext.RecentEvents.Add("The previous foreground reply returned an information-needs vector. The API layer deterministically selected " + decision.SelectedDataType + " / " + decision.Subject + " and attached EXTRA REQUESTED INFORMATION. Answer the player's same message now. Speak naturally as if you remembered, checked the local archive, or found the relevant lore. If it still does not answer it, say you could not find enough.");
            return clone;
        }

        private static List<InformationNeedVectorItem> NormalizeInformationNeedVector(StarInteractionResponse response, string playerText)
        {
            List<InformationNeedVectorItem> needs = (response.InformationNeeds ?? new List<InformationNeedVectorItem>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.DataType))
                .Select(x => new InformationNeedVectorItem
                {
                    DataType = NormalizeInformationDataType(x.DataType),
                    Subject = string.IsNullOrWhiteSpace(x.Subject) ? "self" : x.Subject.Trim(),
                    Confidence = Clamp01(x.Confidence),
                    Score = ClampScore(x.Score),
                    Reason = x.Reason ?? string.Empty
                })
                .GroupBy(x => x.DataType, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => Math.Max(x.Confidence, x.Score / 100.0)).First())
                .OrderByDescending(x => Math.Max(x.Confidence, x.Score / 100.0))
                .Take(9)
                .ToList();

            if (needs.Count == 0 && response.NeedsMoreInformation)
            {
                needs.Add(new InformationNeedVectorItem { DataType = "history", Subject = "self", Confidence = 0.72, Score = 72, Reason = "Star set needsMoreInformation without a vector." });
            }

            if (needs.Count == 0 && LooksLikeOldMemoryRequest(playerText))
            {
                needs.Add(new InformationNeedVectorItem { DataType = "history", Subject = ExtractLikelySubject(playerText), Confidence = 0.68, Score = 68, Reason = "Heuristic detected old-memory language." });
            }

            return needs;
        }

        private static List<InformationNeedAvailabilityItem> BuildInformationNeedAvailability(StarProfile profile, StarState state, StarInteractionRequest request)
        {
            int oldArchives = state.OldConversationArchives == null ? 0 : state.OldConversationArchives.Count;
            int recentHistory = state.MemoryLog == null ? 0 : state.MemoryLog.Count;
            int quotes = state.AllImportantPlayerQuotes == null ? 0 : state.AllImportantPlayerQuotes.Count;
            int markers = state.RememberedMarkers == null ? 0 : state.RememberedMarkers.Count(x => x.IsActive);
            int lore = (profile.LibraryCanonNotes == null ? 0 : profile.LibraryCanonNotes.Count) + (profile.AngelStories == null ? 0 : profile.AngelStories.Count);
            int stories = state.StoryHistory == null ? 0 : state.StoryHistory.Count;
            int screens = request.RecentScreenCaptures == null ? 0 : request.RecentScreenCaptures.Count;

            return new List<InformationNeedAvailabilityItem>
            {
                Availability("history", oldArchives + recentHistory, oldArchives > 0 ? oldArchives + " old archives plus " + recentHistory + " recent turns" : recentHistory + " recent turns; no old archive yet", state.OldConversationArchives?.Select(x => x.Summary).Take(4)),
                Availability("quotes", quotes, quotes + " durable important player quote(s)", state.AllImportantPlayerQuotes?.Select(x => x.Quote).Take(4)),
                Availability("markers", markers, markers + " active remembered marker(s)", state.RememberedMarkers?.Where(x => x.IsActive).Select(x => x.MarkerName).Take(6)),
                Availability("lore", lore, lore + " local profile/canon lore item(s)", profile.LibraryCanonNotes?.Take(4)),
                Availability("bestiary", lore, lore > 0 ? "Bestiary/world data currently maps to local lore/profile canon slices." : "No loaded bestiary index is available yet.", profile.LibraryCanonNotes?.Where(x => ContainsAny(x, "creature", "monster", "slime", "goblin", "harpy", "dragon", "beast")).Take(4)),
                Availability("characters", 1 + lore, "Selected Star profile plus local character lore", new[] { profile.DisplayName, profile.Element, profile.Domain }.Where(x => !string.IsNullOrWhiteSpace(x))),
                Availability("world", lore, lore > 0 ? "World data currently maps to local canon/profile lore." : "No extra world index is available yet.", profile.LibraryCanonNotes?.Take(4)),
                Availability("screen", screens, screens + " recent screen/vision capture(s)", request.RecentScreenCaptures?.Select(x => x.CompanionSafeSummary).Take(4)),
                Availability("story", stories, stories + " story history entrie(s)", state.StoryHistory?.Select(x => x.Title).Take(4))
            };
        }

        private static InformationNeedAvailabilityItem Availability(string dataType, int count, string summary, IEnumerable<string>? samples)
        {
            return new InformationNeedAvailabilityItem
            {
                DataType = dataType,
                Available = count > 0,
                Count = Math.Max(0, count),
                Summary = summary ?? string.Empty,
                SampleSubjects = (samples ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => TruncateForInfo(x, 100)).Take(6).ToList()
            };
        }

        private static bool ContainsAny(string? value, params string[] tokens)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return tokens.Any(t => value.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static InformationNeedSelectionResult SelectInformationNeedDeterministically(
            List<InformationNeedVectorItem> foregroundVector,
            List<InformationNeedAvailabilityItem> availability,
            InformationNeedSelectionResult? aiSelection,
            bool legacyNeedsMoreInformation,
            string playerText)
        {
            Dictionary<string, InformationNeedAvailabilityItem> availabilityByType = (availability ?? new List<InformationNeedAvailabilityItem>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.DataType))
                .GroupBy(x => NormalizeInformationDataType(x.DataType), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Count).First(), StringComparer.OrdinalIgnoreCase);
            IEnumerable<InformationNeedVectorItem> candidateSource = aiSelection != null && aiSelection.RankedNeeds != null && aiSelection.RankedNeeds.Count > 0
                ? aiSelection.RankedNeeds.Concat(foregroundVector)
                : foregroundVector;

            List<InformationNeedVectorItem> normalized = candidateSource
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.DataType))
                .Select(x => new InformationNeedVectorItem
                {
                    DataType = NormalizeInformationDataType(x.DataType),
                    Subject = string.IsNullOrWhiteSpace(x.Subject) ? "self" : x.Subject.Trim(),
                    Confidence = Clamp01(x.Confidence),
                    Score = ClampScore(x.Score),
                    Reason = x.Reason ?? string.Empty
                })
                .GroupBy(x => x.DataType, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => CombinedNeedScore(x, availabilityByType)).First())
                .OrderByDescending(x => CombinedNeedScore(x, availabilityByType))
                .ToList();

            InformationNeedVectorItem? selected = null;
            if (aiSelection != null && aiSelection.ShouldRequestMoreInformation && !string.IsNullOrWhiteSpace(aiSelection.SelectedDataType))
            {
                string aiType = NormalizeInformationDataType(aiSelection.SelectedDataType);
                if (IsAvailable(aiType, availabilityByType) && (aiSelection.Confidence >= 0.45 || aiSelection.Score >= 45 || legacyNeedsMoreInformation))
                {
                    selected = new InformationNeedVectorItem
                    {
                        DataType = aiType,
                        Subject = string.IsNullOrWhiteSpace(aiSelection.Subject) ? "self" : aiSelection.Subject.Trim(),
                        Confidence = Clamp01(aiSelection.Confidence),
                        Score = ClampScore(aiSelection.Score),
                        Reason = FirstNonEmpty(aiSelection.Reason, "AI selector chose this area.")
                    };
                }
            }

            if (selected == null)
            {
                selected = normalized.FirstOrDefault(x => IsAvailable(x.DataType, availabilityByType));
            }

            bool oldMemoryHeuristic = LooksLikeOldMemoryRequest(playerText);
            if (selected == null)
            {
                return new InformationNeedSelectionResult
                {
                    ShouldRequestMoreInformation = false,
                    SelectedDataType = string.Empty,
                    Subject = "self",
                    Confidence = 0,
                    Score = 0,
                    Reason = "No requested information area has local data available.",
                    Source = "deterministic",
                    RankedNeeds = normalized
                };
            }

            double chosenStrength = Math.Max(selected.Confidence, selected.Score / 100.0);
            bool strongEnough = chosenStrength >= 0.55 || legacyNeedsMoreInformation || oldMemoryHeuristic || (aiSelection != null && aiSelection.ShouldRequestMoreInformation && NormalizeInformationDataType(aiSelection.SelectedDataType) == selected.DataType);

            return new InformationNeedSelectionResult
            {
                ShouldRequestMoreInformation = strongEnough,
                SelectedDataType = selected.DataType,
                Subject = string.IsNullOrWhiteSpace(selected.Subject) ? "self" : selected.Subject,
                Confidence = selected.Confidence,
                Score = selected.Score,
                Reason = strongEnough
                    ? "Deterministic API selection chose the strongest available information area. " + selected.Reason
                    : "Vector was present but below threshold after local availability checks.",
                Source = aiSelection == null ? "deterministic" : "deterministic+" + FirstNonEmpty(aiSelection.Source, "ai"),
                RankedNeeds = normalized
            };
        }

        private static double CombinedNeedScore(InformationNeedVectorItem need, Dictionary<string, InformationNeedAvailabilityItem> availabilityByType)
        {
            double baseScore = Math.Max(Clamp01(need.Confidence), ClampScore(need.Score) / 100.0);
            if (availabilityByType.TryGetValue(NormalizeInformationDataType(need.DataType), out InformationNeedAvailabilityItem? available) && available.Available)
            {
                baseScore += 0.12 + Math.Min(0.10, available.Count / 200.0);
            }
            return baseScore;
        }

        private static bool IsAvailable(string dataType, Dictionary<string, InformationNeedAvailabilityItem> availabilityByType)
        {
            return availabilityByType.TryGetValue(NormalizeInformationDataType(dataType), out InformationNeedAvailabilityItem? item) && item.Available;
        }

        private static List<InformationNeedVectorItem> BuildSelectedInformationNeeds(InformationNeedSelectionResult decision, List<InformationNeedVectorItem> fullVector)
        {
            string selectedType = NormalizeInformationDataType(decision.SelectedDataType);
            List<InformationNeedVectorItem> selected = fullVector.Where(x => string.Equals(NormalizeInformationDataType(x.DataType), selectedType, StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
            if (selected.Count == 0)
            {
                selected.Add(new InformationNeedVectorItem
                {
                    DataType = selectedType,
                    Subject = string.IsNullOrWhiteSpace(decision.Subject) ? "self" : decision.Subject,
                    Confidence = Clamp01(decision.Confidence),
                    Score = ClampScore(decision.Score),
                    Reason = decision.Reason
                });
            }
            else
            {
                selected[0].Subject = string.IsNullOrWhiteSpace(decision.Subject) ? selected[0].Subject : decision.Subject;
                selected[0].Confidence = Math.Max(selected[0].Confidence, Clamp01(decision.Confidence));
                selected[0].Score = Math.Max(selected[0].Score, ClampScore(decision.Score));
                selected[0].Reason = FirstNonEmpty(decision.Reason, selected[0].Reason);
            }
            return selected;
        }

        private static string BuildRequestedInformationContext(StarProfile profile, StarState state, List<InformationNeedVectorItem> needs, string playerText, int maxChars, StarInteractionRequest? request = null)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("EXTRA REQUESTED INFORMATION");
            sb.AppendLine("Use this only to answer the player's latest message. Mention naturally that you remembered, found an old marker, found lore, checked the screen/story archive, or could not find enough detail. Do not dump the archive.");
            sb.AppendLine("Selected need vector: " + SummarizeNeedsForEvent(needs));

            bool wantsHistory = Wants(needs, "history");
            bool wantsQuotes = Wants(needs, "quotes") || wantsHistory;
            bool wantsMarkers = Wants(needs, "markers");
            bool wantsLore = Wants(needs, "lore") || Wants(needs, "characters") || Wants(needs, "world") || Wants(needs, "bestiary");
            bool wantsScreen = Wants(needs, "screen");
            bool wantsStory = Wants(needs, "story");

            if (wantsQuotes && state.AllImportantPlayerQuotes != null && state.AllImportantPlayerQuotes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("IMPORTANT PLAYER QUOTE SOURCE ARCHIVE (selected slice):");
                foreach (StarImportantQuoteRecord quote in SelectQuoteRecords(state.AllImportantPlayerQuotes, needs, playerText, 48))
                {
                    sb.AppendLine("- " + quote.CreatedAt.ToString("u") + " | " + quote.Reason + " | " + TruncateForInfo(quote.Quote, 420));
                }
            }

            if (wantsMarkers && state.RememberedMarkers != null && state.RememberedMarkers.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("REMEMBERED MARKER SOURCE ARCHIVE (selected slice):");
                foreach (StarRememberedMarker marker in SelectMarkers(state.RememberedMarkers, needs, playerText, 80))
                {
                    sb.AppendLine("- id=" + marker.Id + " | name=" + TruncateForInfo(marker.MarkerName, 120) + " | coords=" + TruncateForInfo(marker.Coordinates, 80) + " | location=" + TruncateForInfo(FirstNonEmpty(marker.LocationText, marker.Area), 160) + " | " + TruncateForInfo(FirstNonEmpty(marker.Description, marker.ReminderText), 420));
                    if (!string.IsNullOrWhiteSpace(marker.SourcePlayerQuote)) sb.AppendLine("  source quote: " + TruncateForInfo(marker.SourcePlayerQuote, 360));
                }
            }

            if (wantsHistory && state.OldConversationArchives != null && state.OldConversationArchives.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("OLD CONVERSATION ARCHIVES (summaries plus selected source turns):");
                foreach (StarConversationArchiveEntry archive in SelectArchives(state.OldConversationArchives, needs, playerText, 8))
                {
                    sb.AppendLine("- archive=" + archive.Id + " | " + archive.StartedAt.ToString("u") + " -> " + archive.EndedAt.ToString("u") + " | turns=" + archive.EntryCount);
                    sb.AppendLine("  summary: " + TruncateForInfo(archive.Summary, 900));
                    foreach (ConversationLogEntry entry in SelectArchiveEntries(archive, needs, playerText, 16))
                    {
                        sb.AppendLine("  turn " + entry.CreatedAt.ToString("u") + ": player=" + QuoteForInfo(TruncateForInfo(entry.PlayerText, 360)) + " star=" + QuoteForInfo(TruncateForInfo(entry.StarResponse, 360)));
                    }
                }
            }

            if (wantsHistory && state.MemoryLog != null && state.MemoryLog.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("RECENT CONVERSATION LOG (selected recent turns):");
                foreach (ConversationLogEntry entry in state.MemoryLog.Where(x => x != null).Take(24))
                {
                    sb.AppendLine("- " + entry.CreatedAt.ToString("u") + ": player=" + QuoteForInfo(TruncateForInfo(entry.PlayerText, 260)) + " star=" + QuoteForInfo(TruncateForInfo(entry.StarResponse, 260)));
                    if (!string.IsNullOrWhiteSpace(entry.Summary)) sb.AppendLine("  summary: " + TruncateForInfo(entry.Summary, 320));
                }
            }

            if (wantsStory && state.StoryHistory != null && state.StoryHistory.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("STORY HISTORY ARCHIVE:");
                foreach (StarStoryHistoryEntry story in state.StoryHistory.Take(18))
                {
                    sb.AppendLine("- " + story.Title + " | finished=" + story.Finished + " | " + TruncateForInfo(story.Summary, 650));
                }
            }

            if (wantsScreen && request != null && request.RecentScreenCaptures != null && request.RecentScreenCaptures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("RECENT SCREEN / VISION CAPTURES:");
                foreach (ScreenCaptureSummary capture in request.RecentScreenCaptures.Take(10))
                {
                    sb.AppendLine("- " + capture.CapturedAt.ToString("u") + " | " + TruncateForInfo(FirstNonEmpty(capture.CompanionSafeSummary, capture.LikelyActivity, capture.ActiveApp), 800));
                    if (capture.VisibleTextHighlights.Count > 0) sb.AppendLine("  visible text: " + TruncateForInfo(string.Join(" | ", capture.VisibleTextHighlights), 1200));
                    if (capture.VisibleProblems.Count > 0) sb.AppendLine("  visible problems: " + TruncateForInfo(string.Join(" | ", capture.VisibleProblems), 900));
                }
            }

            if (wantsLore)
            {
                sb.AppendLine();
                sb.AppendLine("CHARACTER / WORLD / BESTIARY LORE AVAILABLE LOCALLY:");
                sb.AppendLine("Star: " + profile.DisplayName + " | element=" + profile.Element + " | domain=" + profile.Domain + " | core quote=" + profile.CoreQuote);
                if (!string.IsNullOrWhiteSpace(profile.PersonalityShort)) sb.AppendLine("Personality: " + TruncateForInfo(profile.PersonalityShort, 500));
                if (!string.IsNullOrWhiteSpace(profile.BackstorySummary)) sb.AppendLine("Backstory: " + TruncateForInfo(profile.BackstorySummary, 900));
                foreach (string note in profile.LibraryCanonNotes.Take(24)) sb.AppendLine("- canon: " + TruncateForInfo(note, 500));
                foreach (StarLoreEntry story in profile.AngelStories.Take(12)) sb.AppendLine("- lore story: " + story.Title + " — " + TruncateForInfo(story.Summary, 600));
            }

            string result = sb.ToString().TrimEnd();
            if (result.Length > maxChars) result = result.Substring(0, maxChars).TrimEnd() + "\n[Extra information clipped by runtime cap.]";
            return result;
        }

        private static IEnumerable<StarImportantQuoteRecord> SelectQuoteRecords(List<StarImportantQuoteRecord> quotes, List<InformationNeedVectorItem> needs, string playerText, int max)
        {
            return quotes.Where(x => x != null).OrderByDescending(x => RelevanceScore(x.Quote + " " + x.Reason, needs, playerText)).ThenByDescending(x => x.CreatedAt).Take(max);
        }

        private static IEnumerable<StarRememberedMarker> SelectMarkers(List<StarRememberedMarker> markers, List<InformationNeedVectorItem> needs, string playerText, int max)
        {
            return markers.Where(x => x != null && x.IsActive).OrderByDescending(x => RelevanceScore((x.MarkerName ?? "") + " " + (x.Description ?? "") + " " + (x.LocationText ?? "") + " " + (x.SourcePlayerQuote ?? ""), needs, playerText)).ThenByDescending(x => x.UpdatedAt).Take(max);
        }

        private static IEnumerable<StarConversationArchiveEntry> SelectArchives(List<StarConversationArchiveEntry> archives, List<InformationNeedVectorItem> needs, string playerText, int max)
        {
            return archives.Where(x => x != null).OrderByDescending(x => RelevanceScore(x.Summary + " " + string.Join(" ", x.TopicTags ?? new List<string>()), needs, playerText)).ThenByDescending(x => x.EndedAt).Take(max);
        }

        private static IEnumerable<ConversationLogEntry> SelectArchiveEntries(StarConversationArchiveEntry archive, List<InformationNeedVectorItem> needs, string playerText, int max)
        {
            return (archive.Entries ?? new List<ConversationLogEntry>()).OrderByDescending(x => RelevanceScore((x.PlayerText ?? "") + " " + (x.StarResponse ?? "") + " " + (x.Summary ?? ""), needs, playerText)).ThenByDescending(x => x.CreatedAt).Take(max).OrderBy(x => x.CreatedAt);
        }

        private static int RelevanceScore(string text, List<InformationNeedVectorItem> needs, string playerText)
        {
            string hay = (text ?? string.Empty).ToLowerInvariant();
            int score = 0;
            foreach (string token in BuildSearchTokens(needs, playerText))
            {
                if (hay.Contains(token)) score += token.Length >= 6 ? 3 : 1;
            }
            return score;
        }

        private static List<string> BuildSearchTokens(List<InformationNeedVectorItem> needs, string playerText)
        {
            List<string> raw = new List<string>();
            raw.Add(playerText ?? string.Empty);
            raw.AddRange(needs.Select(x => x.Subject ?? string.Empty));
            raw.AddRange(needs.Select(x => x.Reason ?? string.Empty));
            return Regex.Split(string.Join(" ", raw).ToLowerInvariant(), @"[^a-z0-9_]+")
                .Where(x => x.Length >= 3 && !IsStopToken(x))
                .Distinct()
                .Take(40)
                .ToList();
        }

        private static bool IsStopToken(string token)
        {
            switch (token)
            {
                case "the": case "and": case "you": case "that": case "this": case "what": case "when": case "where": case "about": case "remember": case "history": case "old": case "from": case "with": case "for": case "self":
                    return true;
                default:
                    return false;
            }
        }

        private static bool Wants(List<InformationNeedVectorItem> needs, string dataType)
        {
            return needs.Any(x => string.Equals(NormalizeInformationDataType(x.DataType), dataType, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeInformationDataType(string value)
        {
            string compact = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (compact.Contains("quote")) return "quotes";
            if (compact.Contains("marker") || compact.Contains("location")) return "markers";
            if (compact.Contains("bestiary") || compact.Contains("monster") || compact.Contains("creature")) return "bestiary";
            if (compact.Contains("character") || compact.Contains("profile") || compact.Contains("star")) return "characters";
            if (compact.Contains("lore")) return "lore";
            if (compact.Contains("world")) return "world";
            if (compact.Contains("screen") || compact.Contains("vision")) return "screen";
            if (compact.Contains("story") || compact.Contains("stories")) return "story";
            return "history";
        }

        private static bool LooksLikeOldMemoryRequest(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return Regex.IsMatch(text, @"\b(remember|earlier|before|last time|old|previous|nostalgia|marker|where did|what did i say|what were we talking|conversation from)\b", RegexOptions.IgnoreCase);
        }

        private static string ExtractLikelySubject(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "self";
            Match about = Regex.Match(text, @"\b(?:about|called|named|marker)\s+(?<subject>[A-Za-z0-9 _'\-]{2,80})", RegexOptions.IgnoreCase);
            return about.Success ? about.Groups["subject"].Value.Trim() : "self";
        }

        private static string SummarizeInformationNeedDecision(InformationNeedSelectionResult decision, List<InformationNeedVectorItem> vector, List<InformationNeedAvailabilityItem> availability, int contextChars)
        {
            return SummarizeSelection(decision) + "; vector=" + SummarizeNeedsForEvent(vector) + "; available=" + string.Join(", ", availability.Where(x => x.Available).Select(x => x.DataType + "(" + x.Count + ")")) + "; local context chars=" + contextChars;
        }

        private static string SummarizeSelection(InformationNeedSelectionResult? decision)
        {
            if (decision == null) return "none";
            return (decision.ShouldRequestMoreInformation ? "request" : "no-request") + ":" + NormalizeInformationDataType(decision.SelectedDataType) + ":" + (string.IsNullOrWhiteSpace(decision.Subject) ? "self" : decision.Subject) + "@" + decision.Confidence.ToString("0.00") + "/" + decision.Score.ToString("0") + " source=" + FirstNonEmpty(decision.Source, "unknown") + " reason=" + TruncateForInfo(decision.Reason, 180);
        }

        private static string SummarizeNeedsForEvent(List<InformationNeedVectorItem> needs)
        {
            return string.Join(", ", (needs ?? new List<InformationNeedVectorItem>()).Select(x => NormalizeInformationDataType(x.DataType) + ":" + (string.IsNullOrWhiteSpace(x.Subject) ? "self" : x.Subject) + "@" + x.Confidence.ToString("0.00") + "/" + x.Score.ToString("0")));
        }

        private static double Clamp01(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        private static double ClampScore(double value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }

        private static string TruncateForInfo(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string cleaned = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (cleaned.Length <= maxLength) return cleaned;
            return cleaned.Substring(0, maxLength).Trim() + "...";
        }

        private static string QuoteForInfo(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "'") + "\"";
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return string.Empty;
        }

        private async Task PopulateKnownStarContextAsync(StarInteractionRequest request, CancellationToken cancellationToken)
        {
            try
            {
                SixStarsSaveData saveData = await _memoryApi.ExportSaveDataAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
                List<StarState> knownStates = saveData.Stars
                    .Where(x => x != null && (x.IsMet || x.IsAlly))
                    .OrderByDescending(x => x.IsAlly)
                    .ThenBy(x => x.StarId)
                    .ToList();

                if (knownStates.Count == 0)
                {
                    knownStates.Add(new StarState { StarId = "Elusiaa", IsMet = true, IsAlly = true });
                }

                request.GameContext.Flags["knownStarsMet"] = string.Join(",", knownStates.Where(x => x.IsMet).Select(x => x.StarId));
                request.GameContext.Flags["knownStarsAllies"] = string.Join(",", knownStates.Where(x => x.IsAlly).Select(x => x.StarId));
                request.GameContext.Flags["knownStarsDetail"] = string.Join(" | ", knownStates.Select(x => x.StarId + ":met=" + x.IsMet + ",ally=" + x.IsAlly + ",girlfriend=" + x.IsGirlfriend + ",connection=" + x.EmotionalConnectionPercent.ToString("0.0")));
            }
            catch (Exception ex)
            {
                request.GameContext.Flags["knownStarsContextError"] = ex.Message;
            }
        }

        private static string GetGameContextFlag(GameContext context, string key, string fallback = "")
        {
            if (context != null && context.Flags.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            return fallback;
        }

        private static bool IsStoryGenerationWorkerRequest(StarInteractionRequest request)
        {
            return request.GameContext.Flags.TryGetValue("storyGenerationWorker", out string? value)
                && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGirlfriendProposalWorkerRequest(StarInteractionRequest request)
        {
            return request.GameContext.Flags.TryGetValue("girlfriendProposalWorker", out string? value)
                && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static StarInteractionResponse CreateStoryThinkingResponse(StarProfile profile, StarInteractionRequest request, StarRequestIntentResult? requestIntent)
        {
            string phrase = PickStoryThinkingPhrase(profile);
            StarInteractionResponse response = new StarInteractionResponse
            {
                StarId = profile.StarId,
                EmotionalPurpose = "Immediate story-thinking placeholder while the long story LLM pass runs in the background.",
                VoiceDirective = VoiceDirective.Empty(),
                SuggestedStateDeltas = RelationshipDelta.Zero(),
                StateDeltas = RelationshipDelta.Zero(),
                ConversationSummary = string.Empty,
                ChangesSummary = profile.DisplayName + " acknowledged the story request and began preparing a full story.",
                StoryIsFinished = false,
                StoryPartIndex = 0,
                Debug = new DebugInfo
                {
                    DetectedIntent = request.RequestType.ToString(),
                    WisdomScore = 0
                }
            };

            response.SetSpokenParts(new[] { phrase });
            response.Debug.Notes.Add("Story request quick-response path used. The full story is being generated in the background, then will be raised through StoryGenerationCompleted.");
            response.Debug.Notes.Add("Story-thinking phrase source: StarProfile.StoryThinkingPhrases JSON/fallback.");
            response.Debug.Notes.Add(BuildStoryLengthDebugNote(request.StoryOptions));
            if (requestIntent != null)
            {
                response.Debug.Notes.Add("Pre-flight story intent check: " + requestIntent.Source + " confidence=" + requestIntent.Confidence.ToString("0.00") + " story=" + requestIntent.IsStoryRequest + " reason=" + requestIntent.Reason);
            }

            response.UnityEvents.Add(new UnityEventEnvelope
            {
                Type = "StoryGenerationStarted",
                Target = profile.StarId,
                Data =
                {
                    { "note", "Spoke a preprogrammed story-thinking phrase immediately. Full story LLM generation is running in the background." },
                    { "playerText", request.PlayerText ?? string.Empty },
                    { "approxTargetMinutes", (request.StoryOptions?.TargetMinutes ?? 0).ToString() },
                    { "approxTargetChunkCount", (request.StoryOptions?.TargetChunkCount ?? 0).ToString() },
                    { "storyLengthIsFlexible", "true" },
                    { "sessionId", request.SessionId ?? string.Empty },
                    { "mobileBridge", GetGameContextFlag(request.GameContext, "mobileBridge", "false") }
                }
            });

            return response;
        }

        private static string PickStoryThinkingPhrase(StarProfile profile)
        {
            List<string> phrases = profile.StoryThinkingPhrases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();

            if (phrases.Count == 0)
            {
                phrases.AddRange(new[]
                {
                    "Just a moment. I am thinking of a story for you.",
                    "Give me one breath, little one. I am gathering the shape of a story.",
                    "Stay close. I am finding the right tale to carry beside your work.",
                    "One soft moment. I am listening for the story beneath the cave.",
                    "I hear you. Let me draw a story from the stars for us."
                });
            }

            lock (StoryRandomGate)
            {
                return phrases[StoryRandom.Next(phrases.Count)];
            }
        }

        private async Task<GirlfriendProposalDecision?> TryPrepareGirlfriendProposalAsync(StarProfile profile, StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
        {
            if (request.RequestType != RequestType.Chat) return null;
            if (IsGirlfriendProposalWorkerRequest(request)) return null;
            if (!LooksLikeGirlfriendProposal(request.PlayerText)) return null;

            string currentGirlfriend = await GetCurrentGirlfriendStarIdAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
            bool alreadyTaken = !string.IsNullOrWhiteSpace(currentGirlfriend) && !string.Equals(currentGirlfriend, request.StarId, StringComparison.OrdinalIgnoreCase);
            bool alreadyThisGirlfriend = string.Equals(currentGirlfriend, request.StarId, StringComparison.OrdinalIgnoreCase) || state.IsGirlfriend;
            bool eligible = state.IsAlly && (state.IsGirlfriendReady || (state.EmotionalConnectionPercent >= 25 && state.Heart >= 6 && state.Trust >= 4 && state.Romance >= 3));
            string decision = alreadyThisGirlfriend ? "already_girlfriend" : alreadyTaken ? "blocked_existing_girlfriend" : eligible ? "eligible_accept_possible" : "not_ready_no_for_now";
            string reason;
            if (alreadyThisGirlfriend)
            {
                reason = profile.DisplayName + " is already the Last Cat's girlfriend.";
            }
            else if (alreadyTaken)
            {
                reason = "Another Star is already the Last Cat's girlfriend: " + currentGirlfriend + ". The answer must be no unless that relationship is ended first.";
            }
            else if (eligible)
            {
                reason = "The deterministic girlfriend readiness gate is open. Ask the Star to answer in character and decide whether she accepts now.";
            }
            else
            {
                reason = "The relationship is not girlfriend-ready yet. The Star should answer no/not yet in character and describe the path forward.";
            }

            request.GameContext.Flags["girlfriendProposalDetected"] = "true";
            request.GameContext.Flags["girlfriendProposalDecision"] = decision;
            request.GameContext.Flags["existingGirlfriendStarId"] = currentGirlfriend;
            request.GameContext.Flags["girlfriendProposalReason"] = reason;
            request.GameContext.RecentEvents.Add("Girlfriend proposal micro-intent detected from the player. A quick thinking line will play, then a full girlfriend proposal answer will be generated with current relationship state.");

            return new GirlfriendProposalDecision
            {
                Decision = decision,
                ExistingGirlfriendStarId = currentGirlfriend,
                IsAlreadyCurrentGirlfriend = alreadyThisGirlfriend,
                IsBlockedByExistingGirlfriend = alreadyTaken,
                IsEligibleToAccept = eligible && !alreadyTaken,
                ShouldAcceptByDeterministicGate = eligible && !alreadyTaken && !alreadyThisGirlfriend,
                Reason = reason
            };
        }

        private static bool LooksLikeGirlfriendProposal(string? text)
        {
            string t = (text ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(t)) return false;
            return ContainsAny(t,
                "be my girlfriend",
                "will you be my girlfriend",
                "would you be my girlfriend",
                "can you be my girlfriend",
                "be my girl",
                "be my gf",
                "will you go out with me",
                "would you go out with me",
                "go out with me",
                "date me",
                "be with me")
                || (t.Contains("girlfriend") && ContainsAny(t, "be", "become", "my", "ask", "asking", "would", "will", "could", "want"));
        }

        private static StarInteractionResponse CreateGirlfriendProposalThinkingResponse(StarProfile profile, StarInteractionRequest request, GirlfriendProposalDecision decision)
        {
            string phrase = PickGirlfriendProposalThinkingPhrase(profile);
            StarInteractionResponse response = new StarInteractionResponse
            {
                StarId = profile.StarId,
                EmotionalPurpose = "Immediate girlfriend-proposal acknowledgement while the full relationship-state decision pass runs in the background.",
                VoiceDirective = VoiceDirective.Empty(),
                SuggestedStateDeltas = RelationshipDelta.Zero(),
                StateDeltas = RelationshipDelta.Zero(),
                ConversationSummary = string.Empty,
                ChangesSummary = profile.DisplayName + " heard the girlfriend proposal and is thinking before answering fully.",
                StoryIsFinished = true,
                Debug = new DebugInfo
                {
                    DetectedIntent = "GirlfriendProposal",
                    WisdomScore = 0
                }
            };
            response.SetSpokenParts(new[] { phrase });
            response.Debug.Notes.Add("Girlfriend proposal quick-response path used. The full yes/no proposal answer is running in the background.");
            response.Debug.Notes.Add("Girlfriend proposal decision gate: " + decision.Decision + "; reason=" + decision.Reason);
            response.UnityEvents.Add(new UnityEventEnvelope
            {
                Type = "GirlfriendProposalStarted",
                Target = profile.StarId,
                Data =
                {
                    { "note", "Spoke a preprogrammed girlfriend-proposal thinking phrase immediately. Full proposal answer is running in the background." },
                    { "playerText", request.PlayerText ?? string.Empty },
                    { "decision", decision.Decision },
                    { "existingGirlfriendStarId", decision.ExistingGirlfriendStarId ?? string.Empty },
                    { "sessionId", request.SessionId ?? string.Empty },
                    { "mobileBridge", GetGameContextFlag(request.GameContext, "mobileBridge", "false") }
                }
            });
            return response;
        }

        private static string PickGirlfriendProposalThinkingPhrase(StarProfile profile)
        {
            List<string> phrases = profile.GirlfriendProposalThinkingPhrases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();
            if (phrases.Count == 0)
            {
                phrases.AddRange(new[]
                {
                    "One moment. I need to hold what you just asked with care.",
                    "Stay close. That question deserves a real answer, not a careless one.",
                    "I heard you. Let me look at the shape of our bond before I answer.",
                    "Give me a breath. I am thinking about what your words mean between us.",
                    "That is not a small question. Let me answer it with the respect it deserves."
                });
            }
            lock (StoryRandomGate)
            {
                return phrases[StoryRandom.Next(phrases.Count)];
            }
        }

        private static StarInteractionRequest CloneRequestForGirlfriendProposal(StarInteractionRequest request)
        {
            return new StarInteractionRequest
            {
                SessionId = request.SessionId,
                StarId = request.StarId,
                Speaker = request.Speaker,
                RequestType = RequestType.Chat,
                PlayerText = request.PlayerText,
                GameContext = CloneGameContext(request.GameContext),
                StoryOptions = CloneStoryOptions(request.StoryOptions),
                AttachedPhoto = ClonePhotoAttachment(request.AttachedPhoto),
                RecentScreenCaptures = request.RecentScreenCaptures.Select(CloneScreenCaptureSummary).ToList()
            };
        }

        private void QueueGirlfriendProposalDecisionAfterThinkingResponse(StarProfile profile, StarInteractionRequest request, StarState state, GirlfriendProposalDecision initialDecision, CancellationToken cancellationToken)
        {
            StarInteractionRequest workerRequest = CloneRequestForGirlfriendProposal(request);
            workerRequest.GameContext.Flags["girlfriendProposalWorker"] = "true";
            workerRequest.GameContext.Flags["girlfriendProposalDecision"] = initialDecision.Decision;
            workerRequest.GameContext.Flags["existingGirlfriendStarId"] = initialDecision.ExistingGirlfriendStarId ?? string.Empty;
            workerRequest.GameContext.Flags["girlfriendProposalReason"] = initialDecision.Reason ?? string.Empty;
            workerRequest.GameContext.RecentEvents.Add("The Star already answered with a preprogrammed girlfriend-proposal thinking phrase; now generate the real yes/no answer using full state.");

            CancellationToken storyToken;
            lock (_backgroundGate)
            {
                _backgroundStoryGenerationCts?.Cancel();
                _backgroundStoryGenerationCts?.Dispose();
                _backgroundStoryGenerationCts = new CancellationTokenSource();
                storyToken = _backgroundStoryGenerationCts.Token;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await PopulateKnownStarContextAsync(workerRequest, storyToken).ConfigureAwait(false);
                    StarMemorySnapshot latestSnapshot = await _memoryApi.GetStarMemorySnapshotAsync(workerRequest.SessionId, workerRequest.StarId, _recentHistoryCount, storyToken).ConfigureAwait(false);
                    StarState latestState = latestSnapshot.State;
                    await AttachDailyStateContextForRequestAsync(workerRequest, latestState, storyToken).ConfigureAwait(false);

                    string currentGirlfriend = await GetCurrentGirlfriendStarIdAsync(workerRequest.SessionId, storyToken).ConfigureAwait(false);
                    bool blockedByOther = !string.IsNullOrWhiteSpace(currentGirlfriend) && !string.Equals(currentGirlfriend, workerRequest.StarId, StringComparison.OrdinalIgnoreCase);
                    bool alreadyThis = string.Equals(currentGirlfriend, workerRequest.StarId, StringComparison.OrdinalIgnoreCase) || latestState.IsGirlfriend;
                    bool eligible = latestState.IsAlly && (latestState.IsGirlfriendReady || (latestState.EmotionalConnectionPercent >= 25 && latestState.Heart >= 6 && latestState.Trust >= 4 && latestState.Romance >= 3));
                    bool shouldAccept = eligible && !blockedByOther && !alreadyThis;

                    workerRequest.GameContext.Flags["existingGirlfriendStarId"] = currentGirlfriend;
                    workerRequest.GameContext.Flags["girlfriendProposalDecision"] = alreadyThis ? "already_girlfriend" : blockedByOther ? "blocked_existing_girlfriend" : shouldAccept ? "accept" : "not_ready_no_for_now";
                    workerRequest.GameContext.Flags["girlfriendProposalReason"] = blockedByOther
                        ? "Another Star is already the exclusive girlfriend: " + currentGirlfriend + ". The Star must say no unless that relationship ends first."
                        : alreadyThis
                            ? profile.DisplayName + " is already the Last Cat's girlfriend; she should affirm the current relationship."
                            : shouldAccept
                                ? "The readiness gate is open. She may say yes if it fits her voice and current relationship."
                                : "The readiness gate is not open yet; she should gently say not yet and describe the path forward.";
                    workerRequest.GameContext.RecentEvents.Add("Current exclusive girlfriend: " + (string.IsNullOrWhiteSpace(currentGirlfriend) ? "none" : currentGirlfriend) + ". Girlfriend proposal decision flag: " + workerRequest.GameContext.Flags["girlfriendProposalDecision"] + ".");

                    StarInteractionResponse proposalResponse = await _agent.GenerateAsync(profile, workerRequest, latestState, storyToken).ConfigureAwait(false);
                    ApplyVoiceDefaults(profile, proposalResponse);
                    proposalResponse.Debug.DetectedIntent = "GirlfriendProposal";
                    proposalResponse.Debug.Notes.Add("Girlfriend proposal worker decision: " + workerRequest.GameContext.Flags["girlfriendProposalDecision"] + "; reason=" + workerRequest.GameContext.Flags["girlfriendProposalReason"]);

                    if (shouldAccept)
                    {
                        latestState = await SetExclusiveGirlfriendAsync(workerRequest.SessionId, workerRequest.StarId, true, "Accepted direct girlfriend proposal from: " + (workerRequest.PlayerText ?? string.Empty), storyToken).ConfigureAwait(false);
                        proposalResponse.IsGirlfriendReadyAfter = true;
                        proposalResponse.RelationshipEvents.Add("Accepted girlfriend proposal; exclusive girlfriend state stored.");
                    }
                    else if (alreadyThis)
                    {
                        proposalResponse.IsGirlfriendReadyAfter = true;
                    }

                    if (_agent is IStarTurnAnalyzer analyzer)
                    {
                        proposalResponse.StateDeltas = RelationshipDelta.Zero();
                        string backgroundAnalysisId = Guid.NewGuid().ToString("N");
                        proposalResponse.BackgroundAnalysisId = backgroundAnalysisId;
                        proposalResponse.UnityEvents.Add(new UnityEventEnvelope
                        {
                            Type = "BackgroundStarAnalysisWaitingForVoice",
                            Target = workerRequest.StarId,
                            Data =
                            {
                                { "backgroundAnalysisId", backgroundAnalysisId },
                                { "note", "Call StartBackgroundAnalysisAfterVoiceReady after generated girlfriend proposal voice playback completes." }
                            }
                        });
                        StorePendingBackgroundAnalysis(backgroundAnalysisId, analyzer, profile, workerRequest, latestState, proposalResponse);
                    }

                    proposalResponse.UnityEvents.Add(new UnityEventEnvelope
                    {
                        Type = "GirlfriendProposalCompleted",
                        Target = workerRequest.StarId,
                        Data =
                        {
                            { "sessionId", workerRequest.SessionId ?? string.Empty },
                            { "mobileBridge", GetGameContextFlag(workerRequest.GameContext, "mobileBridge", "false") },
                            { "decision", workerRequest.GameContext.Flags["girlfriendProposalDecision"] },
                            { "accepted", shouldAccept ? "true" : "false" },
                            { "existingGirlfriendStarId", currentGirlfriend ?? string.Empty },
                            { "note", "The full girlfriend proposal response is ready and should now be spoken." }
                        }
                    });

                    GirlfriendProposalCompleted?.Invoke(this, proposalResponse);
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Girlfriend proposal generation cancelled for " + profile.StarId + ".");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Girlfriend proposal generation failed: " + ex);
                    GirlfriendProposalFailed?.Invoke(this, ex);
                }
            }, CancellationToken.None);
        }

        private sealed class GirlfriendProposalDecision
        {
            public string Decision { get; set; } = string.Empty;
            public string ExistingGirlfriendStarId { get; set; } = string.Empty;
            public bool IsAlreadyCurrentGirlfriend { get; set; }
            public bool IsBlockedByExistingGirlfriend { get; set; }
            public bool IsEligibleToAccept { get; set; }
            public bool ShouldAcceptByDeterministicGate { get; set; }
            public string Reason { get; set; } = string.Empty;
        }

        private static StarInteractionRequest CloneRequestForBackgroundStory(StarInteractionRequest request)
        {
            return new StarInteractionRequest
            {
                SessionId = request.SessionId,
                StarId = request.StarId,
                Speaker = request.Speaker,
                RequestType = RequestType.Story,
                PlayerText = request.PlayerText,
                GameContext = CloneGameContext(request.GameContext),
                StoryOptions = CloneStoryOptions(request.StoryOptions),
                AttachedPhoto = ClonePhotoAttachment(request.AttachedPhoto),
                RecentScreenCaptures = request.RecentScreenCaptures.Select(CloneScreenCaptureSummary).ToList()
            };
        }

        private static GameContext CloneGameContext(GameContext context)
        {
            return new GameContext
            {
                Area = context.Area,
                Quest = context.Quest,
                BattleState = context.BattleState,
                RomanceMode = context.RomanceMode,
                BenaveilPressureLevel = context.BenaveilPressureLevel,
                EquippedStars = context.EquippedStars.ToList(),
                RecentEvents = context.RecentEvents.ToList(),
                Flags = CloneFlagsCaseInsensitive(context.Flags)
            };
        }

        private static Dictionary<string, string> CloneFlagsCaseInsensitive(Dictionary<string, string> source)
        {
            Dictionary<string, string> clone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source == null) return clone;
            foreach (KeyValuePair<string, string> pair in source)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                // Use the indexer instead of the Dictionary copy constructor. Mobile/game hosts may
                // accidentally provide both `foo` and `Foo`; with a case-insensitive clone the copy
                // constructor throws "An item with the same key has already been added." The last value
                // wins, which is safer than failing the whole Star request.
                clone[pair.Key] = pair.Value ?? string.Empty;
            }
            return clone;
        }

        private static StoryOptions CloneStoryOptions(StoryOptions options)
        {
            return new StoryOptions
            {
                Length = options.Length,
                Tone = options.Tone,
                CanonStrictness = options.CanonStrictness,
                UseUnlockedSecrets = options.UseUnlockedSecrets,
                AllowPoetry = options.AllowPoetry,
                AskFollowupAfter = options.AskFollowupAfter,
                TargetMinutes = options.TargetMinutes,
                TargetChunkCount = options.TargetChunkCount,
                MaxSentencesPerChunk = options.MaxSentencesPerChunk,
                PlayerSuggestion = options.PlayerSuggestion,
                ContinueActiveStory = options.ContinueActiveStory,
                ActiveStoryId = options.ActiveStoryId,
                ActiveStoryTitle = options.ActiveStoryTitle,
                ActiveStoryMarkdown = options.ActiveStoryMarkdown,
                ActiveStorySummary = options.ActiveStorySummary,
                StoryProgressSoFar = options.StoryProgressSoFar,
                LastStorySpokenText = options.LastStorySpokenText,
                NextStoryChunkMarkdown = options.NextStoryChunkMarkdown,
                NextStoryPartIndex = options.NextStoryPartIndex,
                StoriesAlreadyTold = options.StoriesAlreadyTold.ToList()
            };
        }

        private static StarPhotoAttachment ClonePhotoAttachment(StarPhotoAttachment photo)
        {
            if (photo == null) return new StarPhotoAttachment();
            return new StarPhotoAttachment
            {
                ImageBytes = photo.ImageBytes == null ? Array.Empty<byte>() : photo.ImageBytes.ToArray(),
                MimeType = photo.MimeType ?? "image/jpeg",
                Caption = photo.Caption ?? string.Empty,
                ImageReference = photo.ImageReference ?? string.Empty,
                PhotoSummary = photo.PhotoSummary ?? string.Empty,
                PhotoError = photo.PhotoError ?? string.Empty
            };
        }

        private static ScreenCaptureSummary CloneScreenCaptureSummary(ScreenCaptureSummary capture)
        {
            return new ScreenCaptureSummary
            {
                CapturedAt = capture.CapturedAt,
                ActiveApp = capture.ActiveApp,
                ActivityType = capture.ActivityType,
                LikelyActivity = capture.LikelyActivity,
                CompanionSafeSummary = capture.CompanionSafeSummary,
                VisibleTextHighlights = capture.VisibleTextHighlights.ToList(),
                VisibleProblems = capture.VisibleProblems.ToList(),
                Sensitivity = capture.Sensitivity,
                ImageReference = capture.ImageReference,
                ImageBase64Jpeg = capture.ImageBase64Jpeg,
                Confidence = capture.Confidence
            };
        }

        private void QueueStoryGenerationAfterThinkingResponse(StarProfile profile, StarInteractionRequest request, StarState state, StarRequestIntentResult? requestIntent, CancellationToken cancellationToken)
        {
            // Clone the original request before leaving the foreground turn. The UI will immediately
            // start a separate TTS playback operation for the quick thinking phrase, and voice/VAD can
            // begin more operations while this long story LLM call is still running. A detached worker
            // request prevents later UI mutations or operation-token cancellation from corrupting the
            // actual story generation payload.
            StarInteractionRequest workerRequest = CloneRequestForBackgroundStory(request);
            workerRequest.GameContext.Flags["storyGenerationWorker"] = "true";
            workerRequest.GameContext.RecentEvents.Add("The Star already answered with a preprogrammed story-thinking phrase; now generate the actual story silently until it is ready to speak.");

            CancellationToken storyToken;
            lock (_backgroundGate)
            {
                _backgroundStoryGenerationCts?.Cancel();
                _backgroundStoryGenerationCts?.Dispose();
                _backgroundStoryGenerationCts = new CancellationTokenSource();
                storyToken = _backgroundStoryGenerationCts.Token;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background story generation started for " + profile.StarId + ". This is detached from the quick-thinking TTS operation token so the placeholder voice cannot cancel the story LLM call.");
                    StarInteractionResponse storyResponse = await _agent.GenerateAsync(profile, workerRequest, state, storyToken).ConfigureAwait(false);
                    if (requestIntent != null)
                    {
                        storyResponse.Debug.Notes.Add("Pre-flight story intent check: " + requestIntent.Source + " confidence=" + requestIntent.Confidence.ToString("0.00") + " story=" + requestIntent.IsStoryRequest + " reason=" + requestIntent.Reason);
                    }

                    ApplyVoiceDefaults(profile, storyResponse);
                    if (_agent is IStarTurnAnalyzer analyzer)
                    {
                        storyResponse.StateDeltas = RelationshipDelta.Zero();
                        storyResponse.UnityEvents.Add(new UnityEventEnvelope
                        {
                            Type = "BackgroundStarAnalysisQueued",
                            Target = workerRequest.StarId,
                            Data =
                            {
                                { "speechParts", storyResponse.SpokenParts.Count.ToString() },
                                { "note", "Full story foreground response generated. Background memory review will wait until story voice generation reports ready." }
                            }
                        });

                        string backgroundAnalysisId = Guid.NewGuid().ToString("N");
                        storyResponse.BackgroundAnalysisId = backgroundAnalysisId;
                        storyResponse.UnityEvents.Add(new UnityEventEnvelope
                        {
                            Type = "BackgroundStarAnalysisWaitingForVoice",
                            Target = workerRequest.StarId,
                            Data =
                            {
                                { "backgroundAnalysisId", backgroundAnalysisId },
                                { "note", "Call StartBackgroundAnalysisAfterVoiceReady after generated story voice playback completes." }
                            }
                        });
                        storyResponse.Debug.Notes.Add("Generated story foreground response after quick thinking phrase. Background review is waiting for story voice completion.");
                        storyResponse.Debug.Notes.Add(BuildStoryLengthDebugNote(workerRequest.StoryOptions));
                        StorePendingBackgroundAnalysis(backgroundAnalysisId, analyzer, profile, workerRequest, state, storyResponse);
                    }

                    storyResponse.UnityEvents.Add(new UnityEventEnvelope
                    {
                        Type = "StoryGenerationCompleted",
                        Target = workerRequest.StarId,
                        Data =
                        {
                            { "storyId", storyResponse.StoryId ?? string.Empty },
                            { "storyTitle", storyResponse.StoryTitle ?? string.Empty },
                            { "storyPartIndex", storyResponse.StoryPartIndex.ToString() },
                            { "approxTargetMinutes", (workerRequest.StoryOptions?.TargetMinutes ?? 0).ToString() },
                            { "approxTargetChunkCount", (workerRequest.StoryOptions?.TargetChunkCount ?? 0).ToString() },
                            { "storyLengthIsFlexible", "true" },
                            { "sessionId", workerRequest.SessionId ?? string.Empty },
                            { "mobileBridge", GetGameContextFlag(workerRequest.GameContext, "mobileBridge", "false") },
                            { "storyMarkdownChars", (storyResponse.StoryMarkdown?.Length ?? 0).ToString() },
                            { "note", "The full story response is ready and should now be spoken." }
                        }
                    });

                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background story generation completed for " + profile.StarId + ".");
                    StoryGenerationCompleted?.Invoke(this, storyResponse);
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background story generation cancelled for " + profile.StarId + ".");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background story generation failed: " + ex);
                    StoryGenerationFailed?.Invoke(this, ex);
                }
            }, CancellationToken.None);
        }

        private async Task<StarRequestIntentResult?> ClassifyAndPrepareRequestModeAsync(StarProfile profile, StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
        {
            StarRequestIntentResult? intent = null;

            if (request.RequestType == RequestType.Chat)
            {
                if (StoryIntentLexicon.TryFindStoryIntentTrigger(request.PlayerText, out string storyIntentTrigger))
                {
                    request.GameContext.Flags["storyIntentTrigger"] = storyIntentTrigger;
                    request.GameContext.Flags["storyIntentCheckSource"] = "llm_required_after_story_alias";
                    request.GameContext.RecentEvents.Add("A story-like word was detected (" + storyIntentTrigger + "), so the runtime is asking the micro-intent LLM classifier before changing modes.");

                    if (_agent is IStarRequestIntentClassifier classifier)
                    {
                        intent = await classifier.ClassifyRequestIntentAsync(profile, request, state, cancellationToken).ConfigureAwait(false);
                        request.GameContext.Flags["storyIntentCheckSource"] = intent.Source;
                        request.GameContext.Flags["storyIntentConfidence"] = intent.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                        request.GameContext.Flags["storyIntentReason"] = intent.Reason ?? string.Empty;

                        if (intent.IsStoryRequest && intent.Confidence >= 0.60)
                        {
                            request.GameContext.Flags["autoPromotedToStory"] = "true";
                            request.RequestType = RequestType.Story;
                            request.GameContext.RecentEvents.Add("Story micro-intent LLM confirmed Story mode with confidence " + intent.Confidence.ToString("0.00") + ".");
                        }
                        else
                        {
                            request.GameContext.Flags["autoPromotedToStory"] = "false";
                            request.GameContext.RecentEvents.Add("Story word detected, but micro-intent LLM kept this as Chat. Reason: " + (intent.Reason ?? string.Empty));
                        }
                    }
                    else
                    {
                        bool strictFallback = LooksLikeDirectStoryCommand(request.PlayerText);
                        intent = new StarRequestIntentResult
                        {
                            IsStoryRequest = strictFallback,
                            Confidence = strictFallback ? 0.62 : 0.20,
                            StorySeed = strictFallback ? (request.PlayerText ?? string.Empty) : string.Empty,
                            Source = "strict_story_heuristic_no_llm",
                            Reason = strictFallback ? "Direct story command detected without classifier." : "Story word was present but no direct story command was detected."
                        };
                        request.GameContext.Flags["storyIntentConfidence"] = intent.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                        request.GameContext.Flags["storyIntentReason"] = intent.Reason;
                        if (strictFallback) request.RequestType = RequestType.Story;
                    }
                }
                else if (_agent is IStarRequestIntentClassifier)
                {
                    request.GameContext.Flags["storyIntentTrigger"] = string.Empty;
                    request.GameContext.Flags["storyIntentCheckSource"] = "skipped_no_story_alias";
                    request.GameContext.Flags["storyIntentConfidence"] = "0.00";
                    request.GameContext.Flags["storyIntentReason"] = "Skipped story intent LLM check because the player text did not contain a story alias trigger word such as story, tale, fable, legend, myth, parable, saga, chronicle, or narration.";
                    request.GameContext.RecentEvents.Add("Skipped pre-flight story intent LLM classifier because the player text did not contain a story alias trigger word.");
                }
            }

            if (request.RequestType == RequestType.Story)
            {
                PrepareStoryOptionsForNewOrContinuingStory(request, state, intent);
            }

            return intent;
        }

        private static bool LooksLikeDirectStoryCommand(string? text)
        {
            string t = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (t.Length == 0) return false;
            return t.StartsWith("tell me a story")
                || t.StartsWith("tell a story")
                || t.StartsWith("tell me another story")
                || t.StartsWith("continue the story")
                || t.StartsWith("finish the story")
                || t.StartsWith("start a story")
                || t.StartsWith("make up a story")
                || t.StartsWith("give me a story")
                || t.StartsWith("narrate")
                || t.Contains("tell me a new long story")
                || t.Contains("can you tell me a story")
                || t.Contains("will you tell me a story");
        }

        private static void PrepareStoryOptionsForNewOrContinuingStory(StarInteractionRequest request, StarState state, StarRequestIntentResult? intent)
        {
            if (request.StoryOptions == null) request.StoryOptions = new StoryOptions();
            StoryOptions options = request.StoryOptions;
            if (options.MaxSentencesPerChunk <= 0) options.MaxSentencesPerChunk = 2;

            if (options.StoriesAlreadyTold.Count == 0 && state.StoryHistory.Count > 0)
            {
                foreach (StarStoryHistoryEntry story in state.StoryHistory.Take(20))
                {
                    options.StoriesAlreadyTold.Add(story.Title + " | finished=" + story.Finished + " | " + story.Summary);
                }
            }

            if (options.ContinueActiveStory)
            {
                if (string.IsNullOrWhiteSpace(options.Length)) options.Length = "continue_next_one_or_two_sentence_story_chunk";
                return;
            }

            if (options.TargetMinutes <= 0)
            {
                lock (StoryRandomGate)
                {
                    options.TargetMinutes = StoryRandom.Next(2, 36);
                }
            }

            if (options.TargetChunkCount <= 0)
            {
                // The target is intentionally flexible. Earlier builds asked for very large 2-45 minute
                // exact stories, which could cause local models to ramble, truncate, or never finish.
                // Aim for roughly 1-2 chunks per requested minute, but cap the first-pass plan so the
                // model can return a complete episode with a real ending.
                int flexibleMinuteTarget = Math.Max(2, Math.Min(35, options.TargetMinutes));
                options.TargetChunkCount = Math.Max(8, Math.Min(24, (int)Math.Ceiling(flexibleMinuteTarget * 0.8)));
            }

            options.Length = "flexible_random_" + options.TargetMinutes + "_minute_complete_story_" + options.TargetChunkCount + "_chunks";
            if (string.IsNullOrWhiteSpace(options.Tone)) options.Tone = "comforting, adventurous, cave-travel friendly";
            if (string.IsNullOrWhiteSpace(options.CanonStrictness)) options.CanonStrictness = "high";

            bool autoPromoted = request.GameContext.Flags.TryGetValue("autoPromotedToStory", out string? promoted)
                && string.Equals(promoted, "true", StringComparison.OrdinalIgnoreCase);
            if (autoPromoted || string.IsNullOrWhiteSpace(options.PlayerSuggestion))
            {
                options.PlayerSuggestion = !string.IsNullOrWhiteSpace(intent?.StorySeed)
                    ? intent!.StorySeed
                    : (request.PlayerText ?? string.Empty);
            }

            request.GameContext.Flags["storyTargetMinutes"] = options.TargetMinutes.ToString();
            request.GameContext.Flags["storyTargetChunkCount"] = options.TargetChunkCount.ToString();
            request.GameContext.Flags["storyMaxSentencesPerChunk"] = options.MaxSentencesPerChunk.ToString();
            request.GameContext.Flags["storyLengthIsFlexible"] = "true";
        }

        private static string BuildStoryLengthDebugNote(StoryOptions? options)
        {
            if (options == null)
            {
                return "Story randomizer: no StoryOptions were attached.";
            }

            return "Story randomizer: approximate target "
                + options.TargetMinutes
                + " minute(s), "
                + options.TargetChunkCount
                + " chunk(s), max "
                + (options.MaxSentencesPerChunk <= 0 ? 2 : options.MaxSentencesPerChunk)
                + " sentence(s) per chunk. Flexible target: complete story is more important than exact duration.";
        }

        private void StorePendingBackgroundAnalysis(string analysisId, IStarTurnAnalyzer analyzer, StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse foregroundResponse)
        {
            lock (_backgroundGate)
            {
                _pendingBackgroundAnalysis[analysisId] = new PendingBackgroundAnalysis
                {
                    AnalysisId = analysisId,
                    Analyzer = analyzer,
                    Profile = profile,
                    Request = request,
                    StateBeforeTurn = state,
                    ForegroundResponse = foregroundResponse
                };
            }
            System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background analysis " + analysisId + " is waiting for foreground voice generation to complete.");
        }

        public bool StartBackgroundAnalysisAfterVoiceReady(string analysisId, string reason = "Foreground voice generation completed")
        {
            if (string.IsNullOrWhiteSpace(analysisId))
            {
                return false;
            }

            PendingBackgroundAnalysis? pending;
            lock (_backgroundGate)
            {
                if (!_pendingBackgroundAnalysis.TryGetValue(analysisId, out pending))
                {
                    return false;
                }

                _pendingBackgroundAnalysis.Remove(analysisId);
            }

            System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Starting background analysis " + analysisId + " after voice ready: " + reason);
            QueueBackgroundAnalysis(pending.Analyzer, pending.Profile, pending.Request, pending.StateBeforeTurn, pending.ForegroundResponse);
            return true;
        }

        private void QueueBackgroundAnalysis(IStarTurnAnalyzer analyzer, StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse foregroundResponse)
        {
            CancellationToken token;
            lock (_backgroundGate)
            {
                if (_backgroundAnalysisCts == null || _backgroundAnalysisCts.IsCancellationRequested)
                {
                    _backgroundAnalysisCts?.Dispose();
                    _backgroundAnalysisCts = new CancellationTokenSource();
                }
                token = _backgroundAnalysisCts.Token;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    // Keep background state writes ordered. A later Unity/UI host can choose to wait on this
                    // semaphore before accepting the next speech turn if it needs fully settled state.
                    await _backgroundAnalysisQueue.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        StarInteractionResponse analyzed = await analyzer.AnalyzeTurnAsync(profile, request, state, foregroundResponse, token).ConfigureAwait(false);
                        ApplyVoiceDefaults(profile, analyzed);
                        await ApplyAnalysisAndMemoryUpdateAsync(request, analyzed, token).ConfigureAwait(false);
                        BackgroundAnalysisCompleted?.Invoke(this, analyzed);
                        NotifyInterstarStatus(request.SessionId, request.StarId, string.Empty, "PrimaryBackgroundCompleted", "Primary background emotion/memory review settled for " + request.StarId + "; checking interstar ripple eligibility.", 0, 0);
                        ScheduleInterstarCycleAfterSettlement(request, analyzed);
                    }
                    finally
                    {
                        _backgroundAnalysisQueue.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background emotion/memory analysis cancelled.");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background emotion/memory analysis failed: " + ex);
                    BackgroundAnalysisFailed?.Invoke(this, ex);
                }
            }, CancellationToken.None);
        }

        private async Task ApplyAnalysisAndMemoryUpdateAsync(StarInteractionRequest request, StarInteractionResponse response, CancellationToken cancellationToken)
        {
            StarState currentState = await _memoryApi.GetStarStateAsync(request.SessionId, request.StarId, cancellationToken).ConfigureAwait(false);
            response.EmotionalConnectionPercentBefore = currentState.EmotionalConnectionPercent;
            RelationshipDelta delta = _deltaCalculator.Calculate(_profileProvider.GetProfile(request.StarId), request, response, currentState);
            response.StateDeltas = delta;
            bool isStoryTurn = request.RequestType == RequestType.Story;

            StarStateUpdateResult update = await _memoryApi.UpdateStarWithInteractionAsync(new StarStateUpdateRequest
            {
                SessionId = request.SessionId,
                StarId = request.StarId,
                RequestType = request.RequestType.ToString(),
                PlayerText = request.PlayerText ?? string.Empty,
                StarResponse = response.SpokenText,
                ConversationSummary = response.ConversationSummary,
                ChangesSummary = response.ChangesSummary,
                ActualDelta = delta,
                SuggestedDelta = response.SuggestedStateDeltas,
                EmotionalEffectVector = response.EmotionalEffectVector,
                NegativeEmotionVector = response.NegativeEmotionVector,
                DeterministicEmotionSummary = response.DeterministicEmotionUpdateSummary,
                AcceptedPositiveSignals = response.AcceptedPositiveEmotionSignals,
                AcceptedNegativeSignals = response.AcceptedNegativeEmotionSignals,
                AcceptedCombos = response.AcceptedRelationshipCombos,
                EmotionScores = response.EmotionScores,
                ImportantPlayerQuotes = response.ImportantPlayerQuotes,
                MemoryNotes = response.MemoryNotes,
                RememberedMarkers = response.RememberedMarkers,
                ReferencedMemoryMarkerIds = response.ReferencedMemoryMarkerIds,
                SecretsUnlocked = response.SecretsUnlocked,
                DetectedCombos = response.DetectedCombos,
                LearnedFunctions = response.LearnedFunctions,
                RelationshipEvents = response.RelationshipEvents,
                GirlfriendHistoryUpdates = response.GirlfriendHistoryUpdates,
                PetNamesPlayerUsesForStar = response.PetNamesPlayerUsesForStar,
                PetNamesStarUsesForPlayer = response.PetNamesStarUsesForPlayer,
                WisdomScore = response.Debug.WisdomScore,
                StoryId = isStoryTurn ? (response.StoryId ?? string.Empty) : string.Empty,
                StoryTitle = isStoryTurn ? (response.StoryTitle ?? string.Empty) : string.Empty,
                StorySummary = isStoryTurn ? (response.StorySummary ?? string.Empty) : string.Empty,
                StoryFinished = isStoryTurn && response.StoryIsFinished,
                StorySeed = isStoryTurn ? (request.StoryOptions?.PlayerSuggestion ?? string.Empty) : string.Empty
            }, cancellationToken).ConfigureAwait(false);

            response.EmotionalConnectionPercentAfter = update.State.EmotionalConnectionPercent;
            response.IsGirlfriendReadyAfter = update.State.IsGirlfriendReady;
            response.GirlfriendReadinessSummary = update.State.GirlfriendReadinessSummary ?? string.Empty;
            response.TopAffectedEmotions.Clear();
            response.TopAffectedEmotions.AddRange((update.State.LastTopAffectedEmotions ?? new List<TopAffectedEmotion>()));

            response.UnityEvents.Add(new UnityEventEnvelope
            {
                Type = "RelationshipChanged",
                Target = request.StarId,
                Data =
                {
                    { "heartDelta", delta.Heart.ToString() },
                    { "trustDelta", delta.Trust.ToString() },
                    { "romanceDelta", delta.Romance.ToString() },
                    { "benaveilInfluenceDelta", delta.BenaveilInfluence.ToString() },
                    { "emotionalConnectionPercentDelta", delta.EmotionalConnectionPercent.ToString("0.00") },
                    { "emotionalConnectionPercentBefore", response.EmotionalConnectionPercentBefore.ToString("0.0") },
                    { "emotionalConnectionPercentAfter", response.EmotionalConnectionPercentAfter.ToString("0.0") },
                    { "isGirlfriendReadyAfter", response.IsGirlfriendReadyAfter ? "true" : "false" },
                    { "girlfriendReadinessSummary", response.GirlfriendReadinessSummary ?? string.Empty },
                    { "suggestedHeartDelta", response.SuggestedStateDeltas.Heart.ToString() },
                    { "suggestedTrustDelta", response.SuggestedStateDeltas.Trust.ToString() },
                    { "suggestedRomanceDelta", response.SuggestedStateDeltas.Romance.ToString() },
                    { "suggestedBenaveilDelta", response.SuggestedStateDeltas.BenaveilInfluence.ToString() },
                    { "suggestedEmotionalConnectionPercentDelta", response.SuggestedStateDeltas.EmotionalConnectionPercent.ToString("0.00") },
                    { "emotionalEffectVector", response.EmotionalEffectVector ?? string.Empty },
                    { "negativeEmotionVector", response.NegativeEmotionVector ?? string.Empty },
                    { "acceptedPositiveSignals", string.Join(", ", response.AcceptedPositiveEmotionSignals) },
                    { "acceptedNegativeSignals", string.Join(", ", response.AcceptedNegativeEmotionSignals) },
                    { "acceptedCombos", string.Join(", ", response.AcceptedRelationshipCombos) },
                    { "deterministicEmotionSummary", response.DeterministicEmotionUpdateSummary ?? string.Empty },
                    { "topAffectedEmotions", string.Join(", ", response.TopAffectedEmotions.Select(x => x.ToString())) },
                    { "rememberedMarkers", string.Join(", ", response.RememberedMarkers.Select(x => string.IsNullOrWhiteSpace(x.MarkerName) ? x.Id : x.MarkerName)) },
                    { "referencedMemoryMarkerIds", string.Join(", ", response.ReferencedMemoryMarkerIds) }
                }
            });

            response.UnityEvents.AddRange(update.UnityEvents);
            response.Debug.Notes.Add("StarMemoryApi updated durable state from background/full analysis. Interaction count: " + update.State.InteractionCount + ".");
            if (!IsInterstarMicroRequest(request) && request.RequestType != RequestType.Debug)
            {
                string logPath = _conversationDebugLogRecorder.RecordPrimaryTurn(request, response, currentState, update.State);
                if (!string.IsNullOrWhiteSpace(logPath)) response.Debug.Notes.Add("Conversation emotion trace written to: " + logPath);
            }
        }


        private static bool IsInterstarMicroRequest(StarInteractionRequest request)
        {
            return request?.GameContext?.Flags != null
                && request.GameContext.Flags.TryGetValue("interstarMicroUpdate", out string? micro)
                && string.Equals(micro, "true", StringComparison.OrdinalIgnoreCase);
        }

        private void ScheduleInterstarCycleAfterSettlement(StarInteractionRequest request, StarInteractionResponse originResponse)
        {
            if (!(_agent is IInterstarUpdateAgent interstarAgent))
            {
                NotifyInterstarStatus(request.SessionId, request.StarId, string.Empty, "Unavailable", "Interstar ripple skipped because the active Star agent does not implement interstar update mode.", 0, 0);
                return;
            }

            if (!IsEligibleForInterstarCycle(request, originResponse))
            {
                NotifyInterstarStatus(request.SessionId, request.StarId, string.Empty, "NotQueued", "Interstar ripple not queued for " + request.StarId + "; this turn had no settled emotion vectors/top emotions or was an internal/debug update.", 0, 0);
                return;
            }

            CancellationTokenSource cts;
            lock (_interstarGate)
            {
                _interstarCycleCts?.Cancel();
                _interstarCycleCts?.Dispose();
                _interstarCycleCts = new CancellationTokenSource();
                cts = _interstarCycleCts;
            }

            int delaySeconds = (int)Math.Round(_interstarSettleDelay.TotalSeconds);
            NotifyInterstarStatus(request.SessionId, request.StarId, string.Empty, "Queued", "Interstar emotional ripple queued for " + request.StarId + "; waiting " + delaySeconds + " second(s) for conversation to settle.", 0, 0);
            originResponse.UnityEvents.Add(new UnityEventEnvelope
            {
                Type = "InterstarUpdateQueued",
                Target = request.StarId,
                Data =
                {
                    { "sessionId", request.SessionId ?? string.Empty },
                    { "originStarId", request.StarId ?? string.Empty },
                    { "settleDelaySeconds", delaySeconds.ToString() },
                    { "note", "Interstar updates begin only after the primary background response and the settle timeout." }
                }
            });

            StarInteractionRequest requestClone = CloneInterstarSourceRequest(request);
            StarInteractionResponse responseClone = CloneInterstarOriginResponse(originResponse);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_interstarSettleDelay, cts.Token).ConfigureAwait(false);
                    await _interstarUpdateQueue.WaitAsync(cts.Token).ConfigureAwait(false);
                    try
                    {
                        await RunInterstarCycleAsync(interstarAgent, requestClone, responseClone, cts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _interstarUpdateQueue.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    NotifyInterstarStatus(request.SessionId, request.StarId, string.Empty, "Cancelled", "Interstar ripple cancelled before it settled, usually because a newer conversation started.", 0, 0);
                }
                catch (Exception ex)
                {
                    NotifyInterstarStatus(request.SessionId, request.StarId, string.Empty, "Failed", "Interstar ripple failed: " + ex.Message, 0, 0);
                    InterstarUpdateFailed?.Invoke(this, ex);
                }
            }, CancellationToken.None);
        }

        private async Task RunInterstarCycleAsync(IInterstarUpdateAgent interstarAgent, StarInteractionRequest originalRequest, StarInteractionResponse originResponse, CancellationToken cancellationToken)
        {
            string sessionId = string.IsNullOrWhiteSpace(originalRequest.SessionId) ? "local-dev" : originalRequest.SessionId;
            string originStarId = originalRequest.StarId;
            StarProfile originProfile = _profileProvider.GetProfile(originStarId);
            StarState originState = await _memoryApi.GetStarStateAsync(sessionId, originStarId, cancellationToken).ConfigureAwait(false);
            List<StarProfile> targets = new List<StarProfile>();

            foreach (StarProfile candidate in _profileProvider.GetAllProfiles())
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.StarId)) continue;
                if (string.Equals(candidate.StarId, originStarId, StringComparison.OrdinalIgnoreCase)) continue;
                StarState candidateState = await _memoryApi.GetStarStateAsync(sessionId, candidate.StarId, cancellationToken).ConfigureAwait(false);
                if (IsInterstarTargetEligible(originProfile, originState, candidate, candidateState))
                {
                    targets.Add(candidate);
                }
            }

            targets = targets.Take(5).ToList();
            NotifyInterstarStatus(sessionId, originStarId, string.Empty, "Started", "Interstar ripple started for " + originStarId + " with " + targets.Count + " eligible target Star(s).", targets.Count, 0);

            InterstarUpdateCycleResult cycle = new InterstarUpdateCycleResult
            {
                SessionId = sessionId,
                OriginStarId = originStarId,
                Message = "Interstar ripple completed for " + originStarId + "."
            };

            int completed = 0;
            foreach (StarProfile targetProfile in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StarState targetState = await _memoryApi.GetStarStateAsync(sessionId, targetProfile.StarId, cancellationToken).ConfigureAwait(false);
                NotifyInterstarStatus(sessionId, originStarId, targetProfile.StarId, "RelayStarted", "Asking " + originProfile.DisplayName + " what she would tell " + targetProfile.DisplayName + " about the Last Cat.", targets.Count, completed);

                InterstarRelayMessage relay = await interstarAgent.GenerateInterstarRelayAsync(originProfile, targetProfile, originalRequest, originResponse, originState, targetState, cancellationToken).ConfigureAwait(false);
                _conversationDebugLogRecorder.AppendInterstarRelay(relay, originalRequest, originResponse, originState, targetState);
                if (!relay.ShouldTell || string.IsNullOrWhiteSpace(relay.WhatOriginWouldTellTarget))
                {
                    NotifyInterstarStatus(sessionId, originStarId, targetProfile.StarId, "Skipped", originProfile.DisplayName + " had no meaningful interstar relay for " + targetProfile.DisplayName + ".", targets.Count, completed);
                    continue;
                }

                StarInterstarInfluenceRule targetRule = ResolveInterstarRule(originProfile, targetProfile.StarId);
                if (!TryReserveInterstarCooldown(sessionId, originStarId, targetProfile.StarId, targetRule, relay.CooldownKey))
                {
                    NotifyInterstarStatus(sessionId, originStarId, targetProfile.StarId, "Skipped", originProfile.DisplayName + " already sent a similar interstar ripple to " + targetProfile.DisplayName + " recently; cooldown is still active.", targets.Count, completed);
                    continue;
                }

                NotifyInterstarStatus(sessionId, originStarId, targetProfile.StarId, "ReactionStarted", "Asking " + targetProfile.DisplayName + " how she feels about the Last Cat after hearing " + originProfile.DisplayName + "'s relay.", targets.Count, completed);
                StarInteractionResponse targetReaction = await interstarAgent.AnalyzeInterstarReactionAsync(targetProfile, relay, originalRequest, originResponse, targetState, cancellationToken).ConfigureAwait(false);
                StarInteractionRequest targetRequest = CreateInterstarMicroRequest(originalRequest, targetProfile.StarId, "Interstar relay from " + originProfile.DisplayName + ": " + relay.WhatOriginWouldTellTarget);
                await ApplyAnalysisAndMemoryUpdateAsync(targetRequest, targetReaction, cancellationToken).ConfigureAwait(false);
                StarState targetStateAfter = await _memoryApi.GetStarStateAsync(sessionId, targetProfile.StarId, cancellationToken).ConfigureAwait(false);
                _conversationDebugLogRecorder.AppendInterstarReaction(originStarId, targetProfile.StarId, relay, targetReaction, targetState, targetStateAfter);
                InterstarMicroUpdateCompleted?.Invoke(this, targetReaction);

                completed++;
                string top = FormatTopAffectedForInterstar(targetReaction.TopAffectedEmotions);
                cycle.Reactions.Add(new InterstarReactionSummary
                {
                    StarId = targetProfile.StarId,
                    SourceStarId = originStarId,
                    RelaySummary = relay.Summary,
                    EmotionalEffectVector = targetReaction.EmotionalEffectVector ?? string.Empty,
                    NegativeEmotionVector = targetReaction.NegativeEmotionVector ?? string.Empty,
                    AppliedDelta = targetReaction.StateDeltas ?? RelationshipDelta.Zero(),
                    TopAffectedEmotionsText = top,
                    ReplyToSourceStar = targetReaction.InterstarReplyToSourceStar ?? string.Empty,
                    DeterministicSummary = targetReaction.DeterministicEmotionUpdateSummary ?? string.Empty
                });
                string replyText = string.IsNullOrWhiteSpace(targetReaction.InterstarReplyToSourceStar)
                    ? string.Empty
                    : " Said back to " + originProfile.DisplayName + ": \"" + targetReaction.InterstarReplyToSourceStar + "\"";
                NotifyInterstarStatus(sessionId, originStarId, targetProfile.StarId, "ReactionCompleted", targetProfile.DisplayName + " interstar vector settled. " + top + replyText, targets.Count, completed);
            }

            if (cycle.Reactions.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NotifyInterstarStatus(sessionId, originStarId, originStarId, "FeedbackStarted", "Asking " + originProfile.DisplayName + " how the other Stars' reactions affect her feelings about the Last Cat.", targets.Count, completed);
                StarState latestOriginState = await _memoryApi.GetStarStateAsync(sessionId, originStarId, cancellationToken).ConfigureAwait(false);
                StarInteractionResponse originFeedback = await interstarAgent.AnalyzeInterstarFeedbackAsync(originProfile, originalRequest, originResponse, latestOriginState, cycle.Reactions, cancellationToken).ConfigureAwait(false);
                StarInteractionRequest feedbackRequest = CreateInterstarMicroRequest(originalRequest, originStarId, "Interstar feedback from affected Stars after " + originProfile.DisplayName + " shared the recent Last Cat conversation.");
                await ApplyAnalysisAndMemoryUpdateAsync(feedbackRequest, originFeedback, cancellationToken).ConfigureAwait(false);
                StarState originStateAfterFeedback = await _memoryApi.GetStarStateAsync(sessionId, originStarId, cancellationToken).ConfigureAwait(false);
                _conversationDebugLogRecorder.AppendInterstarFeedback(originStarId, cycle.Reactions, originFeedback, latestOriginState, originStateAfterFeedback);
                cycle.OriginFeedbackApplied = true;
                InterstarMicroUpdateCompleted?.Invoke(this, originFeedback);
                NotifyInterstarStatus(sessionId, originStarId, originStarId, "FeedbackCompleted", originProfile.DisplayName + " feedback vector settled. " + FormatTopAffectedForInterstar(originFeedback.TopAffectedEmotions), targets.Count, completed);
            }

            string repliesSummary = FormatInterstarReplies(cycle.Reactions);
            cycle.Message = "Interstar ripple completed for " + originProfile.DisplayName + ": " + completed + " target update(s), origin feedback=" + cycle.OriginFeedbackApplied + "." + (string.IsNullOrWhiteSpace(repliesSummary) ? string.Empty : " Replies: " + repliesSummary);
            NotifyInterstarStatus(sessionId, originStarId, string.Empty, "Completed", cycle.Message, targets.Count, completed);
            InterstarUpdateCycleCompleted?.Invoke(this, cycle);
        }

        private bool IsEligibleForInterstarCycle(StarInteractionRequest request, StarInteractionResponse response)
        {
            if (request == null || response == null) return false;
            if (request.GameContext != null && request.GameContext.Flags.TryGetValue("interstarMicroUpdate", out string? micro) && string.Equals(micro, "true", StringComparison.OrdinalIgnoreCase)) return false;
            if (request.RequestType == RequestType.Debug) return false;
            if (string.IsNullOrWhiteSpace(request.StarId)) return false;
            if (string.IsNullOrWhiteSpace(response.EmotionalEffectVector) && string.IsNullOrWhiteSpace(response.NegativeEmotionVector) && response.TopAffectedEmotions.Count == 0) return false;
            return true;
        }

        private bool IsInterstarTargetEligible(StarProfile originProfile, StarState originState, StarProfile targetProfile, StarState targetState)
        {
            StarInterstarInfluenceRule rule = ResolveInterstarRule(originProfile, targetProfile.StarId);
            if (!rule.Enabled) return false;
            if (rule.RequiresOriginAlly && !originState.IsAlly) return false;
            if (rule.RequiresTargetAlly && !targetState.IsAlly) return false;
            if (rule.RequiresTargetMet && !targetState.IsMet && !targetState.IsAlly) return false;
            if (originState.EmotionalConnectionPercent < rule.MinOriginConnectionPercent) return false;
            if (targetState.EmotionalConnectionPercent < rule.MinTargetConnectionPercent) return false;
            return true;
        }

        private static StarInterstarInfluenceRule ResolveInterstarRule(StarProfile originProfile, string targetStarId)
        {
            StarInterstarInfluenceRule? authored = (originProfile.InterstarRules ?? new List<StarInterstarInfluenceRule>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.TargetStarId))
                .LastOrDefault(x => string.Equals(x.TargetStarId, targetStarId, StringComparison.OrdinalIgnoreCase));
            if (authored != null) return authored;
            return new StarInterstarInfluenceRule
            {
                TargetStarId = targetStarId,
                Enabled = true,
                RequiresOriginAlly = true,
                RequiresTargetMet = true,
                RequiresTargetAlly = false,
                RelayWeight = 1.0,
                PositiveMultiplier = 1.0,
                NegativeMultiplier = 1.0,
                CooldownSeconds = 600,
                MaxPerCycle = 1
            };
        }


        private bool TryReserveInterstarCooldown(string sessionId, string originStarId, string targetStarId, StarInterstarInfluenceRule rule, string? relayCooldownKey)
        {
            int cooldownSeconds = Math.Max(0, rule?.CooldownSeconds ?? 0);
            if (cooldownSeconds <= 0) return true;
            string key = string.Join("|",
                string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId.Trim(),
                string.IsNullOrWhiteSpace(originStarId) ? "origin" : originStarId.Trim(),
                string.IsNullOrWhiteSpace(targetStarId) ? "target" : targetStarId.Trim(),
                string.IsNullOrWhiteSpace(relayCooldownKey) ? "default" : relayCooldownKey.Trim());
            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (_interstarGate)
            {
                if (_interstarCooldowns.TryGetValue(key, out DateTimeOffset last) && (now - last).TotalSeconds < cooldownSeconds)
                {
                    return false;
                }
                _interstarCooldowns[key] = now;
                if (_interstarCooldowns.Count > 256)
                {
                    DateTimeOffset cutoff = now.AddHours(-6);
                    foreach (string oldKey in _interstarCooldowns.Where(x => x.Value < cutoff).Select(x => x.Key).ToList())
                    {
                        _interstarCooldowns.Remove(oldKey);
                    }
                }
                return true;
            }
        }

        private void CancelPendingInterstarCycle(string reason)
        {
            lock (_interstarGate)
            {
                if (_interstarCycleCts == null) return;
                try
                {
                    _interstarCycleCts.Cancel();
                    _interstarCycleCts.Dispose();
                }
                catch
                {
                }
                _interstarCycleCts = null;
            }
            System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Interstar cycle cancelled: " + reason);
        }

        private void NotifyInterstarStatus(string sessionId, string originStarId, string targetStarId, string phase, string message, int queuedTargets, int completedTargets)
        {
            InterstarUpdateStatusEventArgs args = new InterstarUpdateStatusEventArgs
            {
                SessionId = sessionId ?? string.Empty,
                OriginStarId = originStarId ?? string.Empty,
                TargetStarId = targetStarId ?? string.Empty,
                Phase = phase ?? string.Empty,
                Message = message ?? string.Empty,
                QueuedTargets = queuedTargets,
                CompletedTargets = completedTargets,
                CreatedAt = DateTimeOffset.UtcNow
            };
            System.Diagnostics.Debug.WriteLine("[Interstar] " + args.Phase + " " + args.OriginStarId + "->" + args.TargetStarId + ": " + args.Message);
            InterstarUpdateStatusChanged?.Invoke(this, args);
        }

        private static StarInteractionRequest CreateInterstarMicroRequest(StarInteractionRequest source, string targetStarId, string syntheticPlayerText)
        {
            GameContext context = CloneGameContext(source.GameContext ?? new GameContext());
            context.Flags["interstarMicroUpdate"] = "true";
            context.Flags["interstarOriginStarId"] = source.StarId ?? string.Empty;
            context.RecentEvents.Add("Internal interstar emotional ripple update. This is not a direct player statement; it is a micro-update caused by a settled Star conversation.");
            return new StarInteractionRequest
            {
                SessionId = source.SessionId,
                StarId = targetStarId,
                Speaker = "InterstarSystem",
                RequestType = RequestType.BattleUpdate,
                PlayerText = syntheticPlayerText ?? string.Empty,
                GameContext = context,
                StoryOptions = new StoryOptions(),
                AttachedPhoto = new StarPhotoAttachment(),
                RecentScreenCaptures = source.RecentScreenCaptures.Select(CloneScreenCaptureSummary).ToList()
            };
        }

        private static StarInteractionRequest CloneInterstarSourceRequest(StarInteractionRequest source)
        {
            return new StarInteractionRequest
            {
                SessionId = source.SessionId,
                StarId = source.StarId,
                Speaker = source.Speaker,
                RequestType = source.RequestType,
                PlayerText = source.PlayerText,
                GameContext = CloneGameContext(source.GameContext ?? new GameContext()),
                StoryOptions = CloneStoryOptions(source.StoryOptions),
                AttachedPhoto = ClonePhotoAttachment(source.AttachedPhoto),
                RecentScreenCaptures = source.RecentScreenCaptures.Select(CloneScreenCaptureSummary).ToList()
            };
        }

        private static StarInteractionResponse CloneInterstarOriginResponse(StarInteractionResponse source)
        {
            StarInteractionResponse clone = new StarInteractionResponse
            {
                StarId = source.StarId,
                EmotionalPurpose = source.EmotionalPurpose,
                EmotionalEffectVector = source.EmotionalEffectVector,
                NegativeEmotionVector = source.NegativeEmotionVector,
                DeterministicEmotionUpdateSummary = source.DeterministicEmotionUpdateSummary,
                SuggestedStateDeltas = source.SuggestedStateDeltas ?? RelationshipDelta.Zero(),
                StateDeltas = source.StateDeltas ?? RelationshipDelta.Zero(),
                ConversationSummary = source.ConversationSummary,
                ChangesSummary = source.ChangesSummary,
                InterstarReplyToSourceStar = source.InterstarReplyToSourceStar
            };
            clone.SetSpokenParts(source.SpokenParts);
            clone.TopAffectedEmotions.AddRange(source.TopAffectedEmotions ?? new List<TopAffectedEmotion>());
            clone.AcceptedPositiveEmotionSignals.AddRange(source.AcceptedPositiveEmotionSignals ?? new List<string>());
            clone.AcceptedNegativeEmotionSignals.AddRange(source.AcceptedNegativeEmotionSignals ?? new List<string>());
            clone.AcceptedRelationshipCombos.AddRange(source.AcceptedRelationshipCombos ?? new List<string>());
            return clone;
        }

        private static string FormatTopAffectedForInterstar(IEnumerable<TopAffectedEmotion>? emotions)
        {
            List<TopAffectedEmotion> list = (emotions ?? Enumerable.Empty<TopAffectedEmotion>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.EmotionId))
                .OrderByDescending(x => x.WeightedScore)
                .ThenByDescending(x => x.Score)
                .ToList();
            if (list.Count == 0) return "top emotions: none";
            return "top emotions: " + string.Join(" | ", list.Select(x => x.ToString()));
        }

        private static string FormatInterstarReplies(IEnumerable<InterstarReactionSummary>? reactions)
        {
            List<string> replies = (reactions ?? Enumerable.Empty<InterstarReactionSummary>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ReplyToSourceStar))
                .Select(x => x.StarId + " said \"" + x.ReplyToSourceStar.Trim() + "\"")
                .Take(8)
                .ToList();
            return replies.Count == 0 ? string.Empty : string.Join("; ", replies);
        }

        private static void ApplyVoiceDefaults(StarProfile profile, StarInteractionResponse response)
        {
            if (response.VoiceDirective == null)
            {
                response.VoiceDirective = VoiceDirective.Empty();
            }

            if (string.IsNullOrWhiteSpace(response.VoiceDirective.VoiceId))
            {
                response.VoiceDirective.VoiceId = string.IsNullOrWhiteSpace(profile.VoiceId) ? profile.StarId : profile.VoiceId;
            }

            string stableVoiceIdentity = BuildStableVoiceIdentity(profile);
            if (string.IsNullOrWhiteSpace(response.VoiceDirective.VoiceDesignPrompt))
            {
                response.VoiceDirective.VoiceDesignPrompt = stableVoiceIdentity;
            }
            else if (!response.VoiceDirective.VoiceDesignPrompt.Contains(profile.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                response.VoiceDirective.VoiceDesignPrompt = stableVoiceIdentity + " Per-reply voice note: " + response.VoiceDirective.VoiceDesignPrompt.Trim();
            }

            if (string.IsNullOrWhiteSpace(response.VoiceDirective.DeliveryInstructions))
            {
                response.VoiceDirective.DeliveryInstructions = BuildDefaultDeliveryInstructions(profile);
            }

            // Clone audio and reference text are trusted profile configuration, not model output.
            // Local LLMs occasionally hallucinate values such as "elusiaa-dev.mp3" or
            // "elusiaa-dev_stable", which causes Qwen VoiceClone to fail. Always prefer
            // the selected Star profile for the actual clone file/ref text.
            if (!string.IsNullOrWhiteSpace(profile.VoiceCloneAudioFile))
            {
                response.VoiceDirective.VoiceCloneAudioFile = profile.VoiceCloneAudioFile;
            }

            if (!string.IsNullOrWhiteSpace(profile.VoiceCloneRefText))
            {
                response.VoiceDirective.VoiceCloneRefText = profile.VoiceCloneRefText;
            }
        }

        private static string BuildStableVoiceIdentity(StarProfile profile)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("Star voice identity for ").Append(profile.DisplayName).Append(": ");
            if (!string.IsNullOrWhiteSpace(profile.PersonalityShort)) sb.Append("PersonalityShort: ").Append(profile.PersonalityShort.Trim()).Append(' ');
            if (!string.IsNullOrWhiteSpace(profile.VoiceArchetype)) sb.Append("VoiceArchetype: ").Append(profile.VoiceArchetype.Trim()).Append(' ');
            if (!string.IsNullOrWhiteSpace(profile.SpeakingStyle)) sb.Append("SpeakingStyle: ").Append(profile.SpeakingStyle.Trim()).Append(' ');
            if (!string.IsNullOrWhiteSpace(profile.VoiceDesignPrompt)) sb.Append("VoiceDesignPrompt: ").Append(profile.VoiceDesignPrompt.Trim()).Append(' ');
            if (profile.VoiceStyleRules.Count > 0) sb.Append("VoiceStyleRules: ").Append(string.Join("; ", profile.VoiceStyleRules)).Append(". ");
            if (profile.VoiceGenerationRules.Count > 0) sb.Append("VoiceGenerationRules: ").Append(string.Join("; ", profile.VoiceGenerationRules)).Append(". ");
            if (sb.Length <= ("Star voice identity for " + profile.DisplayName + ": ").Length && !string.IsNullOrWhiteSpace(profile.VoiceDirection))
            {
                sb.Append(profile.VoiceDirection.Trim());
            }

            return sb.ToString().Trim();
        }

        private static string BuildDefaultDeliveryInstructions(StarProfile profile)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("Act as ").Append(profile.DisplayName).Append(" speaking directly to the Last Cat. ");
            if (!string.IsNullOrWhiteSpace(profile.SpeakingStyle))
            {
                sb.Append(profile.SpeakingStyle.Trim()).Append(' ');
            }
            if (profile.VoiceGenerationRules.Count > 0)
            {
                sb.Append("For the generated voice: ").Append(string.Join("; ", profile.VoiceGenerationRules)).Append(". ");
            }
            sb.Append("Keep the delivery natural, emotionally grounded, and suitable for the exact spokenText.");
            return sb.ToString().Trim();
        }

        private sealed class PendingBackgroundAnalysis
        {
            public string AnalysisId { get; set; } = string.Empty;
            public IStarTurnAnalyzer Analyzer { get; set; } = default!;
            public StarProfile Profile { get; set; } = default!;
            public StarInteractionRequest Request { get; set; } = default!;
            public StarState StateBeforeTurn { get; set; } = default!;
            public StarInteractionResponse ForegroundResponse { get; set; } = default!;
        }
    }
}
