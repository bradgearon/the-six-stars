using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Wds.SixStars.Api.Writings;
using Wds.SixStars.Ui.Infrastructure;

namespace Wds.SixStars.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private static readonly string[] BackgroundWritingStarOrder =
    {
        "Elusiaa",
        "Kaelia",
        "Maera",
        "Khavia",
        "Auerlis",
        "Nyxari"
    };

    private static readonly TimeSpan BackgroundWritingIdleDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BackgroundWritingPollDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BackgroundWritingBetweenStarsDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BackgroundWritingErrorDelay = TimeSpan.FromSeconds(5);

    private readonly object _backgroundWritingLoopGate = new object();
    private CancellationTokenSource? _backgroundWritingLoopCts;
    private CancellationTokenSource? _backgroundWritingIterationCts;
    private Task? _backgroundWritingLoopTask;
    private DateTimeOffset _lastForegroundStarActivityAt = DateTimeOffset.UtcNow;
    private bool _backgroundWritingLoopEnabled;
    private bool _backgroundWritingStartNowRequested;
    private long _backgroundWritingActivityVersion;
    private int _backgroundWritingForegroundLeaseCount;
    private int _backgroundWritingNextStarIndex;
    private string _backgroundWritingLoopStatus = "Background writing loop is off.";

    public ICommand StartWritingLoopNowCommand { get; private set; } = null!;

    public bool BackgroundWritingLoopEnabled
    {
        get => _backgroundWritingLoopEnabled;
        set
        {
            if (!SetProperty(ref _backgroundWritingLoopEnabled, value))
            {
                return;
            }

            if (value)
            {
                lock (_backgroundWritingLoopGate)
                {
                    _lastForegroundStarActivityAt = DateTimeOffset.UtcNow;
                    _backgroundWritingStartNowRequested = false;
                    _backgroundWritingNextStarIndex = ResolveBackgroundWritingStartIndex(SelectedStarId);
                }

                SetBackgroundWritingLoopStatus("Enabled. Waiting for a few idle seconds before generating for " + BackgroundWritingStarOrder[_backgroundWritingNextStarIndex] + ".");
                EnsureBackgroundWritingLoopStarted();
            }
            else
            {
                StopBackgroundWritingLoop("Background writing loop is off.");
            }
        }
    }

    public string BackgroundWritingLoopStatus
    {
        get => _backgroundWritingLoopStatus;
        private set => SetProperty(ref _backgroundWritingLoopStatus, value);
    }

    private void InitializeBackgroundWritingLoop()
    {
        StartWritingLoopNowCommand = new RelayCommand(_ => StartBackgroundWritingLoopNow());
    }

    private void StartBackgroundWritingLoopNow()
    {
        if (!BackgroundWritingLoopEnabled)
        {
            BackgroundWritingLoopEnabled = true;
        }

        lock (_backgroundWritingLoopGate)
        {
            _backgroundWritingStartNowRequested = true;
            _backgroundWritingNextStarIndex = ResolveBackgroundWritingStartIndex(SelectedStarId);
        }

        SetBackgroundWritingLoopStatus("Start-now requested. The loop will begin with " + BackgroundWritingStarOrder[_backgroundWritingNextStarIndex] + " as soon as foreground conversation is clear.");
        EnsureBackgroundWritingLoopStarted();
    }

    private void EnsureBackgroundWritingLoopStarted()
    {
        lock (_backgroundWritingLoopGate)
        {
            if (!_backgroundWritingLoopEnabled)
            {
                return;
            }

            if (_backgroundWritingLoopTask != null && !_backgroundWritingLoopTask.IsCompleted)
            {
                return;
            }

            _backgroundWritingLoopCts?.Dispose();
            _backgroundWritingLoopCts = new CancellationTokenSource();
            CancellationToken token = _backgroundWritingLoopCts.Token;
            _backgroundWritingLoopTask = Task.Run(() => RunBackgroundWritingLoopAsync(token), token);
        }
    }

    private async Task RunBackgroundWritingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool enabled;
            bool startNow;
            DateTimeOffset lastForegroundActivity;
            long activityVersion;
            int nextStarIndex;

            lock (_backgroundWritingLoopGate)
            {
                enabled = _backgroundWritingLoopEnabled;
                startNow = _backgroundWritingStartNowRequested;
                lastForegroundActivity = _lastForegroundStarActivityAt;
                activityVersion = _backgroundWritingActivityVersion;
                nextStarIndex = NormalizeBackgroundWritingIndex(_backgroundWritingNextStarIndex);
            }

            if (!enabled)
            {
                return;
            }

            if (IsForegroundStarInteractionActive())
            {
                SetBackgroundWritingLoopStatus("Paused while the player or a Star reply is using the runtime.");
                await Task.Delay(BackgroundWritingPollDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            TimeSpan idleFor = DateTimeOffset.UtcNow - lastForegroundActivity;
            if (!startNow && idleFor < BackgroundWritingIdleDelay)
            {
                SetBackgroundWritingLoopStatus("Waiting for a few idle seconds before generating for " + BackgroundWritingStarOrder[nextStarIndex] + ".");
                await Task.Delay(BackgroundWritingPollDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            lock (_backgroundWritingLoopGate)
            {
                _backgroundWritingStartNowRequested = false;
                nextStarIndex = NormalizeBackgroundWritingIndex(_backgroundWritingNextStarIndex);
            }

            string starId = BackgroundWritingStarOrder[nextStarIndex];
            using CancellationTokenSource iterationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bool activityChangedBeforeStart;
            lock (_backgroundWritingLoopGate)
            {
                _backgroundWritingIterationCts = iterationCts;
                activityChangedBeforeStart = _backgroundWritingActivityVersion != activityVersion;
            }

            if (activityChangedBeforeStart || IsForegroundStarInteractionActive())
            {
                lock (_backgroundWritingLoopGate)
                {
                    if (ReferenceEquals(_backgroundWritingIterationCts, iterationCts))
                    {
                        _backgroundWritingIterationCts = null;
                    }
                }
                SetBackgroundWritingLoopStatus("Background writing paused for player activity. It will resume after a few idle seconds.");
                await Task.Delay(BackgroundWritingPollDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            bool completed = false;
            bool failed = false;
            try
            {
                SetBackgroundWritingLoopStatus("Generating five writings and running the configured refinement passes for " + starId + " in the background...");
                StarWritingCreateResult result = await _writingService.GenerateAndReviewAsync(starId, iterationCts.Token).ConfigureAwait(false);
                completed = true;

                OnUiThread(() =>
                {
                    if (string.Equals(ResolveDefaultStarId(SelectedStarId), starId, StringComparison.OrdinalIgnoreCase))
                    {
                        PendingWritingCount = result.State.PendingCount;
                        PersistedWritingCount = result.State.PersistedWritings?.Count ?? 0;
                    }
                });

                string message = "Background writing: " + starId + " used " + result.WritingMethod + " and completed " + result.CompletedPassCount + " of " + result.SelectedPassCount + " passes with " + result.AppliedImprovementCount + " accepted replacements; " + result.PersistedCount + " writings were saved. " + result.State.PendingCount + " are waiting to be told.";
                SetBackgroundWritingLoopStatus(message);
                await PublishWritingShelfEventAsync(
                    "StarWritingBackgroundGenerated",
                    starId,
                    result.State,
                    result.Batch.Items.Select(x => x.Writing).ToList(),
                    message).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                SetBackgroundWritingLoopStatus("Background writing paused for player activity. It will resume after a few idle seconds.");
            }
            catch (Exception ex)
            {
                completed = true;
                failed = true;
                SetBackgroundWritingLoopStatus("Background writing failed for " + starId + ": " + ex.Message + ". The loop will continue with the next Star.");
                System.Diagnostics.Debug.WriteLine("[WritingLoop] " + starId + " failed: " + ex);
            }
            finally
            {
                lock (_backgroundWritingLoopGate)
                {
                    if (ReferenceEquals(_backgroundWritingIterationCts, iterationCts))
                    {
                        _backgroundWritingIterationCts = null;
                    }

                    if (completed)
                    {
                        _backgroundWritingNextStarIndex = NormalizeBackgroundWritingIndex(nextStarIndex + 1);
                    }
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            TimeSpan delay = failed
                ? BackgroundWritingErrorDelay
                : completed
                    ? BackgroundWritingBetweenStarsDelay
                    : BackgroundWritingPollDelay;

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsForegroundStarInteractionActive()
    {
        return _isProcessingRuntime
            || _isRecording
            || _services.StorySession.AutoContinue
            || Volatile.Read(ref _backgroundWritingForegroundLeaseCount) > 0;
    }

    private IDisposable BeginBackgroundWritingForegroundActivity(string reason)
    {
        Interlocked.Increment(ref _backgroundWritingForegroundLeaseCount);
        NotifyBackgroundWritingLoopForegroundActivity(reason);
        return new BackgroundWritingForegroundLease(this, reason);
    }

    private void EndBackgroundWritingForegroundActivity(string reason)
    {
        int remaining = Interlocked.Decrement(ref _backgroundWritingForegroundLeaseCount);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _backgroundWritingForegroundLeaseCount, 0);
            remaining = 0;
        }

        if (remaining == 0)
        {
            NotifyBackgroundWritingLoopForegroundActivity("the end of " + reason, cancelCurrentIteration: false);
        }
    }

    private void NotifyBackgroundWritingLoopForegroundActivity(string reason, bool cancelCurrentIteration = true)
    {
        CancellationTokenSource? iterationToCancel = null;
        bool enabled;

        lock (_backgroundWritingLoopGate)
        {
            _lastForegroundStarActivityAt = DateTimeOffset.UtcNow;
            _backgroundWritingActivityVersion++;
            if (cancelCurrentIteration)
            {
                _backgroundWritingStartNowRequested = false;
                iterationToCancel = _backgroundWritingIterationCts;
            }
            enabled = _backgroundWritingLoopEnabled;
        }

        if (iterationToCancel != null)
        {
            try
            {
                iterationToCancel.Cancel();
            }
            catch
            {
                // Best-effort preemption. The writing service releases its gate when cancellation settles.
            }
        }

        if (enabled)
        {
            SetBackgroundWritingLoopStatus("Paused for " + reason + ". It will resume after a few idle seconds.");
        }
    }

    private void StopBackgroundWritingLoop(string status)
    {
        CancellationTokenSource? loopToCancel;
        CancellationTokenSource? iterationToCancel;

        lock (_backgroundWritingLoopGate)
        {
            _backgroundWritingStartNowRequested = false;
            loopToCancel = _backgroundWritingLoopCts;
            iterationToCancel = _backgroundWritingIterationCts;
            _backgroundWritingLoopCts = null;
            _backgroundWritingIterationCts = null;
            _backgroundWritingLoopTask = null;
        }

        try
        {
            iterationToCancel?.Cancel();
        }
        catch
        {
        }

        try
        {
            loopToCancel?.Cancel();
        }
        catch
        {
        }

        SetBackgroundWritingLoopStatus(status);
    }

    private void DisposeBackgroundWritingLoop()
    {
        _backgroundWritingLoopEnabled = false;
        StopBackgroundWritingLoop("Background writing loop stopped because the app is closing.");
    }

    private void SetBackgroundWritingLoopStatus(string status)
    {
        OnUiThread(() => BackgroundWritingLoopStatus = status);
    }


    private sealed class BackgroundWritingForegroundLease : IDisposable
    {
        private MainViewModel? _owner;
        private readonly string _reason;

        public BackgroundWritingForegroundLease(MainViewModel owner, string reason)
        {
            _owner = owner;
            _reason = reason;
        }

        public void Dispose()
        {
            MainViewModel? owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndBackgroundWritingForegroundActivity(_reason);
        }
    }

    private static int NormalizeBackgroundWritingIndex(int index)
    {
        int count = BackgroundWritingStarOrder.Length;
        if (count <= 0)
        {
            return 0;
        }

        int normalized = index % count;
        return normalized < 0 ? normalized + count : normalized;
    }

    private static int ResolveBackgroundWritingStartIndex(string? starId)
    {
        int index = Array.FindIndex(
            BackgroundWritingStarOrder,
            candidate => string.Equals(candidate, starId, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
    }
}
