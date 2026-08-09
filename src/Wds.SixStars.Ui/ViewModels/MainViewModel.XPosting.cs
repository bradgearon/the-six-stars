using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Wds.SixStars.Api.Mobile;
using Wds.SixStars.Api.Writings;
using Wds.SixStars.Api.XPosting;
using Wds.SixStars.Ui.Infrastructure;

namespace Wds.SixStars.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private readonly object _xPostingLoopGate = new object();
    private readonly Dictionary<string, DateTimeOffset> _xPostingLastAttemptAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _xPostingScheduledInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _xPostingLoopCts;
    private Task? _xPostingLoopTask;
    private bool _xPostingEnabled;
    private string _xPostingStatus = "Automatic X posting is off.";

    public ICommand AuthorizeSelectedStarForXCommand { get; private set; } = null!;
    public ICommand PostSelectedStarToXNowCommand { get; private set; } = null!;

    public bool XPostingEnabled
    {
        get => _xPostingEnabled;
        set
        {
            if (!SetProperty(ref _xPostingEnabled, value)) return;
            if (value)
            {
                SetXPostingStatus("Automatic X posting is enabled. Waiting for the current daily slot.");
                EnsureXPostingLoopStarted();
            }
            else
            {
                StopXPostingLoop("Automatic X posting is off. Any five-post batch already in progress will finish.");
            }
            PublishXPostingStatusEvent("XPostingEnabledChanged", SelectedStarId, XPostingStatus);
        }
    }

    public string XPostingStatus
    {
        get => _xPostingStatus;
        private set => SetProperty(ref _xPostingStatus, value);
    }

    private void InitializeXPostingLoop()
    {
        AuthorizeSelectedStarForXCommand = new RelayCommand(parameter => { _ = AuthorizeSelectedStarForXAsync(); });
        PostSelectedStarToXNowCommand = new RelayCommand(_ => QueueManualXPosting(SelectedStarId));
        _xPostingEnabled = _xPostingOptions.Enabled;
        if (_xPostingEnabled)
        {
            SetXPostingStatus("Automatic X posting is enabled. Waiting for the current daily slot.");
            EnsureXPostingLoopStarted();
        }
    }


    private async Task AuthorizeSelectedStarForXAsync()
    {
        string starId = ResolveDefaultStarId(SelectedStarId);
        StarXAccountOptions? account = (_xPostingOptions.StarAccounts ?? new List<StarXAccountOptions>())
            .FirstOrDefault(x => x != null && string.Equals(x.Id, starId, StringComparison.OrdinalIgnoreCase));
        if (account == null || !account.Enabled)
        {
            SetXPostingStatus("No enabled XPosting.StarAccounts entry matches " + starId + ".");
            return;
        }
        if (!account.HasClientConfiguration)
        {
            SetXPostingStatus("Set ClientId and the exact loopback RedirectUri for " + starId + " before authorizing.");
            return;
        }

        SetXPostingStatus("Opening X authorization for " + starId + ". Sign into the correct Star account and approve write access.");
        StarXOAuth2AuthorizationResult result = await _xOAuth2AuthorizationService.AuthorizeAsync(
            account,
            _xPostingOptions.OAuthScopes,
            _xPostingOptions.OAuthAuthorizationTimeoutSeconds,
            CancellationToken.None).ConfigureAwait(false);
        SetXPostingStatus(result.Message);
        PublishXPostingStatusEvent(result.Success ? "StarXAuthorizationCompleted" : "StarXAuthorizationFailed", starId, result.Message);
    }

    private void QueueManualXPosting(string starId)
    {
        string resolved = ResolveDefaultStarId(starId);
        SetXPostingStatus("Queued an uninterrupted five-post X batch for " + resolved + ".");
        PublishXPostingStatusEvent("StarXPostingQueued", resolved, XPostingStatus);
        _ = Task.Run(async () =>
        {
            await RunXPostingForStarAsync(resolved, null, null, false).ConfigureAwait(false);
        });
    }

    private void EnsureXPostingLoopStarted()
    {
        lock (_xPostingLoopGate)
        {
            if (!_xPostingEnabled) return;
            if (_xPostingLoopTask != null && !_xPostingLoopTask.IsCompleted) return;
            _xPostingLoopCts?.Dispose();
            _xPostingLoopCts = new CancellationTokenSource();
            CancellationToken token = _xPostingLoopCts.Token;
            _xPostingLoopTask = Task.Run(() => RunXPostingLoopAsync(token));
        }
    }

    private async Task RunXPostingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!_xPostingEnabled) return;
                DateTimeOffset localNow = DateTimeOffset.Now;
                StarXPostingScheduleSlot? slot = ResolveXPostingSlot(localNow);
                if (slot != null)
                {
                    string localDate = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    foreach (string starAccountId in _xPostingService.ConfiguredStarIds)
                    {
                        if (cancellationToken.IsCancellationRequested || !_xPostingEnabled) return;
                        string starId = ResolveDefaultStarId(starAccountId);
                        string attemptKey = localDate + "|" + slot.SlotId + "|" + starId;
                        if (ShouldWaitBeforeXPostingRetry(attemptKey, localNow)) continue;
                        if (await _xPostingScheduleStore.IsCompletedAsync(localDate, slot.SlotId, starId, cancellationToken).ConfigureAwait(false)) continue;

                        if (!TryBeginScheduledXPostingAttempt(attemptKey, localNow)) continue;
                        try
                        {
                            await RunXPostingForStarAsync(starId, localDate, slot.SlotId, true).ConfigureAwait(false);
                        }
                        finally
                        {
                            lock (_xPostingLoopGate) _xPostingScheduledInFlight.Remove(attemptKey);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SetXPostingStatus("Automatic X posting loop error: " + ex.Message + ". It will keep checking.");
                PublishXPostingStatusEvent("StarXPostingFailed", SelectedStarId, XPostingStatus);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _xPostingOptions.PollSeconds)), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private bool ShouldWaitBeforeXPostingRetry(string key, DateTimeOffset now)
    {
        lock (_xPostingLoopGate)
        {
            return _xPostingLastAttemptAt.TryGetValue(key, out DateTimeOffset last)
                && now - last < TimeSpan.FromSeconds(Math.Max(30, _xPostingOptions.RetryDelaySeconds));
        }
    }

    private bool TryBeginScheduledXPostingAttempt(string key, DateTimeOffset now)
    {
        lock (_xPostingLoopGate)
        {
            if (_xPostingScheduledInFlight.Contains(key)) return false;
            _xPostingScheduledInFlight.Add(key);
            _xPostingLastAttemptAt[key] = now;
            return true;
        }
    }

    private async Task RunXPostingForStarAsync(string starId, string? localDate, string? slotId, bool scheduled)
    {
        try
        {
            string prefix = scheduled && !string.IsNullOrWhiteSpace(slotId)
                ? starId + " is selecting five writings for the " + slotId + " X batch."
                : starId + " is selecting five writings for X now.";
            SetXPostingStatus(prefix + " Once posting starts, foreground activity will not interrupt it.");
            PublishXPostingStatusEvent("StarXPostingStarted", starId, XPostingStatus);

            StarXPostingRunResult result = await _xPostingService.PostForStarAsync(starId).ConfigureAwait(false);
            SetXPostingStatus(result.Message);

            if (scheduled && result.BatchCompleted && !string.IsNullOrWhiteSpace(localDate) && !string.IsNullOrWhiteSpace(slotId))
            {
                await _xPostingScheduleStore.RecordCompletedAsync(
                    localDate,
                    slotId,
                    starId,
                    result.PostedCount,
                    result.FailedCount,
                    CancellationToken.None).ConfigureAwait(false);
            }

            StarWritingNotesState state = await _writingService.GetStateAsync(starId, CancellationToken.None).ConfigureAwait(false);
            OnUiThread(() =>
            {
                if (string.Equals(ResolveDefaultStarId(SelectedStarId), starId, StringComparison.OrdinalIgnoreCase))
                {
                    PendingWritingCount = state.PendingCount;
                    PersistedWritingCount = state.PersistedWritings?.Count ?? 0;
                }
            });
            PublishXPostingStatusEvent(result.BatchCompleted ? "StarXPostingCompleted" : "StarXPostingDeferred", starId, result.Message, result, state);
        }
        catch (Exception ex)
        {
            SetXPostingStatus("X posting failed for " + starId + ": " + ex.Message + ". Unposted writings remain eligible.");
            PublishXPostingStatusEvent("StarXPostingFailed", starId, XPostingStatus);
        }
    }

    private StarXPostingScheduleSlot? ResolveXPostingSlot(DateTimeOffset localNow)
    {
        TimeSpan now = localNow.TimeOfDay;
        foreach (StarXPostingScheduleSlot slot in _xPostingOptions.RefreshSlots ?? StarXPostingOptions.CreateDefaultSlots())
        {
            if (!TryParseXPostingTime(slot.StartsAt, out TimeSpan start) || !TryParseXPostingTime(slot.RefreshBefore, out TimeSpan end)) continue;
            bool contains = end > start
                ? now >= start && now < end
                : now >= start || now < end;
            if (contains) return slot;
        }
        return null;
    }

    private static bool TryParseXPostingTime(string? value, out TimeSpan result)
    {
        return TimeSpan.TryParseExact(value ?? string.Empty, @"hh\:mm", CultureInfo.InvariantCulture, out result)
            || TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out result);
    }

    private void StopXPostingLoop(string status)
    {
        CancellationTokenSource? cts;
        lock (_xPostingLoopGate)
        {
            cts = _xPostingLoopCts;
            _xPostingLoopCts = null;
            _xPostingLoopTask = null;
        }
        try { cts?.Cancel(); } catch { }
        cts?.Dispose();
        SetXPostingStatus(status);
    }

    private void DisposeXPostingLoop()
    {
        _xPostingEnabled = false;
        StopXPostingLoop("X posting scheduler stopped because the app is closing. A batch already accepted by X may finish before process exit.");
    }

    private void SetXPostingStatus(string message)
    {
        OnUiThread(() => XPostingStatus = string.IsNullOrWhiteSpace(message) ? "X posting status unavailable." : message.Trim());
    }

    private void PublishXPostingStatusEvent(
        string eventType,
        string starId,
        string message,
        StarXPostingRunResult? result = null,
        StarWritingNotesState? writingState = null)
    {
        try
        {
            _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
            {
                EventType = eventType,
                SourceClientId = "desktop-x-posting",
                SessionId = DefaultSessionId,
                StarId = ResolveDefaultStarId(starId),
                SelectedStarId = SelectedStarId,
                XPostingEnabled = XPostingEnabled,
                XPostingStatus = message,
                XPostingAttemptedCount = result?.AttemptedCount ?? 0,
                XPostingPostedCount = result?.PostedCount ?? 0,
                XPostingFailedCount = result?.FailedCount ?? 0,
                PendingWritingCount = writingState?.PendingCount,
                PersistedWritingCount = writingState?.PersistedWritings?.Count,
                UnpostedWritingCount = writingState?.UnpostedCount,
                Message = message
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[XPosting] Could not publish mobile event: " + ex.Message);
        }
    }
}
