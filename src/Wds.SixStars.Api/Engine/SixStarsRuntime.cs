using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Background;
using Wds.SixStars.Api.GirlfriendState;
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
        private CancellationTokenSource? _backgroundStoryGenerationCts;
        private readonly SemaphoreSlim _backgroundAnalysisQueue = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, PendingBackgroundAnalysis> _pendingBackgroundAnalysis = new Dictionary<string, PendingBackgroundAnalysis>(StringComparer.OrdinalIgnoreCase);
        private readonly StarDailyStateService? _dailyStateService;
        private readonly object _interstarGate = new object();
        private CancellationTokenSource? _interstarCycleCts;
        private readonly SemaphoreSlim _interstarUpdateQueue = new SemaphoreSlim(1, 1);
        private readonly TimeSpan _interstarSettleDelay = ResolveInterstarSettleDelay();
        private readonly Dictionary<string, DateTimeOffset> _interstarCooldowns = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        private readonly BackgroundAiRequestDispatcher _backgroundDispatcher = new BackgroundAiRequestDispatcher();
        private readonly object _girlfriendHourlyStateGate = new object();
        private readonly HashSet<string> _pendingGirlfriendHourlyStateRefresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _girlfriendHourlyStateLoopCts;
        private Task? _girlfriendHourlyStateLoopTask;
        private readonly TimeSpan _girlfriendHourlyStateInterval = ResolveGirlfriendHourlyStateInterval();
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
        public event EventHandler<BackgroundAiRequestStatusEventArgs>? BackgroundAiRequestStatusChanged;
        public event EventHandler<StarGirlfriendHourlyStateUpdatedEventArgs>? GirlfriendHourlyStateUpdated;
        public event EventHandler<Exception>? GirlfriendHourlyStateUpdateFailed;

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
                    MaxThingsLearnedOrSuggestedByPlayer = 256,
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
            _backgroundDispatcher.StatusChanged += (_, args) => BackgroundAiRequestStatusChanged?.Invoke(this, args);

            DailyStarStateOptions dailyOptions = dailyStateOptions ?? new DailyStarStateOptions();
            if (dailyOptions.Enabled && agent is IStarDailyStateGenerator dailyGenerator)
            {
                _dailyStateService = new StarDailyStateService(
                    profileProvider,
                    memoryApi,
                    dailyGenerator,
                    new StarDailyStateFileStore(dailyOptions.StateRoot),
                    dailyOptions,
                    _backgroundDispatcher);
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

        private static TimeSpan ResolveGirlfriendHourlyStateInterval()
        {
            string? value = Environment.GetEnvironmentVariable("SIX_STARS_GIRLFRIEND_STATE_INTERVAL_MINUTES");
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double minutes))
            {
                if (minutes < 5) minutes = 5;
                if (minutes > 1440) minutes = 1440;
                return TimeSpan.FromMinutes(minutes);
            }
            return TimeSpan.FromHours(1);
        }

        public Task<StarState> GetStarStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            return _memoryApi.GetStarStateAsync(sessionId, starId, cancellationToken);
        }

        public async Task<StarDecisionScoreResponse> ScoreDecisionAsync(StarDecisionScoreRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            string starId = string.IsNullOrWhiteSpace(request.StarId) ? "Elusiaa" : request.StarId.Trim();
            request.StarId = starId;
            if (string.IsNullOrWhiteSpace(request.SessionId)) request.SessionId = "local-dev";
            if (string.IsNullOrWhiteSpace(request.CharacterAttributesOrder)) request.CharacterAttributesOrder = StarCharacterAttributeSchema.AttributeOrderText;
            if (string.IsNullOrWhiteSpace(request.RequestedScoreOrder)) request.RequestedScoreOrder = StarCharacterAttributeSchema.DecisionScoreOrderText;

            StarProfile profile = _profileProvider.GetProfile(starId);
            if (string.IsNullOrWhiteSpace(request.CharacterAttributes))
            {
                request.CharacterAttributes = StarCharacterAttributeSchema.NormalizeCompactScores(profile.CharacterAttributes, StarCharacterAttributeSchema.AttributeOrder.Length);
            }

            StarState state = await _memoryApi.GetStarStateAsync(request.SessionId, starId, cancellationToken).ConfigureAwait(false);
            if (_agent is IStarDecisionScorer scorer)
            {
                return await scorer.ScoreDecisionAsync(profile, request, state, cancellationToken).ConfigureAwait(false);
            }

            int scoreCount = request.RequestedScoreOrder.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Length;
            return new StarDecisionScoreResponse
            {
                StarId = profile.StarId,
                SceneKey = request.SceneKey ?? string.Empty,
                ScoreOrder = request.RequestedScoreOrder,
                Scores = StarCharacterAttributeSchema.NormalizeCompactScores(string.Empty, scoreCount <= 0 ? StarCharacterAttributeSchema.DecisionScoreOrder.Length : scoreCount),
                Confidence = 0,
                ReasonCode = "no_decision_scorer",
                ShortExplanation = "The configured Star agent does not implement decision impulse scoring.",
                Source = "fallback"
            };
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
            string normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId;
            _dailyStateService?.Start(normalizedSessionId);
            StartGirlfriendHourlyStateBackgroundUpdates(normalizedSessionId);
        }

        public void StopDailyStateBackgroundUpdates()
        {
            _dailyStateService?.Stop();
            StopGirlfriendHourlyStateBackgroundUpdates();
        }

        public void StartGirlfriendHourlyStateBackgroundUpdates(string sessionId)
        {
            if (!(_agent is IStarGirlfriendHourlyStateGenerator)) return;
            string normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId;
            lock (_girlfriendHourlyStateGate)
            {
                if (_girlfriendHourlyStateLoopTask != null && !_girlfriendHourlyStateLoopTask.IsCompleted) return;
                _girlfriendHourlyStateLoopCts?.Cancel();
                _girlfriendHourlyStateLoopCts?.Dispose();
                _girlfriendHourlyStateLoopCts = new CancellationTokenSource();
                CancellationToken token = _girlfriendHourlyStateLoopCts.Token;
                _girlfriendHourlyStateLoopTask = Task.Run(() => RunGirlfriendHourlyStateLoopAsync(normalizedSessionId, token), CancellationToken.None);
            }
        }

        public void StopGirlfriendHourlyStateBackgroundUpdates()
        {
            lock (_girlfriendHourlyStateGate)
            {
                _girlfriendHourlyStateLoopCts?.Cancel();
                _girlfriendHourlyStateLoopCts?.Dispose();
                _girlfriendHourlyStateLoopCts = null;
                _girlfriendHourlyStateLoopTask = null;
                _pendingGirlfriendHourlyStateRefresh.Clear();
            }
        }

        private async Task RunGirlfriendHourlyStateLoopAsync(string sessionId, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await QueueCurrentGirlfriendHourlyStateRefreshIfDueAsync(sessionId, false, "scheduled hourly girlfriend state refresh", cancellationToken).ConfigureAwait(false);
                    TimeSpan delay = _girlfriendHourlyStateInterval < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : _girlfriendHourlyStateInterval;
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    GirlfriendHourlyStateUpdateFailed?.Invoke(this, ex);
                    try { await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false); } catch { return; }
                }
            }
        }

        private async Task QueueCurrentGirlfriendHourlyStateRefreshIfDueAsync(string sessionId, bool force, string reason, CancellationToken cancellationToken)
        {
            SixStarsSaveData saveData = await _memoryApi.ExportSaveDataAsync(string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId, cancellationToken).ConfigureAwait(false);
            StarState? girlfriend = (saveData.Stars ?? new List<StarState>()).FirstOrDefault(x => x != null && x.IsGirlfriend);
            if (girlfriend == null) return;
            if (!IsGirlfriendHourlyStateDue(girlfriend, force)) return;
            QueueGirlfriendHourlyStateRefresh(sessionId, girlfriend.StarId, reason, force, cancellationToken);
        }

        private static void ResetGirlfriendDurableChunks(StarState state)
        {
            if (state == null) return;
            state.GirlfriendHourlyState = new StarGirlfriendHourlyStateContext();
            state.GirlfriendDesiredStateHistory = new List<GirlfriendVectorHistoryEntry>();
            state.GirlfriendNeedsMetHistory = new List<GirlfriendNeedFulfillmentHistoryEntry>();
            state.GirlfriendDesiredNeedVectorHistory = new List<string>();
            state.GirlfriendDesiredWantVectorHistory = new List<string>();
            state.GirlfriendMetNeedVectorHistory = new List<string>();
            state.GirlfriendMetWantVectorHistory = new List<string>();
            state.GirlfriendNeedsMetPerformanceVector = new List<GirlfriendVectorItem>();
            state.GirlfriendWantsMetPerformanceVector = new List<GirlfriendVectorItem>();
            state.GirlfriendCoupleActivityScores = new List<GirlfriendCoupleActivityScore>();
            state.GirlfriendCoupleActivityHistory = new List<GirlfriendCoupleActivityHistoryEntry>();
            state.GirlfriendCoupleActivityBestExamples = new List<GirlfriendCoupleActivityBestExample>();
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
                        ResetGirlfriendDurableChunks(state);
                        state.GirlfriendHistory.Insert(0, "Girlfriend path cleared because another Star became the exclusive girlfriend.");
                    }
                }
            }
            else
            {
                selected.IsGirlfriend = false;
                selected.GirlfriendSince = null;
                ResetGirlfriendDurableChunks(selected);
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
            if (isGirlfriend)
            {
                QueueGirlfriendHourlyStateRefresh(sessionId, starId, "girlfriend path accepted", true, CancellationToken.None);
            }
            return selected;
        }


        public async Task<StarState> SetGirlfriendCuteModeEnabledAsync(string sessionId, string starId, bool enabled, string reason, CancellationToken cancellationToken)
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

            selected.GirlfriendCuteModeEnabled = enabled;
            selected.RelationshipEvents.Insert(0, "Girlfriend cute mode " + (enabled ? "enabled" : "disabled") + " through host/player settings" + (string.IsNullOrWhiteSpace(reason) ? "." : ": " + reason));
            if (selected.GirlfriendHourlyState != null)
            {
                selected.GirlfriendHourlyState.CuteModeSignature = GetGirlfriendCuteModeSignature(starId);
                if (!enabled)
                {
                    selected.GirlfriendHourlyState.CuteModeActive = false;
                    selected.GirlfriendHourlyState.CuteModeProbability = 0;
                    selected.GirlfriendHourlyState.CuteModeReason = "cute mode disabled by player settings";
                }
                else if (string.IsNullOrWhiteSpace(selected.GirlfriendHourlyState.CuteModeReason))
                {
                    selected.GirlfriendHourlyState.CuteModeReason = "cute mode enabled by player settings; next girlfriend hourly refresh may activate it deterministically";
                }
            }
            if (selected.RelationshipEvents.Count > 120) selected.RelationshipEvents.RemoveRange(120, selected.RelationshipEvents.Count - 120);

            await _memoryApi.ImportSaveDataAsync(saveData, cancellationToken).ConfigureAwait(false);
            return selected;
        }

        public async Task<StarState> ActivateGirlfriendCuteModeNowAsync(string sessionId, string starId, string reason, CancellationToken cancellationToken)
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
            if (!selected.IsGirlfriend)
            {
                throw new InvalidOperationException(starId + " is not currently the girlfriend Star.");
            }

            selected.GirlfriendCuteModeEnabled = true;
            if (selected.GirlfriendHourlyState == null)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                selected.GirlfriendHourlyState = BuildGirlfriendHourlyStateContext(sessionId, starId, now, new StarGirlfriendHourlyStateModelResponse
                {
                    StarId = starId,
                    Summary = "Cute mode was activated by the player before the next girlfriend hourly state refresh.",
                    RelationshipPosture = "girlfriend cute mode activated immediately",
                    NeedSummary = string.Empty,
                    WantSummary = string.Empty,
                    OverallConfidence = 1.0,
                    OverallScore = Math.Max(0.0, Math.Min(100.0, selected.EmotionalConnectionPercent))
                });
            }
            selected.GirlfriendHourlyState.CuteModeActive = true;
            selected.GirlfriendHourlyState.CuteModeProbability = 1.0;
            selected.GirlfriendHourlyState.CuteModeSignature = GetGirlfriendCuteModeSignature(starId);
            selected.GirlfriendHourlyState.CuteModeReason = string.IsNullOrWhiteSpace(reason)
                ? "player requested cute mode now"
                : reason;
            selected.RelationshipEvents.Insert(0, "Girlfriend cute mode activated immediately through host/player action.");
            if (selected.RelationshipEvents.Count > 120) selected.RelationshipEvents.RemoveRange(120, selected.RelationshipEvents.Count - 120);

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
            // Pending durable analyses must survive. Do not abort an LLM HTTP request already in
            // flight: several local servers continue generating after a disconnected client, which
            // can leave the next foreground request waiting behind invisible abandoned work. Queued
            // background work is already held while a foreground lease is active.
            lock (_backgroundGate)
            {
                try
                {
                    _backgroundStoryGenerationCts?.Cancel();
                    _backgroundStoryGenerationCts?.Dispose();
                }
                catch
                {
                }
                _backgroundStoryGenerationCts = new CancellationTokenSource();
            }
            System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Active background work preempted without discarding pending state analysis: " + reason);
        }

        public async Task<StarInteractionResponse> ProcessWhenForegroundIdleAsync(
            StarInteractionRequest request,
            string description,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            TaskCompletionSource<StarInteractionResponse> completion = new TaskCompletionSource<StarInteractionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = cancellationToken.Register(() => completion.TrySetCanceled());

            _backgroundDispatcher.Enqueue(
                "PreparedListen",
                string.IsNullOrWhiteSpace(description) ? "Prepare an at-rest Listen response while foreground conversation is idle." : description,
                BackgroundAiRequestPriority.High,
                async dispatcherToken =>
                {
                    try
                    {
                        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, dispatcherToken);
                        StarInteractionResponse response = await ProcessAsync(request, linked.Token).ConfigureAwait(false);
                        completion.TrySetResult(response);
                    }
                    catch (OperationCanceledException)
                    {
                        completion.TrySetCanceled();
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                },
                cancellationToken);

            return await completion.Task.ConfigureAwait(false);
        }

        public async Task<StarInteractionResponse> ProcessAsync(StarInteractionRequest request, CancellationToken cancellationToken)
        {
            bool isStarInitiativeRequest = request != null && request.RequestType == RequestType.StarInitiative;
            bool isManualStarInitiativeRequest = isStarInitiativeRequest
                && IsTruthy(GetGameContextFlag(request.GameContext, "starInitiativeForce", "false"));
            bool isBackgroundWorkerRequest = (isStarInitiativeRequest && !isManualStarInitiativeRequest)
                || (request != null && (IsStoryGenerationWorkerRequest(request) || IsGirlfriendProposalWorkerRequest(request) || IsInterstarMicroRequest(request)));
            IDisposable? foregroundLease = request != null && request.RequestType != RequestType.Debug && !isBackgroundWorkerRequest
                ? _backgroundDispatcher.EnterForegroundRequest("Foreground reply request for " + request.StarId + " (" + request.RequestType + ")")
                : null;
            try
            {
            if (!isStarInitiativeRequest)
            {
                CancelPendingInterstarCycle("New Star conversation request started for " + request.StarId + ".");
                NotifyInterstarStatus(request.SessionId, request.StarId, string.Empty, "ConversationStarted", "Conversation with " + request.StarId + " started; any pending interstar ripple timer was cancelled.", 0, 0);
            }
            else
            {
                NotifyInterstarStatus(request.SessionId, request.StarId, string.Empty, "StarInitiativeCheckStarted", "Checking state to see whether " + request.StarId + " has something to say at rest. This does not affect relationship/emotion state.", 0, 0);
            }

            if (_screenContextStore != null && request.RecentScreenCaptures.Count == 0)
            {
                request.RecentScreenCaptures.AddRange(await _screenContextStore.GetRecentAsync(request.SessionId, _recentScreenCount, cancellationToken).ConfigureAwait(false));
            }

            StarProfile profile = _profileProvider.GetProfile(request.StarId);
            await PopulateKnownStarContextAsync(request, cancellationToken).ConfigureAwait(false);
            StarMemorySnapshot snapshot = await _memoryApi.GetStarMemorySnapshotAsync(request.SessionId, request.StarId, _recentHistoryCount, cancellationToken).ConfigureAwait(false);
            StarState state = snapshot.State;
            await RefreshOtherStarsViewAsync(request.SessionId, state, cancellationToken).ConfigureAwait(false);
            await AttachDailyStateContextForRequestAsync(request, state, cancellationToken).ConfigureAwait(false);

            if (isStarInitiativeRequest)
            {
                return await ProcessStarInitiativeCheckAsync(profile, request, state, cancellationToken).ConfigureAwait(false);
            }

            GirlfriendProposalDecision? girlfriendProposal = await TryPrepareGirlfriendProposalAsync(profile, request, state, cancellationToken).ConfigureAwait(false);
            if (girlfriendProposal != null && !IsGirlfriendProposalWorkerRequest(request))
            {
                StarInteractionResponse thinkingResponse = CreateGirlfriendProposalThinkingResponse(profile, request, girlfriendProposal);
                ApplyVoiceDefaults(profile, thinkingResponse);
                QueueGirlfriendProposalDecisionAfterThinkingResponse(profile, request, state, girlfriendProposal, cancellationToken);
                return thinkingResponse;
            }

            PrepareExplicitStoryRequest(request, state);

            if (request.RequestType == RequestType.Story && !IsStoryGenerationWorkerRequest(request))
            {
                StarInteractionResponse thinkingResponse = CreateStoryThinkingResponse(profile, request);
                ApplyVoiceDefaults(profile, thinkingResponse);
                QueueStoryGenerationAfterThinkingResponse(profile, request, state, cancellationToken);
                return thinkingResponse;
            }

            StarInteractionResponse response = await _agent.GenerateAsync(profile, request, state, cancellationToken).ConfigureAwait(false);
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

            if (request.RequestType == RequestType.StarInitiative)
            {
                response.StateDeltas = RelationshipDelta.Zero();
                response.SuggestedStateDeltas = RelationshipDelta.Zero();
                response.EmotionalConnectionPercentBefore = state.EmotionalConnectionPercent;
                response.EmotionalConnectionPercentAfter = state.EmotionalConnectionPercent;
                response.IsGirlfriendReadyAfter = state.IsGirlfriendReady;
                response.GirlfriendReadinessSummary = state.GirlfriendReadinessSummary ?? string.Empty;
                response.Debug.Notes.Add("StarInitiative request finished without background emotion/memory review, relationship deltas, or interstar ripple.");
                return response;
            }

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
            finally
            {
                foregroundLease?.Dispose();
            }
        }


        private async Task<StarInteractionResponse> ProcessStarInitiativeCheckAsync(StarProfile profile, StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
        {
            StarInitiativeStateGate gate = EvaluateStarInitiativeStateGate(request, state);
            request.GameContext.Flags["starInitiativeStateGateScore"] = gate.Score.ToString("0.00");
            request.GameContext.Flags["starInitiativeStateGateReason"] = gate.Reason ?? string.Empty;
            if (gate.Topics.Count > 0) request.GameContext.Flags["starInitiativeStateTopics"] = string.Join(" | ", gate.Topics.Take(8));

            if (!gate.ShouldAskModel)
            {
                StarInteractionResponse skipped = CreateSkippedStarInitiativeResponse(profile, request, state, gate);
                ApplyVoiceDefaults(profile, skipped);
                NotifyInterstarStatus(request.SessionId, request.StarId, string.Empty, "StarInitiativeSkipped", skipped.InitiativeReason, 0, 0);
                return skipped;
            }

            StarInteractionResponse response = await _agent.GenerateAsync(profile, request, state, cancellationToken).ConfigureAwait(false);
            ApplyVoiceDefaults(profile, response);
            response.StateDeltas = RelationshipDelta.Zero();
            response.SuggestedStateDeltas = RelationshipDelta.Zero();
            response.EmotionalConnectionPercentBefore = state.EmotionalConnectionPercent;
            response.EmotionalConnectionPercentAfter = state.EmotionalConnectionPercent;
            response.IsGirlfriendReadyAfter = state.IsGirlfriendReady;
            response.GirlfriendReadinessSummary = state.GirlfriendReadinessSummary ?? string.Empty;
            response.InitiativeSkippedBeforeLlm = false;
            response.InitiativeStateScore = gate.Score;
            if (string.IsNullOrWhiteSpace(response.InitiativeTopic) && gate.Topics.Count > 0) response.InitiativeTopic = gate.Topics[0];
            if (string.IsNullOrWhiteSpace(response.InitiativeReason)) response.InitiativeReason = gate.Reason;
            response.EmotionalEffectVector = string.Empty;
            response.NegativeEmotionVector = string.Empty;
            ApplyStarInitiativeSpeechScoreGate(request, state, response);
            response.TopAffectedEmotions.Clear();
            response.AcceptedPositiveEmotionSignals.Clear();
            response.AcceptedNegativeEmotionSignals.Clear();
            response.AcceptedRelationshipCombos.Clear();
            response.DeterministicEmotionUpdateSummary = "Star initiative check: no deterministic emotion scoring or relationship delta applied.";

            if (!response.InitiativeShouldSpeak || string.IsNullOrWhiteSpace(response.SpokenText))
            {
                response.InitiativeShouldSpeak = false;
                response.SetSpokenParts(Array.Empty<string>());
                response.SpokenText = string.Empty;
                NotifyInterstarStatus(request.SessionId, request.StarId, string.Empty, "StarInitiativeNoSpeech", response.InitiativeReason, 0, 0);
            }
            else
            {
                NotifyInterstarStatus(request.SessionId, request.StarId, string.Empty, "StarInitiativeSpeechReady", profile.DisplayName + " has an at-rest line to say: " + response.InitiativeTopic, 0, 0);
            }

            response.Debug.Notes.Add("StarInitiative state gate allowed LLM check: score=" + gate.Score.ToString("0.00") + "; " + gate.Reason);
            response.Debug.Notes.Add("No StarMemoryApi update, emotion vector scoring, relationship update, or interstar ripple is run for RequestType=StarInitiative.");
            return response;
        }

        private static void ApplyStarInitiativeSpeechScoreGate(StarInteractionRequest request, StarState state, StarInteractionResponse response)
        {
            if (request == null || response == null || request.RequestType != RequestType.StarInitiative) return;
            bool hasText = response.SpokenParts != null && response.SpokenParts.Any(x => !string.IsNullOrWhiteSpace(x));
            GirlfriendVectorItem? decision = (response.InitiativeSpeechDecisionVector ?? new List<GirlfriendVectorItem>())
                .Where(x => x != null)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Confidence)
                .FirstOrDefault();
            double score = Math.Max(0, Math.Min(100, decision?.Score ?? (response.InitiativePriority * 100.0)));
            double confidence = Math.Max(0, Math.Min(1, decision?.Confidence ?? response.InitiativePriority));

            double randomThreshold;
            lock (StoryRandomGate)
            {
                // Zero-based threshold: some checks accept very low scores, especially when girlfriend mode
                // produced any confidence at all. This keeps at-rest speech organic instead of fixed-timer robotic.
                randomThreshold = (state != null && state.IsGirlfriend ? 5.0 : 10.0) + (StoryRandom.NextDouble() * (state != null && state.IsGirlfriend ? 30.0 : 45.0));
            }

            double effectiveScore = score + (confidence * (state != null && state.IsGirlfriend ? 35.0 : 25.0));
            bool force = IsTruthy(GetContextFlag(request, "starInitiativeForce"))
                || IsTruthy(GetContextFlag(request, "starInitiativePrepareForListen"));
            bool vectorWantsSpeech = score > 0 || confidence > 0;
            bool modelWantsSpeech = response.InitiativeShouldSpeak || vectorWantsSpeech;
            bool accepted = force || (hasText && modelWantsSpeech && (effectiveScore >= randomThreshold || (state != null && state.IsGirlfriend && confidence > 0 && score > 0)));

            response.InitiativeSpeechAcceptThreshold = randomThreshold;
            response.InitiativeSpeechEffectiveScore = effectiveScore;
            response.InitiativeSpeechScoreAccepted = accepted;
            if (!accepted)
            {
                response.InitiativeShouldSpeak = false;
                response.InitiativeReason = (response.InitiativeReason ?? string.Empty) + " Runtime score/confidence gate suppressed this at-rest line: score=" + score.ToString("0.0") + ", confidence=" + confidence.ToString("0.00") + ", effective=" + effectiveScore.ToString("0.0") + ", threshold=" + randomThreshold.ToString("0.0") + ".";
                response.SetSpokenParts(Array.Empty<string>());
                response.InitiativePriority = 0;
            }
        }

        private static StarInteractionResponse CreateSkippedStarInitiativeResponse(StarProfile profile, StarInteractionRequest request, StarState state, StarInitiativeStateGate gate)
        {
            StarInteractionResponse response = new StarInteractionResponse
            {
                StarId = profile.StarId,
                InitiativeShouldSpeak = false,
                InitiativeSkippedBeforeLlm = true,
                InitiativeStateScore = gate.Score,
                InitiativePriority = 0,
                InitiativeTopic = string.Empty,
                InitiativeReason = gate.Reason,
                EmotionalPurpose = "At-rest Star initiative skipped before LLM because state did not contain enough reason to interrupt the Last Cat.",
                EmotionalConnectionPercentBefore = state.EmotionalConnectionPercent,
                EmotionalConnectionPercentAfter = state.EmotionalConnectionPercent,
                IsGirlfriendReadyAfter = state.IsGirlfriendReady,
                GirlfriendReadinessSummary = state.GirlfriendReadinessSummary ?? string.Empty,
                SuggestedStateDeltas = RelationshipDelta.Zero(),
                StateDeltas = RelationshipDelta.Zero(),
                DeterministicEmotionUpdateSummary = "Star initiative skipped before LLM; no deterministic emotion scoring or relationship delta applied.",
                ConversationSummary = state.ConversationSummary ?? string.Empty,
                ChangesSummary = profile.DisplayName + " did not have a state-backed at-rest line to say right now.",
                Debug = new DebugInfo { DetectedIntent = RequestType.StarInitiative.ToString(), WisdomScore = 0 }
            };
            response.SetSpokenParts(Array.Empty<string>());
            response.Debug.Notes.Add("StarInitiative skipped before LLM: score=" + gate.Score.ToString("0.00") + "; " + gate.Reason);
            return response;
        }

        private sealed class StarInitiativeStateGate
        {
            public bool ShouldAskModel { get; set; }
            public double Score { get; set; }
            public string Reason { get; set; } = string.Empty;
            public List<string> Topics { get; set; } = new List<string>();
        }

        private static StarInitiativeStateGate EvaluateStarInitiativeStateGate(StarInteractionRequest request, StarState state)
        {
            List<string> topics = ExtractInitiativeTopics(request, state).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
            string summary = FirstNonEmpty(GetContextFlag(request, "todayStarStateSummary"), state.TodayState?.Summary);
            bool force = IsTruthy(GetContextFlag(request, "starInitiativeForce"));
            bool prepareForListen = IsTruthy(GetContextFlag(request, "starInitiativePrepareForListen"));
            bool reconnect = ContainsIgnoreCase(GetContextFlag(request, "initiativeReason"), "reconnect") || ContainsIgnoreCase(request.PlayerText, "reconnect");
            bool hasRecentEmotion = state.LastTopAffectedEmotions != null && state.LastTopAffectedEmotions.Any();
            bool hasRecentConversation = state.MemoryLog != null && state.MemoryLog.Any(x => x != null && x.CreatedAt > DateTimeOffset.UtcNow.AddHours(-4));

            double score = 0;
            List<string> reasons = new List<string>();
            if (topics.Count > 0) { score += 0.45; reasons.Add("daily wants-to-talk topics=" + topics.Count); }
            if (!string.IsNullOrWhiteSpace(summary)) { score += 0.10; reasons.Add("daily summary available"); }
            if (hasRecentEmotion) { score += 0.20; reasons.Add("recent settled emotion vector exists"); }
            if (hasRecentConversation) { score += 0.08; reasons.Add("recent conversation exists"); }
            if (state.IsGirlfriend) { score += 0.15; reasons.Add("girlfriend closeness"); }
            else if (state.EmotionalConnectionPercent >= 25) { score += 0.12; reasons.Add("high connection"); }
            else if (state.IsAlly) { score += 0.05; reasons.Add("ally relationship"); }
            if (reconnect) { score += 0.05; reasons.Add("phone reconnect/rest nudge"); }
            if (force) { score += 0.10; reasons.Add("manual/forced check"); }
            if (prepareForListen) { score += 0.10; reasons.Add("prepared Listen cache check"); }

            bool shouldAsk = force || prepareForListen || score >= 0.28;
            if (!shouldAsk && topics.Count == 0)
            {
                reasons.Add("no daily wants-to-talk topics were present in state");
            }

            return new StarInitiativeStateGate
            {
                ShouldAskModel = shouldAsk,
                Score = Math.Max(0, Math.Min(1, score)),
                Topics = topics,
                Reason = (shouldAsk ? "State check allowed Star initiative LLM: " : "State check skipped Star initiative LLM: ") + (reasons.Count == 0 ? "no state-backed reason detected" : string.Join("; ", reasons))
            };
        }

        private static List<string> ExtractInitiativeTopics(StarInteractionRequest request, StarState state)
        {
            List<string> topics = new List<string>();
            string json = FirstNonEmpty(GetContextFlag(request, "todayStarStateJson"), state.TodayState?.Json, state.TodayState?.Xml);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(json);
                    AddStringArrayProperty(doc.RootElement, topics, "wantsToTalkAbout");
                    AddStringArrayProperty(doc.RootElement, topics, "thingsSheWantsToTalkAbout");
                    if (doc.RootElement.TryGetProperty("today", out JsonElement today))
                    {
                        AddStringArrayProperty(today, topics, "wantsToTalkAbout");
                        AddStringArrayProperty(today, topics, "thingsSheWantsToTalkAbout");
                        AddStringArrayProperty(today, topics, "conversationHooks");
                    }
                    AddStringArrayProperty(doc.RootElement, topics, "conversationHooks");
                }
                catch
                {
                    foreach (Match match in Regex.Matches(json, "\\\"(?:wantsToTalkAbout|thingsSheWantsToTalkAbout|conversationHooks)\\\"\\s*:\\s*\\[(?<items>.*?)\\]", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                    {
                        foreach (Match item in Regex.Matches(match.Groups["items"].Value, "\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\""))
                        {
                            string value = Regex.Unescape(item.Groups["value"].Value).Trim();
                            if (!string.IsNullOrWhiteSpace(value)) topics.Add(value);
                        }
                    }
                }
            }

            string girlfriendJson = FirstNonEmpty(GetContextFlag(request, "girlfriendHourlyStateJson"), state.GirlfriendHourlyState?.Json);
            if (!string.IsNullOrWhiteSpace(girlfriendJson))
            {
                AddGirlfriendHourlyTopicsFromJson(girlfriendJson, topics);
            }
            if (state.GirlfriendHourlyState != null && state.GirlfriendHourlyState.IsAvailable)
            {
                topics.AddRange((state.GirlfriendHourlyState.ThingsSheWantsToSay ?? new List<string>()).Take(8));
                topics.AddRange((state.GirlfriendHourlyState.CuteThingsSheWantsToSay ?? new List<string>()).Take(8));
                AddGirlfriendVectorTopics(state.GirlfriendHourlyState.NeedVector, topics, "girlfriend need");
                AddGirlfriendVectorTopics(state.GirlfriendHourlyState.WantVector, topics, "girlfriend want");
            }

            return topics;
        }

        private static void AddGirlfriendHourlyTopicsFromJson(string json, List<string> topics)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                AddStringArrayProperty(doc.RootElement, topics, "thingsSheWantsToSay");
                AddStringArrayProperty(doc.RootElement, topics, "cuteThingsSheWantsToSay");
                AddStringArrayProperty(doc.RootElement, topics, "conversationHooks");
                AddVectorTopicsFromJson(doc.RootElement, topics, "needVector", "girlfriend need");
                AddVectorTopicsFromJson(doc.RootElement, topics, "wantVector", "girlfriend want");
            }
            catch
            {
                foreach (Match match in Regex.Matches(json, "\\\"(?:thingsSheWantsToSay|cuteThingsSheWantsToSay|conversationHooks)\\\"\\s*:\\s*\\[(?<items>.*?)\\]", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    foreach (Match item in Regex.Matches(match.Groups["items"].Value, "\"(?<value>(?:\\.|[^\"])*)\""))
                    {
                        string value = Regex.Unescape(item.Groups["value"].Value).Trim();
                        if (!string.IsNullOrWhiteSpace(value)) topics.Add(value);
                    }
                }
            }
        }

        private static void AddVectorTopicsFromJson(JsonElement root, List<string> topics, string propertyName, string prefix)
        {
            if (root.ValueKind != JsonValueKind.Object) return;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string name = GetJsonString(item, "displayName");
                    if (string.IsNullOrWhiteSpace(name)) name = GetJsonString(item, "itemId");
                    double score = GetJsonDouble(item, "score");
                    if (!string.IsNullOrWhiteSpace(name) && score >= 50) topics.Add(prefix + ": " + name);
                }
            }
        }

        private static void AddGirlfriendVectorTopics(IEnumerable<GirlfriendVectorItem>? vector, List<string> topics, string prefix)
        {
            foreach (GirlfriendVectorItem item in (vector ?? Enumerable.Empty<GirlfriendVectorItem>()).Where(x => x != null).OrderByDescending(x => x.Score).Take(6))
            {
                if (item.Score < 50) continue;
                string name = string.IsNullOrWhiteSpace(item.DisplayName) ? item.ItemId : item.DisplayName;
                if (!string.IsNullOrWhiteSpace(name)) topics.Add(prefix + ": " + name);
            }
        }

        private static string GetJsonString(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object) return string.Empty;
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString() ?? string.Empty;
                }
            }
            return string.Empty;
        }

        private static double GetJsonDouble(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object) return 0;
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out double value)) return value;
            }
            return 0;
        }

        private static void AddStringArrayProperty(JsonElement element, List<string> output, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object) return;
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string? value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) output.Add(value.Trim());
                    }
                }
            }
        }

        private static string GetContextFlag(StarInteractionRequest request, string key)
        {
            if (request != null && request.GameContext != null && request.GameContext.Flags != null && request.GameContext.Flags.TryGetValue(key, out string? value)) return value ?? string.Empty;
            return string.Empty;
        }

        private static bool IsTruthy(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsIgnoreCase(string? value, string fragment)
        {
            return !string.IsNullOrWhiteSpace(value) && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task AttachDailyStateContextForRequestAsync(StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
        {
            if (request == null || request.GameContext == null) return;
            AttachGirlfriendHourlyStateContextForRequest(request, state);
            if (state != null && state.IsGirlfriend && request.RequestType != RequestType.StarInitiative)
            {
                QueueGirlfriendHourlyStateRefresh(request.SessionId, request.StarId, "request prompt girlfriend-state check", false, CancellationToken.None);
            }
            StarDailyStateRecord? record = null;
            if (_dailyStateService != null)
            {
                try
                {
                    // Initiative is a latency-sensitive yes/no/one-line check. It may consume the
                    // newest cache, but it must not launch a daily-state LLM generation that can
                    // contend with the initiative request itself. Normal foreground turns queue the
                    // refresh behind their foreground dispatcher lease.
                    record = request.RequestType == RequestType.StarInitiative
                        ? await _dailyStateService.ReadCurrentAsync(request.SessionId, request.StarId, cancellationToken).ConfigureAwait(false)
                        : await _dailyStateService.ReadCurrentAndQueueRefreshAsync(
                            request.SessionId,
                            request.StarId,
                            state,
                            "foreground request found daily state missing or stale",
                            cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    request.GameContext.RecentEvents.Add("Daily Star state cache read/refresh queue failed, so the foreground reply continued without waiting: " + ex.Message);
                    DailyStateUpdateFailed?.Invoke(this, ex);
                }
            }

            // The durable state already carries the latest usable daily context. Prefer it before
            // searching other sessions, and never synchronously regenerate daily state on the
            // foreground path. A missing/stale refresh is queued behind the player reply above.
            if (record == null && state.TodayState != null && state.TodayState.IsAvailable)
            {
                request.GameContext.Flags["todayStarStateJson"] = state.TodayState.Json ?? state.TodayState.Xml ?? string.Empty;
                request.GameContext.Flags["todayStarStateXml"] = string.Empty;
                request.GameContext.Flags["todayStarStateSummary"] = state.TodayState.Summary ?? string.Empty;
                request.GameContext.Flags["todayStarStateGeneratedAt"] = state.TodayState.GeneratedAt?.ToString("o") ?? string.Empty;
                request.GameContext.Flags["todayStarStateSlotId"] = state.TodayState.SlotId ?? string.Empty;
                request.GameContext.Flags["todayStarStateLocalDate"] = state.TodayState.LocalDate ?? string.Empty;
                request.GameContext.Flags["todayStarStateFilePath"] = state.TodayState.FilePath ?? string.Empty;
                request.GameContext.Flags["todayStarStateMayBeStale"] = "true";
                request.GameContext.RecentEvents.Add("Attached the last cached daily Star state immediately; any refresh is running behind the foreground reply.");
                return;
            }

            if (record == null)
            {
                StarDailyStateRecord? fallbackRecord = await TryReadDailyStateFallbackAsync(request.SessionId, request.StarId, state, cancellationToken).ConfigureAwait(false);
                if (fallbackRecord != null)
                {
                    record = fallbackRecord;
                    request.GameContext.RecentEvents.Add("No daily Star state existed yet for session " + request.SessionId + "; using cached daily Star state from session " + fallbackRecord.SessionId + " while a local refresh is queued.");
                }
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

        private static void AttachGirlfriendHourlyStateContextForRequest(StarInteractionRequest request, StarState state)
        {
            if (request == null || request.GameContext == null || state == null) return;
            StarGirlfriendHourlyStateContext? context = state.GirlfriendHourlyState;
            if (context == null || !context.IsAvailable) return;

            string json = context.Json ?? string.Empty;
            if (json.Length > 12000) json = json.Substring(0, 12000) + "\n/* girlfriend-hourly-state truncated for prompt */";
            request.GameContext.Flags["girlfriendHourlyStateJson"] = json;
            request.GameContext.Flags["girlfriendHourlyStateSummary"] = context.Summary ?? string.Empty;
            request.GameContext.Flags["girlfriendHourlyStateGeneratedAt"] = context.GeneratedAt?.ToString("o") ?? string.Empty;
            request.GameContext.Flags["girlfriendHourlyStateSlotId"] = context.SlotId ?? string.Empty;
            request.GameContext.RecentEvents.Add("Current girlfriend hourly needs/wants state is attached for " + request.StarId + ".");
        }

        private bool IsGirlfriendHourlyStateDue(StarState state, bool force)
        {
            if (state == null || !state.IsGirlfriend) return false;
            if (force) return true;
            StarGirlfriendHourlyStateContext? context = state.GirlfriendHourlyState;
            if (context == null || !context.IsAvailable || !context.GeneratedAt.HasValue) return true;
            return DateTimeOffset.UtcNow - context.GeneratedAt.Value >= _girlfriendHourlyStateInterval;
        }

        private void QueueGirlfriendHourlyStateRefresh(string sessionId, string starId, string reason, bool force, CancellationToken cancellationToken)
        {
            if (!(_agent is IStarGirlfriendHourlyStateGenerator generator)) return;
            string normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId;
            string normalizedStarId = string.IsNullOrWhiteSpace(starId) ? "Elusiaa" : starId;
            string key = normalizedSessionId + "|" + normalizedStarId;

            lock (_girlfriendHourlyStateGate)
            {
                if (!force && _pendingGirlfriendHourlyStateRefresh.Contains(key)) return;
                _pendingGirlfriendHourlyStateRefresh.Add(key);
            }

            _backgroundDispatcher.Enqueue(
                "GirlfriendHourlyState",
                "Refresh girlfriend hourly need/want vectors for " + normalizedStarId + " (" + reason + ")",
                BackgroundAiRequestPriority.Low,
                async workToken =>
                {
                    try
                    {
                        StarState latest = await _memoryApi.GetStarStateAsync(normalizedSessionId, normalizedStarId, workToken).ConfigureAwait(false);
                        if (!latest.IsGirlfriend) return;
                        if (!IsGirlfriendHourlyStateDue(latest, force)) return;

                        StarProfile profile = _profileProvider.GetProfile(normalizedStarId);
                        DateTimeOffset now = DateTimeOffset.UtcNow;
                        StarGirlfriendHourlyStateModelResponse model = await generator.GenerateGirlfriendHourlyStateAsync(new StarGirlfriendHourlyStateGenerationRequest
                        {
                            SessionId = normalizedSessionId,
                            Profile = profile,
                            State = latest,
                            SlotId = now.ToString("yyyyMMddHH"),
                            LocalNow = now,
                            Reason = reason ?? string.Empty,
                            PreviousState = latest.GirlfriendHourlyState
                        }, workToken).ConfigureAwait(false);

                        StarGirlfriendHourlyStateContext context = BuildGirlfriendHourlyStateContext(normalizedSessionId, normalizedStarId, now, model);
                        ApplyGirlfriendCuteModeDecision(context, latest, profile);
                        await _memoryApi.SetStarGirlfriendHourlyStateContextAsync(normalizedSessionId, normalizedStarId, context, workToken).ConfigureAwait(false);
                        GirlfriendHourlyStateUpdated?.Invoke(this, new StarGirlfriendHourlyStateUpdatedEventArgs
                        {
                            SessionId = normalizedSessionId,
                            StarId = normalizedStarId,
                            Reason = reason ?? string.Empty,
                            State = context
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        GirlfriendHourlyStateUpdateFailed?.Invoke(this, ex);
                        throw;
                    }
                    finally
                    {
                        lock (_girlfriendHourlyStateGate)
                        {
                            _pendingGirlfriendHourlyStateRefresh.Remove(key);
                        }
                    }
                },
                cancellationToken);
        }


        private static double ResolveGirlfriendCuteModeBaseProbability()
        {
            string? value = Environment.GetEnvironmentVariable("SIX_STARS_GIRLFRIEND_CUTE_MODE_PROBABILITY");
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double probability))
            {
                if (probability > 1) probability /= 100.0;
                return Math.Max(0.0, Math.Min(1.0, probability));
            }
            return 0.35;
        }

        private static void ApplyGirlfriendCuteModeDecision(StarGirlfriendHourlyStateContext context, StarState state, StarProfile profile)
        {
            if (context == null) return;
            if (state != null && !state.GirlfriendCuteModeEnabled)
            {
                context.CuteModeProbability = 0;
                context.CuteModeActive = false;
                context.CuteModeReason = "cute mode disabled by player settings";
                context.CuteModeSignature = GetGirlfriendCuteModeSignature(profile?.StarId ?? context.StarId);
                return;
            }
            double probability = ResolveGirlfriendCuteModeBaseProbability();
            double topNeedWant = 0;
            if (context.NeedVector != null && context.NeedVector.Count > 0) topNeedWant = Math.Max(topNeedWant, context.NeedVector.Max(x => x?.Score ?? 0));
            if (context.WantVector != null && context.WantVector.Count > 0) topNeedWant = Math.Max(topNeedWant, context.WantVector.Max(x => x?.Score ?? 0));
            if (topNeedWant >= 70 || context.OverallScore >= 70) probability += 0.12;
            else if (topNeedWant >= 45 || context.OverallScore >= 45) probability += 0.06;
            if ((state?.GirlfriendCoupleActivityScores?.Count ?? 0) > 0) probability += 0.04;
            probability = Math.Max(0.05, Math.Min(0.70, probability));

            bool active;
            lock (StoryRandomGate)
            {
                active = StoryRandom.NextDouble() <= probability;
            }

            context.CuteModeProbability = probability;
            context.CuteModeActive = active;
            context.CuteModeReason = active
                ? "deterministic girlfriend cute-mode gate accepted; base probability is 35% with small boosts for unmet/high needs, wants, and existing couple activity history"
                : "deterministic girlfriend cute-mode gate did not activate this refresh";
            context.CuteModeSignature = GetGirlfriendCuteModeSignature(profile?.StarId ?? context.StarId);
        }

        private static string GetGirlfriendCuteModeSignature(string starId)
        {
            if (string.Equals(starId, "Elusiaa", StringComparison.OrdinalIgnoreCase)) return "Light names + halo boops + tender blessings.";
            if (string.Equals(starId, "Kaelia", StringComparison.OrdinalIgnoreCase)) return "Quiet whispers + void-cloak privacy + reluctant softness.";
            if (string.Equals(starId, "Khavia", StringComparison.OrdinalIgnoreCase)) return "Ocean sounds + explained touch + flowing pet names.";
            if (string.Equals(starId, "Maera", StringComparison.OrdinalIgnoreCase)) return "Sparse mine + heartbeat/red-thread affection + learned tenderness.";
            if (string.Equals(starId, "Auerlis", StringComparison.OrdinalIgnoreCase)) return "Spark teasing + dramatic hugs + tiny fireworks.";
            if (string.Equals(starId, "Nyxari", StringComparison.OrdinalIgnoreCase)) return "Shadow vanish-kisses + poetic names + fragment compliments.";
            return "Star-specific cute affection while preserving personality.";
        }

        private static StarGirlfriendHourlyStateContext BuildGirlfriendHourlyStateContext(string sessionId, string starId, DateTimeOffset generatedAt, StarGirlfriendHourlyStateModelResponse model)
        {
            model ??= new StarGirlfriendHourlyStateModelResponse { StarId = starId };
            string json = JsonSerializer.Serialize(new
            {
                starId = string.IsNullOrWhiteSpace(model.StarId) ? starId : model.StarId,
                generatedAt,
                summary = model.Summary ?? string.Empty,
                relationshipPosture = model.RelationshipPosture ?? string.Empty,
                needSummary = model.NeedSummary ?? string.Empty,
                wantSummary = model.WantSummary ?? string.Empty,
                needVector = model.NeedVector ?? new List<GirlfriendVectorItem>(),
                wantVector = model.WantVector ?? new List<GirlfriendVectorItem>(),
                thingsSheWantsToSay = model.ThingsSheWantsToSay ?? new List<string>(),
                cuteThingsSheWantsToSay = model.CuteThingsSheWantsToSay ?? new List<string>(),
                overallConfidence = model.OverallConfidence,
                overallScore = model.OverallScore,
                cuteModeActive = false,
                cuteModeProbability = 0.35,
                cuteModeReason = string.Empty,
                cuteModeSignature = GetGirlfriendCuteModeSignature(starId)
            }, new JsonSerializerOptions { WriteIndented = true });

            return new StarGirlfriendHourlyStateContext
            {
                IsAvailable = true,
                SessionId = sessionId ?? string.Empty,
                StarId = string.IsNullOrWhiteSpace(model.StarId) ? starId : model.StarId,
                SlotId = generatedAt.ToString("yyyyMMddHH"),
                GeneratedAt = generatedAt,
                Summary = model.Summary ?? string.Empty,
                RelationshipPosture = model.RelationshipPosture ?? string.Empty,
                NeedSummary = model.NeedSummary ?? string.Empty,
                WantSummary = model.WantSummary ?? string.Empty,
                OverallConfidence = model.OverallConfidence,
                OverallScore = model.OverallScore,
                CuteModeActive = false,
                CuteModeProbability = 0.35,
                CuteModeReason = string.Empty,
                CuteModeSignature = GetGirlfriendCuteModeSignature(starId),
                Json = json,
                NeedVector = model.NeedVector ?? new List<GirlfriendVectorItem>(),
                WantVector = model.WantVector ?? new List<GirlfriendVectorItem>(),
                ThingsSheWantsToSay = model.ThingsSheWantsToSay ?? new List<string>(),
                CuteThingsSheWantsToSay = model.CuteThingsSheWantsToSay ?? new List<string>()
            };
        }

        private async Task<StarInteractionResponse> TryResolveInformationNeedAsync(StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse response, CancellationToken cancellationToken)
        {
            if (request == null || response == null) return response;
            if (request.GameContext.Flags.TryGetValue("informationFollowup", out string? already) && string.Equals(already, "true", StringComparison.OrdinalIgnoreCase))
            {
                return response;
            }

            List<InformationNeedVectorItem> foregroundVector = NormalizeInformationNeeds(response);
            response.InformationNeeds = foregroundVector;
            response.Debug.Notes.Add("Normalized model informationNeeds: " + SummarizeNeedsForEvent(foregroundVector));
            if (foregroundVector.Count == 0) return response;

            List<InformationNeedAvailabilityItem> availability = BuildInformationNeedAvailability(profile, state, request);
            response.InformationNeedAvailability = availability;

            // The foreground model already returned a scored informationNeeds array and the API
            // already has deterministic availability/threshold logic. Do not spend a second LLM
            // request merely selecting one item from that vector. A real deep-data follow-up still
            // runs only when the deterministic gate decides the need is strong enough.
            InformationNeedSelectionResult? aiSelection = null;
            InformationNeedSelectionResult decision = SelectInformationNeedDeterministically(foregroundVector, availability, aiSelection, response.NeedsMoreInformation);
            response.Debug.Notes.Add("Information need selection used the foreground informationNeeds array plus local-data availability; no selector LLM request was made.");
            response.InformationNeedSelection = decision;
            response.NeedsMoreInformation = decision.ShouldRequestMoreInformation;
            response.InformationFollowupContextSummary = SummarizeInformationNeedDecision(decision, foregroundVector, availability, 0);

            if (!decision.ShouldRequestMoreInformation)
            {
                response.Debug.Notes.Add("informationNeeds was present, but deterministic API selection did not request a deep second pass: " + decision.Reason);
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
                        { "informationNeeds", SummarizeNeedsForEvent(foregroundVector) },
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
                foreach (string learnedItem in foregroundResponse.ThingsLearnedOrSuggestedByPlayer ?? new List<string>())
                {
                    if (!followup.ThingsLearnedOrSuggestedByPlayer.Any(x => string.Equals(x, learnedItem, StringComparison.OrdinalIgnoreCase)))
                    {
                        followup.ThingsLearnedOrSuggestedByPlayer.Add(learnedItem);
                    }
                }
                followup.Debug.Notes.Add("requestStarDeepResponseWithData pass used deterministic information selection plus local archive/lore context.");
                followup.Debug.Notes.Add("Selected information need: " + SummarizeSelection(decision));
                followup.Debug.Notes.Add("Full foreground informationNeeds: " + SummarizeNeedsForEvent(fullVector));
                followup.Debug.Notes.Add("Extra context chars: " + context.Length);
                followup.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "InformationFollowupCompleted",
                    Target = request.StarId,
                    Data =
                    {
                        { "selected", SummarizeSelection(decision) },
                        { "informationNeeds", SummarizeNeedsForEvent(fullVector) },
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
                        { "informationNeeds", SummarizeNeedsForEvent(fullVector) },
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
            clone.GameContext.Flags["informationOriginalPlayerSpeech"] = request.PlayerText ?? string.Empty;
            clone.GameContext.RecentEvents.Add("This is the second response request for the same original player speech. The fast response detected that more information was needed. The API selected " + decision.SelectedDataType + " / " + decision.Subject + " and attached the matching source folder index. Read it and answer the exact detail the player asked for if it is present. Do not repeat the first uncertainty bridge; if the detail is absent, say the attached source did not contain it.");
            return clone;
        }

        private static List<InformationNeedVectorItem> NormalizeInformationNeeds(StarInteractionResponse response)
        {
            // The foreground model chooses only the source category in subject. The player's
            // original speech is kept separately and becomes the lookup question for that source.
            return (response.InformationNeeds ?? new List<InformationNeedVectorItem>())
                .Where(x => x != null && IsInformationNeedSubject(x.Subject))
                .Select(x => new InformationNeedVectorItem
                {
                    Subject = NormalizeInformationDataType(x.Subject),
                    DataType = NormalizeInformationDataType(x.Subject),
                    Confidence = Clamp01(x.Confidence),
                    Score = ClampScore(x.Score)
                })
                .GroupBy(x => x.DataType, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => Math.Max(x.Confidence, x.Score / 100.0)).First())
                .OrderByDescending(x => Math.Max(x.Confidence, x.Score / 100.0))
                .Take(9)
                .ToList();
        }

        private static bool IsInformationNeedSubject(string? subject)
        {
            string normalized = (subject ?? string.Empty).Trim().ToLowerInvariant();
            return normalized == "history" || normalized == "quotes" || normalized == "markers"
                || normalized == "lore" || normalized == "bestiary" || normalized == "characters"
                || normalized == "world" || normalized == "screen" || normalized == "story";
        }

        private static List<InformationNeedAvailabilityItem> BuildInformationNeedAvailability(StarProfile profile, StarState state, StarInteractionRequest request)
        {
            int oldArchives = state.OldConversationArchives == null ? 0 : state.OldConversationArchives.Count;
            int recentHistory = state.MemoryLog == null ? 0 : state.MemoryLog.Count;
            int learnedItems = state.ThingsLearnedOrSuggestedByPlayer == null ? 0 : state.ThingsLearnedOrSuggestedByPlayer.Count;
            int quotes = state.AllImportantPlayerQuotes == null ? 0 : state.AllImportantPlayerQuotes.Count;
            int markers = state.RememberedMarkers == null ? 0 : state.RememberedMarkers.Count(x => x.IsActive);
            int stories = state.StoryHistory == null ? 0 : state.StoryHistory.Count;
            int screens = request.RecentScreenCaptures == null ? 0 : request.RecentScreenCaptures.Count;

            List<InformationNeedAvailabilityItem> result = new List<InformationNeedAvailabilityItem>
            {
                Availability("history", oldArchives + recentHistory + learnedItems, oldArchives > 0 ? oldArchives + " old archives plus " + recentHistory + " recent turns and " + learnedItems + " durable learned item(s)" : recentHistory + " recent turns plus " + learnedItems + " durable learned item(s); no old archive yet", state.ThingsLearnedOrSuggestedByPlayer?.Take(4)),
                Availability("quotes", quotes, quotes + " durable important player quote(s)", state.AllImportantPlayerQuotes?.Select(x => x.Quote).Take(4)),
                Availability("markers", markers, markers + " active remembered marker(s)", state.RememberedMarkers?.Where(x => x.IsActive).Select(x => x.MarkerName).Take(6)),
                BuildLoreIndexAvailability("lore"),
                BuildLoreIndexAvailability("bestiary"),
                BuildLoreIndexAvailability("characters"),
                BuildLoreIndexAvailability("world"),
                Availability("screen", screens, screens + " recent screen/vision capture(s)", request.RecentScreenCaptures?.Select(x => x.CompanionSafeSummary).Take(4)),
                BuildStoryAvailability(stories, state)
            };

            return result;
        }

        private static InformationNeedAvailabilityItem BuildLoreIndexAvailability(string dataType)
        {
            if (LoreLibraryIndexReader.TryRead(dataType, out LoreLibraryIndexDocument index))
            {
                return Availability(dataType, 1, "Content/LoreLibrary/docs/" + index.RelativePath + " is available (" + index.CharacterCount + " chars).", new[] { index.RelativePath });
            }

            return Availability(dataType, 0, "No canonical " + dataType + "/index.md lore folder is available.", Array.Empty<string>());
        }

        private static InformationNeedAvailabilityItem BuildStoryAvailability(int storyHistoryCount, StarState state)
        {
            bool hasIndex = LoreLibraryIndexReader.TryRead("story", out LoreLibraryIndexDocument index);
            int count = storyHistoryCount + (hasIndex ? 1 : 0);
            List<string> samples = new List<string>();
            if (hasIndex) samples.Add(index.RelativePath);
            if (state.StoryHistory != null) samples.AddRange(state.StoryHistory.Select(x => x.Title).Where(x => !string.IsNullOrWhiteSpace(x)).Take(4));
            return Availability("story", count, storyHistoryCount + " personal story history entrie(s)" + (hasIndex ? " plus the canonical story/index.md folder." : "; no canonical story index."), samples);
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

        private static InformationNeedSelectionResult SelectInformationNeedDeterministically(
            List<InformationNeedVectorItem> foregroundVector,
            List<InformationNeedAvailabilityItem> availability,
            InformationNeedSelectionResult? aiSelection,
            bool legacyNeedsMoreInformation)
        {
            Dictionary<string, InformationNeedAvailabilityItem> availabilityByType = (availability ?? new List<InformationNeedAvailabilityItem>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.DataType))
                .GroupBy(x => NormalizeInformationDataType(x.DataType), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Count).First(), StringComparer.OrdinalIgnoreCase);
            IEnumerable<InformationNeedVectorItem> candidateSource = aiSelection != null && aiSelection.RankedNeeds != null && aiSelection.RankedNeeds.Count > 0
                ? aiSelection.RankedNeeds.Concat(foregroundVector)
                : foregroundVector;

            List<InformationNeedVectorItem> normalized = candidateSource
                .Where(x => x != null && (IsInformationNeedSubject(x.Subject) || IsInformationNeedSubject(x.DataType)))
                .Select(x =>
                {
                    string source = IsInformationNeedSubject(x.Subject) ? x.Subject : x.DataType;
                    string normalizedSource = NormalizeInformationDataType(source);
                    return new InformationNeedVectorItem
                    {
                        DataType = normalizedSource,
                        Subject = normalizedSource,
                        Confidence = Clamp01(x.Confidence),
                        Score = ClampScore(x.Score)
                    };
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
                        Subject = aiType,
                        Confidence = Clamp01(aiSelection.Confidence),
                        Score = ClampScore(aiSelection.Score),
                        Reason = FirstNonEmpty(aiSelection.Reason, "AI selector chose this source.")
                    };
                }
            }

            if (selected == null)
            {
                selected = normalized.FirstOrDefault(x => IsAvailable(x.DataType, availabilityByType));
            }

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
            bool strongEnough = chosenStrength >= 0.55 || legacyNeedsMoreInformation || (aiSelection != null && aiSelection.ShouldRequestMoreInformation && NormalizeInformationDataType(aiSelection.SelectedDataType) == selected.DataType);

            return new InformationNeedSelectionResult
            {
                ShouldRequestMoreInformation = strongEnough,
                SelectedDataType = selected.DataType,
                Subject = string.IsNullOrWhiteSpace(selected.Subject) ? "self" : selected.Subject,
                Confidence = selected.Confidence,
                Score = selected.Score,
                Reason = strongEnough
                    ? "Deterministic API selection chose the strongest available information area. " + selected.Reason
                    : "informationNeeds was present but below threshold after local availability checks.",
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
                    Subject = selectedType,
                    Confidence = Clamp01(decision.Confidence),
                    Score = ClampScore(decision.Score),
                    Reason = decision.Reason
                });
            }
            else
            {
                selected[0].Subject = selectedType;
                selected[0].Confidence = Math.Max(selected[0].Confidence, Clamp01(decision.Confidence));
                selected[0].Score = Math.Max(selected[0].Score, ClampScore(decision.Score));
                selected[0].Reason = FirstNonEmpty(decision.Reason, selected[0].Reason);
            }
            return selected;
        }

        private static string BuildRequestedInformationContext(StarProfile profile, StarState state, List<InformationNeedVectorItem> needs, string playerText, int maxChars, StarInteractionRequest? request = null)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("SECOND RESPONSE REQUEST WITH MORE INFORMATION");
            sb.AppendLine("The fast foreground response said more information was needed. This request includes the matching local source. Answer the player's original speech again now, giving the exact detail asked for when it appears below.");
            sb.AppendLine("Do not repeat the earlier uncertainty bridge. Do not invent or generalize. If the exact answer is present, state it clearly and naturally in character. If it is not present, say that the attached source did not contain the exact detail.");
            sb.AppendLine();
            sb.AppendLine("PLAYER'S ORIGINAL SPEECH:");
            sb.AppendLine(QuoteForInfo(playerText ?? string.Empty));
            sb.AppendLine();
            sb.AppendLine("Selected informationNeeds: " + SummarizeNeedsForEvent(needs));

            bool wantsHistory = Wants(needs, "history");
            bool wantsQuotes = Wants(needs, "quotes") || wantsHistory;
            bool wantsMarkers = Wants(needs, "markers");
            bool wantsLore = Wants(needs, "lore") || Wants(needs, "characters") || Wants(needs, "world") || Wants(needs, "bestiary");
            bool wantsScreen = Wants(needs, "screen");
            bool wantsStory = Wants(needs, "story");
            bool attachedCanonicalIndex = AppendMatchedLoreIndexes(sb, needs);

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

            if ((wantsHistory || wantsLore) && state.ThingsLearnedOrSuggestedByPlayer != null && state.ThingsLearnedOrSuggestedByPlayer.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("DURABLE THINGS THIS STAR LEARNED OR HEARD SUGGESTED BY THE LAST CAT:");
                foreach (string item in state.ThingsLearnedOrSuggestedByPlayer
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .OrderByDescending(x => RelevanceScore(x, needs, playerText))
                    .Take(80))
                {
                    sb.AppendLine("- " + TruncateForInfo(item, 600));
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
                sb.AppendLine("PERSONAL STORY HISTORY ARCHIVE:");
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

            if (wantsLore && !attachedCanonicalIndex)
            {
                sb.AppendLine();
                sb.AppendLine("FALLBACK STAR PROFILE CANON (no matching folder index was found):");
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

        private static bool AppendMatchedLoreIndexes(StringBuilder sb, List<InformationNeedVectorItem> needs)
        {
            bool attached = false;
            foreach (string dataType in (needs ?? new List<InformationNeedVectorItem>())
                .Select(x => NormalizeInformationDataType(x.DataType))
                .Where(LoreLibraryIndexReader.IsContentBackedType)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!LoreLibraryIndexReader.TryRead(dataType, out LoreLibraryIndexDocument index)) continue;
                attached = true;
                sb.AppendLine();
                sb.AppendLine("MATCHED LORE FOLDER: Content/LoreLibrary/docs/" + index.FolderName + "/");
                sb.AppendLine("ATTACHED FILE: " + index.RelativePath);
                sb.AppendLine("The complete canonical index for the selected folder follows:");
                sb.AppendLine();
                sb.AppendLine(index.Content.Trim());
            }
            return attached;
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


        private static string SummarizeInformationNeedDecision(InformationNeedSelectionResult decision, List<InformationNeedVectorItem> vector, List<InformationNeedAvailabilityItem> availability, int contextChars)
        {
            return SummarizeSelection(decision) + "; informationNeeds=" + SummarizeNeedsForEvent(vector) + "; available=" + string.Join(", ", availability.Where(x => x.Available).Select(x => x.DataType + "(" + x.Count + ")")) + "; local context chars=" + contextChars;
        }

        private static string SummarizeSelection(InformationNeedSelectionResult? decision)
        {
            if (decision == null) return "none";
            return (decision.ShouldRequestMoreInformation ? "request" : "no-request") + ":" + NormalizeInformationDataType(decision.SelectedDataType) + ":" + (string.IsNullOrWhiteSpace(decision.Subject) ? "self" : decision.Subject) + "@" + decision.Confidence.ToString("0.00") + "/" + decision.Score.ToString("0") + " source=" + FirstNonEmpty(decision.Source, "unknown") + " reason=" + TruncateForInfo(decision.Reason, 180);
        }

        private static string SummarizeNeedsForEvent(List<InformationNeedVectorItem> needs)
        {
            return string.Join(", ", (needs ?? new List<InformationNeedVectorItem>()).Select(x => (string.IsNullOrWhiteSpace(x.Subject) ? NormalizeInformationDataType(x.DataType) : x.Subject) + "@" + x.Confidence.ToString("0.00") + "/" + x.Score.ToString("0")));
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
                if (string.IsNullOrWhiteSpace(value)) continue;
                string trimmed = value.Trim();
                if (LooksLikeZeroCompactVector(trimmed)) continue;
                return trimmed;
            }
            return string.Empty;
        }

        private static bool LooksLikeZeroCompactVector(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf(';') < 0 || value.IndexOf(':') < 0) return false;
            string[] parts = value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            foreach (string raw in parts)
            {
                string[] pair = raw.Trim().Split(':');
                if (pair.Length < 2) continue;
                double.TryParse(pair[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double confidence);
                double.TryParse(pair[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double score);
                if (confidence > 0 || score > 0) return false;
            }
            return true;
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

        private static StarInteractionResponse CreateStoryThinkingResponse(StarProfile profile, StarInteractionRequest request)
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
            bool explicitProposalIntent = IsTruthy(GetGameContextFlag(request.GameContext, "girlfriendProposalDetected", "false"));
            if (!explicitProposalIntent && !GirlfriendProposalIntentDetector.IsProposal(request.PlayerText)) return null;

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

            // A proposal is a foreground relationship transaction, not disposable background
            // flavor. Keep its continuation independent from the shared story/background CTS so a
            // mobile state refresh, automatic initiative check, or later foreground request cannot
            // cancel the state transition after the Star has begun answering.
            _backgroundDispatcher.Enqueue(
                "GirlfriendProposalDecision",
                "Generate and durably commit girlfriend proposal response for " + workerRequest.StarId,
                BackgroundAiRequestPriority.High,
                async workToken =>
            {
                try
                {
                    await PopulateKnownStarContextAsync(workerRequest, workToken).ConfigureAwait(false);
                    StarMemorySnapshot latestSnapshot = await _memoryApi.GetStarMemorySnapshotAsync(workerRequest.SessionId, workerRequest.StarId, _recentHistoryCount, workToken).ConfigureAwait(false);
                    StarState latestState = latestSnapshot.State;
                    await AttachDailyStateContextForRequestAsync(workerRequest, latestState, workToken).ConfigureAwait(false);

                    string currentGirlfriend = await GetCurrentGirlfriendStarIdAsync(workerRequest.SessionId, workToken).ConfigureAwait(false);
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

                    StarInteractionResponse proposalResponse = await _agent.GenerateAsync(profile, workerRequest, latestState, workToken).ConfigureAwait(false);
                    ApplyVoiceDefaults(profile, proposalResponse);
                    proposalResponse.Debug.DetectedIntent = "GirlfriendProposal";
                    proposalResponse.Debug.Notes.Add("Girlfriend proposal worker decision: " + workerRequest.GameContext.Flags["girlfriendProposalDecision"] + "; reason=" + workerRequest.GameContext.Flags["girlfriendProposalReason"]);

                    if (shouldAccept)
                    {
                        latestState = await SetExclusiveGirlfriendAsync(workerRequest.SessionId, workerRequest.StarId, true, "Accepted direct girlfriend proposal from: " + (workerRequest.PlayerText ?? string.Empty), workToken).ConfigureAwait(false);
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
            },
                CancellationToken.None);
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

        private void QueueStoryGenerationAfterThinkingResponse(StarProfile profile, StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
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

            _backgroundDispatcher.Enqueue(
                "StoryGeneration",
                "Generate full story response for " + workerRequest.StarId,
                BackgroundAiRequestPriority.Normal,
                async workToken =>
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background story generation started for " + profile.StarId + ". This is detached from the quick-thinking TTS operation token so the placeholder voice cannot cancel the story LLM call.");
                    StarInteractionResponse storyResponse = await _agent.GenerateAsync(profile, workerRequest, state, workToken).ConfigureAwait(false);
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
            },
                storyToken);
        }

        private static void PrepareExplicitStoryRequest(StarInteractionRequest request, StarState state)
        {
            // Story narration is never inferred from ordinary Chat text. A player can ask for a
            // long story in Chat and receive the entire answer through spokenText like any other
            // response. The legacy structured Story pipeline remains available only to callers
            // that explicitly set RequestType.Story.
            if (request.RequestType == RequestType.Story)
            {
                PrepareStoryOptionsForNewOrContinuingStory(request, state);
            }
        }

        private static void PrepareStoryOptionsForNewOrContinuingStory(StarInteractionRequest request, StarState state)
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

            bool hadExplicitTargetMinutes = options.TargetMinutes > 0;
            if (options.TargetMinutes <= 0)
            {
                int minMinutes = ResolveStoryRandomMinimumMinutes();
                int maxMinutes = ResolveStoryRandomMaximumMinutes(minMinutes);
                lock (StoryRandomGate)
                {
                    options.TargetMinutes = StoryRandom.Next(minMinutes, maxMinutes + 1);
                }
            }

            if (options.TargetChunkCount <= 0)
            {
                int minChunks = ResolveStoryRandomMinimumChunks();
                if (hadExplicitTargetMinutes)
                {
                    // An explicit duration from the player is a hard creative requirement. Do not
                    // silently collapse a long requested story back to the short default range.
                    options.TargetChunkCount = Math.Max(minChunks, (int)Math.Ceiling(Math.Max(1, options.TargetMinutes) * 0.8));
                }
                else
                {
                    // Unseeded/default story requests stay compact enough for local models, while
                    // explicit player length requests above are allowed to scale beyond this range.
                    int maxChunks = ResolveStoryRandomMaximumChunks(minChunks);
                    int flexibleMinuteTarget = Math.Max(2, Math.Min(12, options.TargetMinutes));
                    options.TargetChunkCount = Math.Max(minChunks, Math.Min(maxChunks, (int)Math.Ceiling(flexibleMinuteTarget * 0.8)));
                }
            }

            options.Length = "flexible_random_" + options.TargetMinutes + "_minute_complete_story_" + options.TargetChunkCount + "_chunks";
            if (string.IsNullOrWhiteSpace(options.Tone)) options.Tone = "comforting, adventurous, cave-travel friendly";
            if (string.IsNullOrWhiteSpace(options.CanonStrictness)) options.CanonStrictness = "high";

            if (string.IsNullOrWhiteSpace(options.PlayerSuggestion) && !string.IsNullOrWhiteSpace(request.PlayerText))
            {
                options.PlayerSuggestion = request.PlayerText;
            }
            else if (string.IsNullOrWhiteSpace(options.PlayerSuggestion))
            {
                request.GameContext.Flags["storyButtonUnseeded"] = "true";
                request.GameContext.RecentEvents.Add("The Story button was clicked with no seed; the Star should invent a fresh complete story without asking for a seed.");
            }

            request.GameContext.Flags["storyTargetMinutes"] = options.TargetMinutes.ToString();
            request.GameContext.Flags["storyTargetChunkCount"] = options.TargetChunkCount.ToString();
            request.GameContext.Flags["storyMaxSentencesPerChunk"] = options.MaxSentencesPerChunk.ToString();
            request.GameContext.Flags["storyLengthIsFlexible"] = "true";
        }

        private static int ResolveStoryRandomMinimumMinutes()
        {
            string? env = Environment.GetEnvironmentVariable("SIX_STARS_STORY_TARGET_MINUTES_MIN");
            if (int.TryParse(env, out int value)) return Math.Max(1, Math.Min(20, value));
            return 3;
        }

        private static int ResolveStoryRandomMaximumMinutes(int minMinutes)
        {
            string? env = Environment.GetEnvironmentVariable("SIX_STARS_STORY_TARGET_MINUTES_MAX");
            if (int.TryParse(env, out int value)) return Math.Max(minMinutes, Math.Min(35, value));
            return Math.Max(minMinutes, 12);
        }

        private static int ResolveStoryRandomMinimumChunks()
        {
            string? env = Environment.GetEnvironmentVariable("SIX_STARS_STORY_TARGET_CHUNKS_MIN");
            if (int.TryParse(env, out int value)) return Math.Max(3, Math.Min(16, value));
            return 6;
        }

        private static int ResolveStoryRandomMaximumChunks(int minChunks)
        {
            string? env = Environment.GetEnvironmentVariable("SIX_STARS_STORY_TARGET_CHUNKS_MAX");
            if (int.TryParse(env, out int value)) return Math.Max(minChunks, Math.Min(24, value));
            return Math.Max(minChunks, 10);
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

            // A transport disconnect or replaced Android voice stream must never leave this update
            // waiting forever. Normal voice completion removes it first, so this fallback is a no-op
            // in the healthy path. Keep the delay long enough that a one-page story can finish TTS.
            _ = Task.Run(async () =>
            {
                try
                {
                    int fallbackSeconds = 180;
                    string? configured = Environment.GetEnvironmentVariable("SIX_STARS_BACKGROUND_ANALYSIS_FALLBACK_SECONDS");
                    if (int.TryParse(configured, out int parsed)) fallbackSeconds = Math.Max(30, Math.Min(900, parsed));
                    await Task.Delay(TimeSpan.FromSeconds(fallbackSeconds)).ConfigureAwait(false);
                    StartBackgroundAnalysisAfterVoiceReady(analysisId, "Voice-ready callback fallback after " + fallbackSeconds + " seconds.");
                }
                catch
                {
                }
            });
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
            QueueBackgroundAnalysis(analysisId, pending.Analyzer, pending.Profile, pending.Request, pending.StateBeforeTurn, pending.ForegroundResponse);
            return true;
        }

        private void QueueBackgroundAnalysis(string analysisId, IStarTurnAnalyzer analyzer, StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse foregroundResponse)
        {
            _backgroundDispatcher.Enqueue(
                "PrimaryBackgroundAnalysis",
                "Analyze emotion vectors and update durable Star state for " + request.StarId,
                BackgroundAiRequestPriority.High,
                async workToken =>
                {
                    try
                    {
                        // Keep background state writes ordered. A later Unity/UI host can choose to wait on this
                        // semaphore before accepting the next speech turn if it needs fully settled state.
                        await _backgroundAnalysisQueue.WaitAsync(workToken).ConfigureAwait(false);
                        try
                        {
                            StarInteractionResponse analyzed = await analyzer.AnalyzeTurnAsync(profile, request, state, foregroundResponse, workToken).ConfigureAwait(false);
                            ApplyVoiceDefaults(profile, analyzed);
                            await ApplyAnalysisAndMemoryUpdateAsync(request, analyzed, workToken).ConfigureAwait(false);
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
                        System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background emotion/memory analysis was preempted and will be retried after foreground work: " + analysisId);
                        // Reinsert the exact analysis rather than dropping it. The dispatcher will not
                        // run the retry until all foreground leases have completed.
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
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(500).ConfigureAwait(false);
                                StartBackgroundAnalysisAfterVoiceReady(analysisId, "Retry after foreground preempted the background model call.");
                            }
                            catch
                            {
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background emotion/memory analysis failed: " + ex);
                        BackgroundAnalysisFailed?.Invoke(this, ex);
                    }
                },
                delay: TimeSpan.FromSeconds(5));
        }

        private async Task ApplyAnalysisAndMemoryUpdateAsync(StarInteractionRequest request, StarInteractionResponse response, CancellationToken cancellationToken)
        {
            StarState currentState = await _memoryApi.GetStarStateAsync(request.SessionId, request.StarId, cancellationToken).ConfigureAwait(false);
            bool isInterstarMicroUpdate = IsInterstarMicroRequest(request);
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
                ThingsLearnedOrSuggestedByPlayer = response.ThingsLearnedOrSuggestedByPlayer,
                RememberedMarkers = response.RememberedMarkers,
                ReferencedMemoryMarkerIds = response.ReferencedMemoryMarkerIds,
                SecretsUnlocked = response.SecretsUnlocked,
                DetectedCombos = response.DetectedCombos,
                LearnedFunctions = response.LearnedFunctions,
                RelationshipEvents = response.RelationshipEvents,
                GirlfriendHistoryUpdates = response.GirlfriendHistoryUpdates,
                PetNamesPlayerUsesForStar = response.PetNamesPlayerUsesForStar,
                PetNamesStarUsesForPlayer = response.PetNamesStarUsesForPlayer,
                GirlfriendNeedsMetSummary = response.GirlfriendNeedsMetSummary,
                GirlfriendNeedsMetVector = response.GirlfriendNeedsMetVector,
                GirlfriendWantsMetVector = response.GirlfriendWantsMetVector,
                GirlfriendCoupleActivitySummary = response.GirlfriendCoupleActivitySummary,
                GirlfriendCoupleActivityVector = response.GirlfriendCoupleActivityVector,
                WisdomScore = response.Debug.WisdomScore,
                StoryId = isStoryTurn ? (response.StoryId ?? string.Empty) : string.Empty,
                StoryTitle = isStoryTurn ? (response.StoryTitle ?? string.Empty) : string.Empty,
                StorySummary = isStoryTurn ? (response.StorySummary ?? string.Empty) : string.Empty,
                StoryFinished = isStoryTurn && response.StoryIsFinished,
                StorySeed = isStoryTurn ? (request.StoryOptions?.PlayerSuggestion ?? string.Empty) : string.Empty,
                IsInternalBackgroundUpdate = isInterstarMicroUpdate,
                SuppressConversationSummaryUpdate = isInterstarMicroUpdate,
                SuppressConversationLog = isInterstarMicroUpdate
            }, cancellationToken).ConfigureAwait(false);

            response.EmotionalConnectionPercentAfter = update.State.EmotionalConnectionPercent;
            response.IsGirlfriendReadyAfter = update.State.IsGirlfriendReady;
            response.GirlfriendReadinessSummary = update.State.GirlfriendReadinessSummary ?? string.Empty;
            if (!isInterstarMicroUpdate)
            {
                response.TopAffectedEmotions.Clear();
                response.TopAffectedEmotions.AddRange((update.State.LastTopAffectedEmotions ?? new List<TopAffectedEmotion>()));
            }

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
            response.Debug.Notes.Add(isInterstarMicroUpdate
                ? "StarMemoryApi applied hidden interstar state update without appending a direct conversation turn. Interaction count: " + update.State.InteractionCount + "."
                : "StarMemoryApi updated durable state from background/full analysis. Interaction count: " + update.State.InteractionCount + ".");
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

            _backgroundDispatcher.Enqueue(
                "InterstarUpdateCycle",
                "Run settled interstar emotional ripple for " + request.StarId,
                BackgroundAiRequestPriority.Low,
                async workToken =>
                {
                    try
                    {
                        await _interstarUpdateQueue.WaitAsync(workToken).ConfigureAwait(false);
                        try
                        {
                            await RunInterstarCycleAsync(interstarAgent, requestClone, responseClone, workToken).ConfigureAwait(false);
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
                },
                cts.Token,
                _interstarSettleDelay);
        }

        private async Task RunInterstarCycleAsync(IInterstarUpdateAgent interstarAgent, StarInteractionRequest originalRequest, StarInteractionResponse originResponse, CancellationToken cancellationToken)
        {
            string sessionId = string.IsNullOrWhiteSpace(originalRequest.SessionId) ? "local-dev" : originalRequest.SessionId;
            string originStarId = originalRequest.StarId;
            StarProfile originProfile = _profileProvider.GetProfile(originStarId);
            StarState originState = await _memoryApi.GetStarStateAsync(sessionId, originStarId, cancellationToken).ConfigureAwait(false);
            await RefreshOtherStarsViewAsync(sessionId, originState, cancellationToken).ConfigureAwait(false);
            List<StarProfile> targets = new List<StarProfile>();

            foreach (StarProfile candidate in _profileProvider.GetAllProfiles())
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.StarId)) continue;
                if (string.Equals(candidate.StarId, originStarId, StringComparison.OrdinalIgnoreCase)) continue;
                StarState candidateState = await _memoryApi.GetStarStateAsync(sessionId, candidate.StarId, cancellationToken).ConfigureAwait(false);
                await RefreshOtherStarsViewAsync(sessionId, candidateState, cancellationToken).ConfigureAwait(false);
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
                await RefreshOtherStarsViewAsync(sessionId, targetState, cancellationToken).ConfigureAwait(false);
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
                List<InterstarRelationshipDeltaResult> targetInterstarDeltas = await ApplyInterstarRelationshipVectorsAsync(
                    sessionId,
                    targetProfile.StarId,
                    originStarId,
                    FirstNonEmpty(targetReaction.SourceStarEmotionalEffectVector, FindInterstarPositiveVector(targetReaction.InterstarEmotionVectors, originStarId)),
                    FirstNonEmpty(targetReaction.SourceStarNegativeEmotionVector, FindInterstarNegativeVector(targetReaction.InterstarEmotionVectors, originStarId)),
                    "interstar relay from " + originProfile.DisplayName + " to " + targetProfile.DisplayName,
                    cancellationToken).ConfigureAwait(false);
                targetReaction.InterstarRelationshipDeltas.AddRange(targetInterstarDeltas);
                StarState targetStateAfter = await _memoryApi.GetStarStateAsync(sessionId, targetProfile.StarId, cancellationToken).ConfigureAwait(false);
                await RefreshOtherStarsViewAsync(sessionId, targetStateAfter, cancellationToken).ConfigureAwait(false);
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
                    SourceStarEmotionalEffectVector = targetReaction.SourceStarEmotionalEffectVector ?? string.Empty,
                    SourceStarNegativeEmotionVector = targetReaction.SourceStarNegativeEmotionVector ?? string.Empty,
                    InterstarEmotionVectors = targetReaction.InterstarEmotionVectors.ToList(),
                    InterstarRelationshipDeltas = targetReaction.InterstarRelationshipDeltas.ToList(),
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
                await RefreshOtherStarsViewAsync(sessionId, latestOriginState, cancellationToken).ConfigureAwait(false);
                StarInteractionResponse originFeedback = await interstarAgent.AnalyzeInterstarFeedbackAsync(originProfile, originalRequest, originResponse, latestOriginState, cycle.Reactions, cancellationToken).ConfigureAwait(false);
                StarInteractionRequest feedbackRequest = CreateInterstarMicroRequest(originalRequest, originStarId, "Interstar feedback from affected Stars after " + originProfile.DisplayName + " shared the recent Last Cat conversation.");
                await ApplyAnalysisAndMemoryUpdateAsync(feedbackRequest, originFeedback, cancellationToken).ConfigureAwait(false);
                foreach (InterstarEmotionVectorResult vector in (originFeedback.InterstarEmotionVectors ?? new List<InterstarEmotionVectorResult>()).Where(x => x != null && !string.IsNullOrWhiteSpace(x.StarId)))
                {
                    if (string.Equals(vector.StarId, originStarId, StringComparison.OrdinalIgnoreCase)) continue;
                    List<InterstarRelationshipDeltaResult> feedbackDeltas = await ApplyInterstarRelationshipVectorsAsync(
                        sessionId,
                        originStarId,
                        vector.StarId,
                        vector.PositiveEmotionVector,
                        vector.NegativeEmotionVector,
                        "interstar feedback to " + originProfile.DisplayName + " about " + vector.StarId,
                        cancellationToken).ConfigureAwait(false);
                    originFeedback.InterstarRelationshipDeltas.AddRange(feedbackDeltas);
                }
                StarState originStateAfterFeedback = await _memoryApi.GetStarStateAsync(sessionId, originStarId, cancellationToken).ConfigureAwait(false);
                await RefreshOtherStarsViewAsync(sessionId, originStateAfterFeedback, cancellationToken).ConfigureAwait(false);
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
            if (request.RequestType == RequestType.Story) return false;
            if (request.RequestType == RequestType.Debug) return false;
            if (string.IsNullOrWhiteSpace(request.StarId)) return false;
            if (string.IsNullOrWhiteSpace(response.EmotionalEffectVector) && string.IsNullOrWhiteSpace(response.NegativeEmotionVector) && response.TopAffectedEmotions.Count == 0) return false;
            return true;
        }

        private async Task RefreshOtherStarsViewAsync(string sessionId, StarState ownerState, CancellationToken cancellationToken)
        {
            if (ownerState == null || string.IsNullOrWhiteSpace(ownerState.StarId)) return;
            try
            {
                string normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId;
                SixStarsSaveData saveData = await _memoryApi.ExportSaveDataAsync(normalizedSessionId, cancellationToken).ConfigureAwait(false);
                ApplyOtherStarsView(ownerState, saveData.Stars ?? new List<StarState>());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Prompt enrichment should never block foreground speech if the optional snapshot cannot be read.
                if (ownerState.OtherStars == null) ownerState.OtherStars = new List<InterstarRelationshipState>();
            }
        }

        private void ApplyOtherStarsView(StarState ownerState, IEnumerable<StarState> allStates)
        {
            if (ownerState == null || string.IsNullOrWhiteSpace(ownerState.StarId)) return;
            Dictionary<string, StarState> stateById = (allStates ?? Enumerable.Empty<StarState>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.StarId))
                .GroupBy(x => x.StarId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, InterstarRelationshipState> existing = (ownerState.OtherStars ?? new List<InterstarRelationshipState>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.StarId))
                .GroupBy(x => x.StarId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

            List<InterstarRelationshipState> next = new List<InterstarRelationshipState>();
            foreach (StarProfile profile in _profileProvider.GetAllProfiles().Where(x => x != null && !string.IsNullOrWhiteSpace(x.StarId)).OrderBy(x => x.DisplayName))
            {
                if (string.Equals(profile.StarId, ownerState.StarId, StringComparison.OrdinalIgnoreCase)) continue;
                existing.TryGetValue(profile.StarId, out InterstarRelationshipState? relation);
                relation ??= new InterstarRelationshipState();
                relation.StarId = profile.StarId;
                relation.StarName = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.StarId : profile.DisplayName;
                if (relation.History == null) relation.History = new List<InterstarRelationshipHistoryEntry>();

                if (stateById.TryGetValue(profile.StarId, out StarState? otherState))
                {
                    relation.IsMet = otherState.IsMet;
                    relation.IsAlly = otherState.IsAlly;
                    relation.IsGirlfriend = otherState.IsGirlfriend;
                }
                else
                {
                    bool elusiaa = string.Equals(profile.StarId, "Elusiaa", StringComparison.OrdinalIgnoreCase);
                    relation.IsMet = elusiaa;
                    relation.IsAlly = elusiaa;
                    relation.IsGirlfriend = false;
                }
                relation.TrustScore = ClampInt(relation.TrustScore, 0, 100);
                relation.HeartScore = ClampInt(relation.HeartScore, 0, 100);
                next.Add(relation);
            }

            ownerState.OtherStars = next;
        }

        private static string FindInterstarPositiveVector(IEnumerable<InterstarEmotionVectorResult>? vectors, string starId)
        {
            return (vectors ?? Enumerable.Empty<InterstarEmotionVectorResult>())
                .LastOrDefault(x => x != null && string.Equals(x.StarId, starId, StringComparison.OrdinalIgnoreCase))
                ?.PositiveEmotionVector ?? string.Empty;
        }

        private static string FindInterstarNegativeVector(IEnumerable<InterstarEmotionVectorResult>? vectors, string starId)
        {
            return (vectors ?? Enumerable.Empty<InterstarEmotionVectorResult>())
                .LastOrDefault(x => x != null && string.Equals(x.StarId, starId, StringComparison.OrdinalIgnoreCase))
                ?.NegativeEmotionVector ?? string.Empty;
        }

        private async Task<List<InterstarRelationshipDeltaResult>> ApplyInterstarRelationshipVectorsAsync(
            string sessionId,
            string ownerStarId,
            string affectedStarId,
            string positiveEmotionVector,
            string negativeEmotionVector,
            string reason,
            CancellationToken cancellationToken)
        {
            List<InterstarRelationshipDeltaResult> results = new List<InterstarRelationshipDeltaResult>();
            if (string.IsNullOrWhiteSpace(ownerStarId) || string.IsNullOrWhiteSpace(affectedStarId)) return results;
            if (string.Equals(ownerStarId, affectedStarId, StringComparison.OrdinalIgnoreCase)) return results;

            List<EmotionVectorScore> positive = RelationshipDeltaCalculator.ParseVector(positiveEmotionVector, StarEmotionMatrix.StandardEmotionIds);
            List<EmotionVectorScore> negative = RelationshipDeltaCalculator.ParseVector(negativeEmotionVector, RelationshipDeltaCalculator.NegativeEmotionIds);
            bool hasEvidence = positive.Any(x => x != null && (x.Confidence > 0 || x.Score > 0))
                || negative.Any(x => x != null && (x.Confidence > 0 || x.Score > 0));
            if (!hasEvidence) return results;

            string normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId;
            await _memoryApi.GetStarStateAsync(normalizedSessionId, ownerStarId, cancellationToken).ConfigureAwait(false);
            await _memoryApi.GetStarStateAsync(normalizedSessionId, affectedStarId, cancellationToken).ConfigureAwait(false);
            SixStarsSaveData saveData = await _memoryApi.ExportSaveDataAsync(normalizedSessionId, cancellationToken).ConfigureAwait(false);
            if (saveData.Stars == null) saveData.Stars = new List<StarState>();

            StarState owner = saveData.Stars.FirstOrDefault(x => string.Equals(x.StarId, ownerStarId, StringComparison.OrdinalIgnoreCase))
                ?? new StarState { StarId = ownerStarId };
            if (!saveData.Stars.Any(x => ReferenceEquals(x, owner))) saveData.Stars.Add(owner);

            ApplyOtherStarsView(owner, saveData.Stars);
            InterstarRelationshipState relation = owner.OtherStars.FirstOrDefault(x => string.Equals(x.StarId, affectedStarId, StringComparison.OrdinalIgnoreCase))
                ?? new InterstarRelationshipState { StarId = affectedStarId, StarName = affectedStarId };
            if (!owner.OtherStars.Contains(relation)) owner.OtherStars.Add(relation);
            if (relation.History == null) relation.History = new List<InterstarRelationshipHistoryEntry>();

            (int heartDelta, int trustDelta, string summary) = CalculateInterstarRelationshipDelta(positive, negative);
            if (heartDelta == 0 && trustDelta == 0)
            {
                // Keep the last vectors durable even when they are too small to move the meters.
                summary = "Interstar vectors recorded without a meter delta: " + summary;
            }

            relation.HeartScore = ClampInt(relation.HeartScore + heartDelta, 0, 100);
            relation.TrustScore = ClampInt(relation.TrustScore + trustDelta, 0, 100);
            relation.LastPositiveEmotionVector = positiveEmotionVector ?? string.Empty;
            relation.LastNegativeEmotionVector = negativeEmotionVector ?? string.Empty;
            relation.LastUpdatedAt = DateTimeOffset.UtcNow;
            relation.History.Insert(0, new InterstarRelationshipHistoryEntry
            {
                CreatedAt = DateTimeOffset.UtcNow,
                SourceStarId = ownerStarId,
                AffectedStarId = affectedStarId,
                Reason = reason ?? string.Empty,
                PositiveEmotionVector = positiveEmotionVector ?? string.Empty,
                NegativeEmotionVector = negativeEmotionVector ?? string.Empty,
                HeartDelta = heartDelta,
                TrustDelta = trustDelta,
                HeartAfter = relation.HeartScore,
                TrustAfter = relation.TrustScore,
                Summary = summary
            });
            if (relation.History.Count > 40) relation.History.RemoveRange(40, relation.History.Count - 40);

            // Refresh all visible met/ally/girlfriend flags before saving so every shard carries the prompt-facing view.
            foreach (StarState state in saveData.Stars.Where(x => x != null)) ApplyOtherStarsView(state, saveData.Stars);
            await _memoryApi.ImportSaveDataAsync(saveData, cancellationToken).ConfigureAwait(false);

            results.Add(new InterstarRelationshipDeltaResult
            {
                OwnerStarId = ownerStarId,
                AffectedStarId = affectedStarId,
                HeartDelta = heartDelta,
                TrustDelta = trustDelta,
                HeartAfter = relation.HeartScore,
                TrustAfter = relation.TrustScore,
                PositiveEmotionVector = positiveEmotionVector ?? string.Empty,
                NegativeEmotionVector = negativeEmotionVector ?? string.Empty,
                Summary = summary
            });
            return results;
        }

        private static (int heartDelta, int trustDelta, string summary) CalculateInterstarRelationshipDelta(
            IReadOnlyList<EmotionVectorScore> positive,
            IReadOnlyList<EmotionVectorScore> negative)
        {
            double heart = 0;
            double trust = 0;
            foreach (EmotionVectorScore signal in positive ?? Array.Empty<EmotionVectorScore>())
            {
                if (signal == null || (signal.Confidence <= 0 && signal.Score <= 0)) continue;
                double weighted = Math.Max(0, signal.Confidence) * Math.Max(0, signal.Score);
                heart += weighted * GetInterstarPositiveHeartWeight(signal.Id);
                trust += weighted * GetInterstarPositiveTrustWeight(signal.Id);
            }
            foreach (EmotionVectorScore signal in negative ?? Array.Empty<EmotionVectorScore>())
            {
                if (signal == null || (signal.Confidence <= 0 && signal.Score <= 0)) continue;
                double weighted = Math.Max(0, signal.Confidence) * Math.Max(0, signal.Score);
                heart -= weighted * GetInterstarNegativeHeartWeight(signal.Id);
                trust -= weighted * GetInterstarNegativeTrustWeight(signal.Id);
            }

            int heartDelta = ScoreToInterstarDelta(heart);
            int trustDelta = ScoreToInterstarDelta(trust);
            string summary = "interstar weighted heart=" + heart.ToString("0.0") + ", trust=" + trust.ToString("0.0") + ", delta H/T=" + heartDelta + "/" + trustDelta;
            return (heartDelta, trustDelta, summary);
        }

        private static int ScoreToInterstarDelta(double score)
        {
            if (score >= 18) return 2;
            if (score >= 7) return 1;
            if (score <= -18) return -2;
            if (score <= -7) return -1;
            return 0;
        }

        private static double GetInterstarPositiveHeartWeight(string id)
        {
            switch (id ?? string.Empty)
            {
                case "Affectionate": return 1.1;
                case "Tender": return 1.2;
                case "Happy": return 0.8;
                case "Comforted": return 0.8;
                case "Admiring": return 0.8;
                case "Vulnerable": return 0.7;
                case "Hopeful": return 0.6;
                case "Wondrous": return 0.6;
                case "Protected": return 0.6;
                case "Amused": return 0.5;
                case "Playful": return 0.5;
                case "Safe": return 0.5;
                case "Trusting": return 0.5;
                case "Respected": return 0.4;
                case "Intrigued": return 0.3;
                default: return 0.15;
            }
        }

        private static double GetInterstarPositiveTrustWeight(string id)
        {
            switch (id ?? string.Empty)
            {
                case "Trusting": return 1.2;
                case "Respected": return 1.0;
                case "Safe": return 0.8;
                case "Protected": return 0.7;
                case "Admiring": return 0.6;
                case "Comforted": return 0.5;
                case "Affectionate": return 0.5;
                case "Hopeful": return 0.4;
                case "Wondrous": return 0.4;
                case "Tender": return 0.4;
                case "Intrigued": return 0.3;
                default: return 0.15;
            }
        }

        private static double GetInterstarNegativeHeartWeight(string id)
        {
            switch (id ?? string.Empty)
            {
                case "Hurt": return 1.2;
                case "Disrespected": return 0.8;
                case "CreepedOut": return 0.8;
                case "Unsafe": return 0.8;
                case "Irritated": return 0.7;
                case "Pressured": return 0.5;
                case "Bored": return 0.3;
                default: return 0.2;
            }
        }

        private static double GetInterstarNegativeTrustWeight(string id)
        {
            switch (id ?? string.Empty)
            {
                case "Unsafe": return 1.5;
                case "Disrespected": return 1.4;
                case "CreepedOut": return 1.2;
                case "Pressured": return 1.0;
                case "Hurt": return 0.8;
                case "Irritated": return 0.5;
                case "Bored": return 0.2;
                default: return 0.2;
            }
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
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
                SourceStarEmotionalEffectVector = source.SourceStarEmotionalEffectVector,
                SourceStarNegativeEmotionVector = source.SourceStarNegativeEmotionVector,
                InterstarReplyToSourceStar = source.InterstarReplyToSourceStar
            };
            clone.InterstarEmotionVectors.AddRange(source.InterstarEmotionVectors ?? new List<InterstarEmotionVectorResult>());
            clone.InterstarRelationshipDeltas.AddRange(source.InterstarRelationshipDeltas ?? new List<InterstarRelationshipDeltaResult>());
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
