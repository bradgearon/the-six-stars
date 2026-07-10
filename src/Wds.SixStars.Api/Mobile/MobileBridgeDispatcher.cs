using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Mobile;

/// <summary>
/// Small in-process dispatcher for mobile/control UI commands. The API layer owns the command and
/// event shapes so Android, desktop, Unity, and future front ends do not directly mutate a view model.
/// The host view model registers one handler, applies the command to the runtime/state store, and then
/// publishes queued events back through this dispatcher for subscribed clients.
/// </summary>
public sealed class MobileBridgeDispatcher
{
    private readonly object _gate = new object();
    private readonly List<MobileBridgeDispatchEvent> _events = new List<MobileBridgeDispatchEvent>();
    private Func<MobileBridgeDispatchCommand, CancellationToken, Task<MobileBridgeDispatchResult>>? _handler;
    private long _nextSequence;
    private const int MaxEvents = 250;

    public event EventHandler<MobileBridgeDispatchEvent>? EventPublished;

    public void RegisterCommandHandler(Func<MobileBridgeDispatchCommand, CancellationToken, Task<MobileBridgeDispatchResult>> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public async Task<MobileBridgeDispatchResult> DispatchAsync(MobileBridgeDispatchCommand command, CancellationToken cancellationToken)
    {
        if (command == null) throw new ArgumentNullException(nameof(command));
        command.CommandId = string.IsNullOrWhiteSpace(command.CommandId) ? Guid.NewGuid().ToString("N") : command.CommandId;
        command.CommandType = NormalizeCommandType(command.CommandType);
        command.RequestedAt = command.RequestedAt == default ? DateTimeOffset.UtcNow : command.RequestedAt;

        Func<MobileBridgeDispatchCommand, CancellationToken, Task<MobileBridgeDispatchResult>>? handler = _handler;
        if (handler == null)
        {
            MobileBridgeDispatchEvent notReady = Publish(new MobileBridgeDispatchEvent
            {
                EventType = "BridgeCommandRejected",
                CommandId = command.CommandId,
                SourceClientId = command.SourceClientId,
                StarId = command.StarId,
                Message = "No desktop host is listening for mobile bridge commands yet."
            });
            return new MobileBridgeDispatchResult
            {
                Ok = false,
                CommandId = command.CommandId,
                Message = notReady.Message,
                Event = notReady
            };
        }

        try
        {
            MobileBridgeDispatchResult result = await handler(command, cancellationToken).ConfigureAwait(false);
            result.CommandId = command.CommandId;
            if (result.Event == null)
            {
                result.Event = Publish(MobileBridgeDispatchEvent.FromResult(command, result));
            }
            return result;
        }
        catch (Exception ex)
        {
            MobileBridgeDispatchEvent error = Publish(new MobileBridgeDispatchEvent
            {
                EventType = "BridgeCommandFailed",
                CommandId = command.CommandId,
                SourceClientId = command.SourceClientId,
                StarId = command.StarId,
                Message = ex.Message,
                Error = ex.GetType().Name
            });
            return new MobileBridgeDispatchResult
            {
                Ok = false,
                CommandId = command.CommandId,
                Message = ex.Message,
                Error = ex.ToString(),
                Event = error
            };
        }
    }

    public MobileBridgeDispatchEvent Publish(MobileBridgeDispatchEvent evt)
    {
        if (evt == null) throw new ArgumentNullException(nameof(evt));
        evt.Sequence = Interlocked.Increment(ref _nextSequence);
        evt.CreatedAt = evt.CreatedAt == default ? DateTimeOffset.UtcNow : evt.CreatedAt;
        evt.EventType = string.IsNullOrWhiteSpace(evt.EventType) ? "BridgeEvent" : evt.EventType;

        lock (_gate)
        {
            _events.Add(evt);
            if (_events.Count > MaxEvents)
            {
                _events.RemoveRange(0, _events.Count - MaxEvents);
            }
        }

        EventPublished?.Invoke(this, evt);
        return evt;
    }

    public MobileBridgeEventBatch GetEventsAfter(long afterSequence, int maxEvents = 50)
    {
        List<MobileBridgeDispatchEvent> copy;
        lock (_gate)
        {
            copy = _events
                .Where(x => x.Sequence > afterSequence)
                .OrderBy(x => x.Sequence)
                .Take(Math.Max(1, Math.Min(100, maxEvents <= 0 ? 50 : maxEvents)))
                .ToList();
        }

        long latest;
        lock (_gate)
        {
            latest = _events.Count == 0 ? Volatile.Read(ref _nextSequence) : Math.Max(Volatile.Read(ref _nextSequence), _events[_events.Count - 1].Sequence);
        }

        return new MobileBridgeEventBatch
        {
            Ok = true,
            AfterSequence = afterSequence,
            LatestSequence = latest,
            Events = copy
        };
    }

    private static string NormalizeCommandType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "RefreshState";
        string compact = value.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
        if (compact.Equals("SelectStar", StringComparison.OrdinalIgnoreCase) || compact.Equals("SelectedStar", StringComparison.OrdinalIgnoreCase)) return "SelectStar";
        if (compact.Equals("SetMet", StringComparison.OrdinalIgnoreCase) || compact.Equals("ToggleMet", StringComparison.OrdinalIgnoreCase)) return "SetMet";
        if (compact.Equals("SetAlly", StringComparison.OrdinalIgnoreCase) || compact.Equals("ToggleAlly", StringComparison.OrdinalIgnoreCase)) return "SetAlly";
        if (compact.Equals("ClearHistory", StringComparison.OrdinalIgnoreCase) || compact.Equals("ClearStarHistory", StringComparison.OrdinalIgnoreCase)) return "ClearHistory";
        if (compact.Equals("ClearAllData", StringComparison.OrdinalIgnoreCase) || compact.Equals("ClearAll", StringComparison.OrdinalIgnoreCase) || compact.Equals("ResetAllStars", StringComparison.OrdinalIgnoreCase)) return "ClearAllData";
        if (compact.Equals("SendPhoto", StringComparison.OrdinalIgnoreCase) || compact.Equals("Photo", StringComparison.OrdinalIgnoreCase) || compact.Equals("SharePhoto", StringComparison.OrdinalIgnoreCase) || compact.Equals("PhotoRequest", StringComparison.OrdinalIgnoreCase)) return "SendPhoto";
        if (compact.Equals("MaybeStarInitiative", StringComparison.OrdinalIgnoreCase) || compact.Equals("StarInitiative", StringComparison.OrdinalIgnoreCase) || compact.Equals("AskStarInitiative", StringComparison.OrdinalIgnoreCase) || compact.Equals("AtRestStarInitiative", StringComparison.OrdinalIgnoreCase)) return "MaybeStarInitiative";
        if (compact.Equals("SetGirlfriendCuteMode", StringComparison.OrdinalIgnoreCase) || compact.Equals("GirlfriendCuteMode", StringComparison.OrdinalIgnoreCase) || compact.Equals("SetCuteMode", StringComparison.OrdinalIgnoreCase) || compact.Equals("CuteMode", StringComparison.OrdinalIgnoreCase)) return "SetGirlfriendCuteMode";
        if (compact.Equals("CuteNow", StringComparison.OrdinalIgnoreCase) || compact.Equals("ActivateCuteModeNow", StringComparison.OrdinalIgnoreCase) || compact.Equals("GirlfriendCuteNow", StringComparison.OrdinalIgnoreCase)) return "ActivateCuteModeNow";
        if (compact.Equals("BreakupGirlfriend", StringComparison.OrdinalIgnoreCase) || compact.Equals("BreakUpGirlfriend", StringComparison.OrdinalIgnoreCase)) return "BreakupGirlfriend";
        if (compact.Equals("Refresh", StringComparison.OrdinalIgnoreCase) || compact.Equals("RefreshState", StringComparison.OrdinalIgnoreCase)) return "RefreshState";
        return value.Trim();
    }
}

public sealed class MobileBridgeDispatchCommand
{
    public string CommandId { get; set; } = string.Empty;
    public string CommandType { get; set; } = "RefreshState";
    public string SourceClientId { get; set; } = "SixStarsMobileAndroid";
    public string SessionId { get; set; } = "android-phone-dev";
    public string StarId { get; set; } = "Elusiaa";
    public bool? IsMet { get; set; }
    public bool? IsAlly { get; set; }
    public bool? CuteModeEnabled { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
    public string PlayerText { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public string Quest { get; set; } = string.Empty;
    public bool RomanceMode { get; set; } = true;
    public bool SpeakOnDesktop { get; set; }
    public bool ReturnAudio { get; set; } = true;
    public string ClientName { get; set; } = "SixStarsMobileAndroid";
    public string PhotoBase64 { get; set; } = string.Empty;
    public string PhotoMimeType { get; set; } = "image/jpeg";
    public string PhotoCaption { get; set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; set; }
}

public sealed class MobileBridgeDispatchResult
{
    public bool Ok { get; set; }
    public string CommandId { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public string StarId { get; set; } = string.Empty;
    public string SelectedStarId { get; set; } = string.Empty;
    public bool IsMet { get; set; }
    public bool IsAlly { get; set; }
    public bool IsGirlfriend { get; set; }
    public bool GirlfriendCuteModeEnabled { get; set; } = true;
    public bool GirlfriendCuteModeActive { get; set; }
    public string GirlfriendCuteModeSignature { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
    public string Transcript { get; set; } = string.Empty;
    public string SpokenText { get; set; } = string.Empty;
    public string StarOutput { get; set; } = string.Empty;
    public string ConversationSummary { get; set; } = string.Empty;
    public string ChangesSummary { get; set; } = string.Empty;
    public bool InitiativeShouldSpeak { get; set; } = true;
    public bool InitiativeSkippedBeforeLlm { get; set; }
    public double InitiativeStateScore { get; set; }
    public double InitiativePriority { get; set; }
    public string InitiativeTopic { get; set; } = string.Empty;
    public string InitiativeReason { get; set; } = string.Empty;
    public bool VoiceRequested { get; set; }
    public bool VoiceSocketConnected { get; set; }
    public int VoiceChunksSent { get; set; }
    public int VoiceBytesSent { get; set; }
    public string VoiceMessage { get; set; } = string.Empty;
    public string VoiceError { get; set; } = string.Empty;
    public string StoryId { get; set; } = string.Empty;
    public string StoryTitle { get; set; } = string.Empty;
    public string StoryMarkdown { get; set; } = string.Empty;
    public string StorySummary { get; set; } = string.Empty;
    public int StoryPartIndex { get; set; }
    public bool StoryIsFinished { get; set; }
    public bool StoryMarkdownReady { get; set; }
    public bool StoryAutoplayPlanned { get; set; }
    public int StoryChunkCount { get; set; }
    public bool PhotoAttached { get; set; }
    public string PhotoSummary { get; set; } = string.Empty;
    public string PhotoError { get; set; } = string.Empty;
    public List<TopAffectedEmotion> TopAffectedEmotions { get; set; } = new List<TopAffectedEmotion>();
    public List<MobileConversationHistoryItem> RecentConversationHistory { get; set; } = new List<MobileConversationHistoryItem>();
    public string EmotionalEffectVector { get; set; } = string.Empty;
    public string NegativeEmotionVector { get; set; } = string.Empty;
    public string LastEmotionVectorsText { get; set; } = string.Empty;
    public string InterstarStatusText { get; set; } = string.Empty;
    public string DailyStateSlotId { get; set; } = string.Empty;
    public string DailyStateLocalDate { get; set; } = string.Empty;
    public DateTimeOffset? DailyStateGeneratedAt { get; set; }
    public string DailyStateSummary { get; set; } = string.Empty;
    public string DailyStateJson { get; set; } = string.Empty;
    public string DailyStateXml { get; set; } = string.Empty;
    public StarState? State { get; set; }
    public MobileBridgeDispatchEvent? Event { get; set; }
}

public sealed class MobileBridgeDispatchEvent
{
    public long Sequence { get; set; }
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public string EventType { get; set; } = "BridgeEvent";
    public string CommandId { get; set; } = string.Empty;
    public string SourceClientId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string StarId { get; set; } = string.Empty;
    public string SelectedStarId { get; set; } = string.Empty;
    public bool IsMet { get; set; }
    public bool IsAlly { get; set; }
    public int Heart { get; set; }
    public int Trust { get; set; }
    public int Romance { get; set; }
    public double EmotionalConnectionPercent { get; set; }
    public bool IsGirlfriendReady { get; set; }
    public bool IsGirlfriend { get; set; }
    public bool GirlfriendCuteModeEnabled { get; set; } = true;
    public bool GirlfriendCuteModeActive { get; set; }
    public string GirlfriendCuteModeSignature { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public string Transcript { get; set; } = string.Empty;
    public string StoryId { get; set; } = string.Empty;
    public string StoryTitle { get; set; } = string.Empty;
    public string StoryMarkdown { get; set; } = string.Empty;
    public string StorySummary { get; set; } = string.Empty;
    public int StoryPartIndex { get; set; }
    public bool StoryIsFinished { get; set; }
    public int StoryChunkCount { get; set; }
    public string SpokenText { get; set; } = string.Empty;
    public bool InitiativeShouldSpeak { get; set; } = true;
    public bool InitiativeSkippedBeforeLlm { get; set; }
    public double InitiativeStateScore { get; set; }
    public double InitiativePriority { get; set; }
    public string InitiativeTopic { get; set; } = string.Empty;
    public string InitiativeReason { get; set; } = string.Empty;
    public bool PhotoAttached { get; set; }
    public string PhotoSummary { get; set; } = string.Empty;
    public string PhotoError { get; set; } = string.Empty;
    public List<TopAffectedEmotion> TopAffectedEmotions { get; set; } = new List<TopAffectedEmotion>();
    public List<MobileConversationHistoryItem> RecentConversationHistory { get; set; } = new List<MobileConversationHistoryItem>();
    public string EmotionalEffectVector { get; set; } = string.Empty;
    public string NegativeEmotionVector { get; set; } = string.Empty;
    public string LastEmotionVectorsText { get; set; } = string.Empty;
    public string InterstarStatusText { get; set; } = string.Empty;
    public string DailyStateSlotId { get; set; } = string.Empty;
    public string DailyStateLocalDate { get; set; } = string.Empty;
    public DateTimeOffset? DailyStateGeneratedAt { get; set; }
    public string DailyStateSummary { get; set; } = string.Empty;
    public string DailyStateJson { get; set; } = string.Empty;
    public string DailyStateXml { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    private static List<MobileConversationHistoryItem> CreateRecentHistoryFromState(StarState? state)
    {
        if (state?.MemoryLog == null) return new List<MobileConversationHistoryItem>();
        return state.MemoryLog
            .Where(IsUsableConversationTurn)
            .OrderByDescending(x => x.CreatedAt)
            .Take(10)
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

    private static bool IsUsableConversationTurn(ConversationLogEntry entry)
    {
        if (entry == null) return false;
        if (string.IsNullOrWhiteSpace(entry.PlayerText)) return false;
        if (string.IsNullOrWhiteSpace(entry.StarResponse)) return false;
        string text = entry.StarResponse;
        return text.IndexOf("thoughts came back in a shape the system could not fully read", StringComparison.OrdinalIgnoreCase) < 0
            && text.IndexOf("answer came back in a strange shape", StringComparison.OrdinalIgnoreCase) < 0
            && !(text.IndexOf("received your words", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("try again", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public static MobileBridgeDispatchEvent FromResult(MobileBridgeDispatchCommand command, MobileBridgeDispatchResult result)
    {
        StarState? state = result.State;
        return new MobileBridgeDispatchEvent
        {
            EventType = result.Ok ? "StarStateChanged" : "BridgeCommandFailed",
            CommandId = command.CommandId,
            SourceClientId = command.SourceClientId,
            SessionId = string.IsNullOrWhiteSpace(command.SessionId) ? "android-phone-dev" : command.SessionId,
            StarId = string.IsNullOrWhiteSpace(result.StarId) ? command.StarId : result.StarId,
            SelectedStarId = result.SelectedStarId,
            IsMet = state?.IsMet ?? result.IsMet,
            IsAlly = state?.IsAlly ?? result.IsAlly,
            IsGirlfriend = state?.IsGirlfriend ?? result.IsGirlfriend,
            GirlfriendCuteModeEnabled = state?.GirlfriendCuteModeEnabled ?? result.GirlfriendCuteModeEnabled,
            GirlfriendCuteModeActive = (state?.GirlfriendCuteModeEnabled ?? result.GirlfriendCuteModeEnabled) && (state?.GirlfriendHourlyState?.CuteModeActive ?? result.GirlfriendCuteModeActive),
            GirlfriendCuteModeSignature = !string.IsNullOrWhiteSpace(result.GirlfriendCuteModeSignature) ? result.GirlfriendCuteModeSignature : (state?.GirlfriendHourlyState?.CuteModeSignature ?? string.Empty),
            Heart = state?.Heart ?? 0,
            Trust = state?.Trust ?? 0,
            Romance = state?.Romance ?? 0,
            EmotionalConnectionPercent = state?.EmotionalConnectionPercent ?? 0,
            IsGirlfriendReady = state?.IsGirlfriendReady ?? false,
            Message = result.Message,
            Error = result.Error,
            Transcript = result.Transcript,
            StoryId = result.StoryId,
            StoryTitle = result.StoryTitle,
            StoryMarkdown = result.StoryMarkdown,
            StorySummary = result.StorySummary,
            StoryPartIndex = result.StoryPartIndex,
            StoryIsFinished = result.StoryIsFinished,
            StoryChunkCount = result.StoryChunkCount,
            SpokenText = result.SpokenText,
            InitiativeShouldSpeak = result.InitiativeShouldSpeak,
            InitiativeSkippedBeforeLlm = result.InitiativeSkippedBeforeLlm,
            InitiativeStateScore = result.InitiativeStateScore,
            InitiativePriority = result.InitiativePriority,
            InitiativeTopic = result.InitiativeTopic ?? string.Empty,
            InitiativeReason = result.InitiativeReason ?? string.Empty,
            PhotoAttached = result.PhotoAttached,
            PhotoSummary = result.PhotoSummary,
            PhotoError = result.PhotoError,
            RecentConversationHistory = result.RecentConversationHistory.Count > 0
                ? result.RecentConversationHistory.ToList()
                : CreateRecentHistoryFromState(state),
            EmotionalEffectVector = !string.IsNullOrWhiteSpace(result.EmotionalEffectVector) ? result.EmotionalEffectVector : (state?.LastEmotionalEffectVector ?? string.Empty),
            NegativeEmotionVector = !string.IsNullOrWhiteSpace(result.NegativeEmotionVector) ? result.NegativeEmotionVector : (state?.LastNegativeEmotionVector ?? string.Empty),
            LastEmotionVectorsText = result.LastEmotionVectorsText ?? string.Empty,
            InterstarStatusText = result.InterstarStatusText ?? string.Empty,
            DailyStateSlotId = !string.IsNullOrWhiteSpace(result.DailyStateSlotId) ? result.DailyStateSlotId : (state?.TodayState?.SlotId ?? string.Empty),
            DailyStateLocalDate = !string.IsNullOrWhiteSpace(result.DailyStateLocalDate) ? result.DailyStateLocalDate : (state?.TodayState?.LocalDate ?? string.Empty),
            DailyStateGeneratedAt = result.DailyStateGeneratedAt ?? state?.TodayState?.GeneratedAt,
            DailyStateSummary = !string.IsNullOrWhiteSpace(result.DailyStateSummary) ? result.DailyStateSummary : (state?.TodayState?.Summary ?? string.Empty),
            DailyStateJson = !string.IsNullOrWhiteSpace(result.DailyStateJson) ? result.DailyStateJson : (state?.TodayState?.Json ?? state?.TodayState?.Xml ?? string.Empty),
            DailyStateXml = string.Empty,
            TopAffectedEmotions = result.TopAffectedEmotions.Count > 0
                ? result.TopAffectedEmotions.ToList()
                : (state?.LastTopAffectedEmotions?.ToList() ?? new List<TopAffectedEmotion>())
        };
    }
}

public sealed class MobileBridgeEventBatch
{
    public bool Ok { get; set; }
    public long AfterSequence { get; set; }
    public long LatestSequence { get; set; }
    public List<MobileBridgeDispatchEvent> Events { get; set; } = new List<MobileBridgeDispatchEvent>();
}
