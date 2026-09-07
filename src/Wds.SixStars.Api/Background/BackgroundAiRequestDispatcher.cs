using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Background
{
    /// <summary>
    /// API-owned background AI dispatcher for durable state work that should never compete with
    /// foreground player replies. Foreground calls enter a lease; background stacks are queued,
    /// ordered by priority, and only started while no foreground reply is active.
    /// </summary>
    public sealed class BackgroundAiRequestDispatcher : IDisposable
    {
        private readonly object _gate = new object();
        private readonly List<QueuedBackgroundAiRequest> _queue = new List<QueuedBackgroundAiRequest>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();
        private Task? _workerTask;
        private long _nextSequence;
        private int _foregroundDepth;
        private CancellationTokenSource? _activeRequestCts;
        private string _activeRequestId = string.Empty;
        private string _activeRequestStackName = string.Empty;
        private bool _activeRequestAllowsForegroundPreemption = true;
        private bool _activeRequestAllowsPlayerActivityPreemption = true;
        private DateTimeOffset _lastForegroundFinishedAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _backgroundSuppressedUntilUtc = DateTimeOffset.MinValue;
        private bool _disposed;
        private const int MaxDelayWakeSeconds = 15;
        // Build 15N: keep the local model truly foreground-first. The app serializes all LLM calls
        // through one client request gate, so background work restarting a few seconds after a reply
        // can destroy the foreground prompt cache and collide with the player's next turn.
        private const int HighPriorityForegroundQuietSeconds = 30;
        private const int NormalPriorityForegroundQuietSeconds = 45;
        private const int LowPriorityForegroundQuietSeconds = 90;

        public event EventHandler<BackgroundAiRequestStatusEventArgs>? StatusChanged;

        public bool HasForegroundRequestActive
        {
            get { lock (_gate) return _foregroundDepth > 0; }
        }

        public int QueuedCount
        {
            get { lock (_gate) return _queue.Count; }
        }

        public bool IsShuttingDown
        {
            get { lock (_gate) return _disposed || _shutdownCts.IsCancellationRequested; }
        }

        public IDisposable EnterForegroundRequest(string reason)
        {
            string activeRequestId;
            string activeStackName;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                _foregroundDepth++;
                activeRequestId = _activeRequestId;
                activeStackName = _activeRequestStackName;

                // Once the actual foreground request begins, any quiet pulse that came from the
                // touch/type/send action which launched this same request is stale. The foreground
                // lease itself now protects the model. A NEW PlayerActivity ping received while the
                // Star is answering will extend suppression again and is still respected afterward.
                if (_backgroundSuppressedUntilUtc > DateTimeOffset.UtcNow)
                {
                    _backgroundSuppressedUntilUtc = DateTimeOffset.UtcNow;
                }
            }

            // Foreground owns the model. Ordinary background work is cancelled outright; durable
            // primary memory-review compute is also cancelled here, but its runtime callback
            // immediately requeues the immutable review job with the same durable ordering key.
            if (!string.IsNullOrWhiteSpace(activeRequestId))
            {
                bool cancelled = CancelActiveRequest("Foreground request preempted active background generation: " + (reason ?? string.Empty));
                Publish(
                    cancelled ? "ForegroundPreempting" : "ForegroundPreemptionSkipped",
                    "foreground",
                    activeRequestId,
                    cancelled
                        ? "Foreground work cancelled the active background model call and is taking priority."
                        : "Foreground work did not cancel non-preemptible background stack '" + activeStackName + "'.",
                    QueuedCount);
            }

            Publish("ForegroundStarted", "foreground", reason ?? string.Empty, "A foreground reply request is active; queued background AI stacks will wait.", 0);
            SafeReleaseSignal();
            return new ForegroundLease(this, reason ?? string.Empty);
        }

        public bool CancelActiveRequest(string reason)
        {
            CancellationTokenSource? activeToCancel;
            string activeRequestId;
            string activeStackName;
            bool allowsForegroundPreemption;
            lock (_gate)
            {
                activeToCancel = _activeRequestCts;
                activeRequestId = _activeRequestId;
                activeStackName = _activeRequestStackName;
                allowsForegroundPreemption = _activeRequestAllowsForegroundPreemption;
            }

            if (activeToCancel == null || activeToCancel.IsCancellationRequested) return false;
            if (!allowsForegroundPreemption)
            {
                Publish(
                    "ForegroundPreemptionSkippedNonPreemptible",
                    activeStackName,
                    activeRequestId,
                    "Cancellation request was ignored because this background stack is explicitly non-preemptible. " + (reason ?? string.Empty),
                    QueuedCount);
                return false;
            }

            try { activeToCancel.Cancel(); } catch { return false; }
            Publish("Preempted", activeStackName, activeRequestId, reason ?? "Background request cancelled.", QueuedCount);
            return true;
        }

        /// <summary>
        /// Records lightweight player activity without creating a full foreground lease. Background
        /// requests stay queued until the quiet window expires, and an already-running background
        /// model request is cancelled once so repeated touch/typing pings cannot create a
        /// cancel/restart storm while the player is preparing the next message.
        /// </summary>
        public void SuppressBackgroundFor(TimeSpan duration, string reason)
        {
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

            CancellationTokenSource? activeToCancel;
            string activeRequestId;
            string activeStackName;
            bool activeAllowsPlayerActivityPreemption;
            DateTimeOffset suppressedUntil;
            bool foregroundAlreadyActive;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                foregroundAlreadyActive = _foregroundDepth > 0;

                // A live foreground lease already prevents every queued background request from
                // starting. Do not turn touch/type pings that arrive DURING that request into an
                // extra 30-second tail after it. This also makes an asynchronously delivered
                // "Android text message" activity ping harmless if the real request won the race.
                // Once the foreground lease ends, fresh player activity again extends suppression.
                if (!foregroundAlreadyActive)
                {
                    DateTimeOffset requestedUntil = DateTimeOffset.UtcNow.Add(duration);
                    if (requestedUntil > _backgroundSuppressedUntilUtc)
                    {
                        _backgroundSuppressedUntilUtc = requestedUntil;
                    }
                }

                suppressedUntil = _backgroundSuppressedUntilUtc;
                activeToCancel = _activeRequestCts;
                activeRequestId = _activeRequestId;
                activeStackName = _activeRequestStackName;
                activeAllowsPlayerActivityPreemption = _activeRequestAllowsPlayerActivityPreemption;
            }

            bool cancelledActive = false;
            bool protectedActive = activeToCancel != null && !activeAllowsPlayerActivityPreemption;
            if (activeToCancel != null && !activeToCancel.IsCancellationRequested && activeAllowsPlayerActivityPreemption)
            {
                try
                {
                    activeToCancel.Cancel();
                    cancelledActive = true;
                }
                catch
                {
                }
            }

            Publish(
                "ForegroundActivitySuppressed",
                "foreground-activity",
                activeRequestId,
                protectedActive
                    ? "PlayerActivity left foreground-only-preemptible background stack '" + activeStackName + "' running; suppression applies only to cancellable/queued background work. " + (reason ?? string.Empty)
                    : foregroundAlreadyActive
                        ? "Foreground request is already active, so this activity ping does not add a post-reply suppression tail. " + (reason ?? string.Empty)
                        : (cancelledActive ? "Cancelled the active cancellable background model call once and " : string.Empty)
                            + "held ordinary queued background AI until " + suppressedUntil.ToString("O") + ". " + (reason ?? string.Empty),
                QueuedCount);
            SafeReleaseSignal();
        }

        public BackgroundAiRequestReceipt Enqueue(
            string stackName,
            string description,
            BackgroundAiRequestPriority priority,
            Func<CancellationToken, Task> executeAsync,
            CancellationToken cancellationToken = default(CancellationToken),
            TimeSpan? delay = null,
            bool bypassPostForegroundQuiet = false,
            bool allowForegroundPreemption = true,
            bool bypassPlayerActivitySuppression = false,
            bool allowPlayerActivityPreemption = true,
            long? orderingSequence = null)
        {
            if (executeAsync == null) throw new ArgumentNullException(nameof(executeAsync));
            string id = Guid.NewGuid().ToString("N");
            long sequence;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                sequence = ++_nextSequence;
                _queue.Add(new QueuedBackgroundAiRequest
                {
                    Id = id,
                    StackName = string.IsNullOrWhiteSpace(stackName) ? "BackgroundAi" : stackName.Trim(),
                    Description = description ?? string.Empty,
                    Priority = priority,
                    Sequence = sequence,
                    OrderSequence = orderingSequence.GetValueOrDefault(sequence),
                    EnqueuedAt = DateTimeOffset.UtcNow,
                    NotBefore = DateTimeOffset.UtcNow.Add(delay.GetValueOrDefault(TimeSpan.Zero)),
                    ExecuteAsync = executeAsync,
                    CancellationToken = cancellationToken,
                    BypassPostForegroundQuiet = bypassPostForegroundQuiet,
                    AllowForegroundPreemption = allowForegroundPreemption,
                    BypassPlayerActivitySuppression = bypassPlayerActivitySuppression,
                    AllowPlayerActivityPreemption = allowPlayerActivityPreemption
                });
                EnsureWorkerLocked();
            }

            Publish("Queued", stackName, id, description ?? string.Empty, QueuedCount);
            SafeReleaseSignal();
            return new BackgroundAiRequestReceipt
            {
                RequestId = id,
                StackName = string.IsNullOrWhiteSpace(stackName) ? "BackgroundAi" : stackName.Trim(),
                Priority = priority,
                Sequence = sequence,
                OrderSequence = orderingSequence.GetValueOrDefault(sequence)
            };
        }

        private async Task RunWorkerAsync()
        {
            CancellationToken shutdownToken = _shutdownCts.Token;
            while (!shutdownToken.IsCancellationRequested)
            {
                QueuedBackgroundAiRequest? request = null;
                TimeSpan? wait = null;

                lock (_gate)
                {
                    if (_disposed) return;
                    if (_foregroundDepth <= 0)
                    {
                        DateTimeOffset now = DateTimeOffset.UtcNow;
                        request = _queue
                            .Where(x => GetEffectiveNotBeforeLocked(x) <= now)
                            .OrderBy(x => (int)x.Priority)
                            .ThenBy(x => x.OrderSequence)
                            .ThenBy(x => x.Sequence)
                            .FirstOrDefault();

                        if (request != null)
                        {
                            _queue.Remove(request);
                        }
                        else if (_queue.Count > 0)
                        {
                            DateTimeOffset next = _queue.Min(GetEffectiveNotBeforeLocked);
                            TimeSpan untilNext = next <= now ? TimeSpan.FromMilliseconds(100) : next - now;
                            if (untilNext > TimeSpan.FromSeconds(MaxDelayWakeSeconds)) untilNext = TimeSpan.FromSeconds(MaxDelayWakeSeconds);
                            if (untilNext < TimeSpan.FromMilliseconds(100)) untilNext = TimeSpan.FromMilliseconds(100);
                            wait = untilNext;
                        }
                    }
                }

                if (request == null)
                {
                    try
                    {
                        if (wait.HasValue)
                        {
                            await Task.Delay(wait.Value, shutdownToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await _signal.WaitAsync(shutdownToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    continue;
                }

                if (request.CancellationToken.IsCancellationRequested)
                {
                    Publish("SkippedCancelled", request.StackName, request.Id, request.Description, QueuedCount);
                    continue;
                }

                await WaitForForegroundIdleAsync(shutdownToken).ConfigureAwait(false);

                CancellationTokenSource? linked = null;
                try
                {
                    linked = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, request.CancellationToken);
                    bool foregroundWonRace = false;
                    lock (_gate)
                    {
                        // A foreground lease can begin in the tiny gap after WaitForForegroundIdleAsync
                        // returns but before this background request publishes itself as active. Never
                        // let that race sneak background inference onto the model after player activity.
                        if (_foregroundDepth > 0 || (!request.BypassPlayerActivitySuppression && _backgroundSuppressedUntilUtc > DateTimeOffset.UtcNow))
                        {
                            _queue.Add(request);
                            foregroundWonRace = true;
                        }
                        else
                        {
                            _activeRequestCts = linked;
                            _activeRequestId = request.Id;
                            _activeRequestStackName = request.StackName;
                            _activeRequestAllowsForegroundPreemption = request.AllowForegroundPreemption;
                            _activeRequestAllowsPlayerActivityPreemption = request.AllowPlayerActivityPreemption;
                        }
                    }
                    if (foregroundWonRace)
                    {
                        Publish("DeferredForForeground", request.StackName, request.Id, "Foreground activity/quiet suppression won the start race; background request returned to the queue.", QueuedCount);
                        SafeReleaseSignal();
                        continue;
                    }
                    Publish("Started", request.StackName, request.Id, request.Description, QueuedCount);
                    await request.ExecuteAsync(linked.Token).ConfigureAwait(false);
                    Publish("Completed", request.StackName, request.Id, request.Description, QueuedCount);
                }
                catch (OperationCanceledException)
                {
                    Publish("Cancelled", request.StackName, request.Id, request.Description, QueuedCount);
                }
                catch (Exception ex)
                {
                    Publish("Failed", request.StackName, request.Id, ex.Message, QueuedCount);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_activeRequestCts, linked))
                        {
                            _activeRequestCts = null;
                            _activeRequestId = string.Empty;
                            _activeRequestStackName = string.Empty;
                            _activeRequestAllowsForegroundPreemption = true;
                            _activeRequestAllowsPlayerActivityPreemption = true;
                        }
                    }
                    linked?.Dispose();
                }
            }
        }


        private DateTimeOffset GetEffectiveNotBeforeLocked(QueuedBackgroundAiRequest request)
        {
            DateTimeOffset foregroundQuietUntil = request.BypassPostForegroundQuiet || _lastForegroundFinishedAtUtc == DateTimeOffset.MinValue
                ? DateTimeOffset.MinValue
                : _lastForegroundFinishedAtUtc.AddSeconds(GetForegroundQuietSeconds(request.Priority));
            DateTimeOffset effective = request.NotBefore >= foregroundQuietUntil ? request.NotBefore : foregroundQuietUntil;
            if (!request.BypassPlayerActivitySuppression && _backgroundSuppressedUntilUtc > effective) effective = _backgroundSuppressedUntilUtc;
            return effective;
        }

        private static int GetForegroundQuietSeconds(BackgroundAiRequestPriority priority)
        {
            if (priority == BackgroundAiRequestPriority.Critical || priority == BackgroundAiRequestPriority.High) return HighPriorityForegroundQuietSeconds;
            if (priority == BackgroundAiRequestPriority.Normal) return NormalPriorityForegroundQuietSeconds;
            return LowPriorityForegroundQuietSeconds;
        }

        private async Task WaitForForegroundIdleAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (_foregroundDepth <= 0) return;
                }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        private void EnsureWorkerLocked()
        {
            if (_workerTask == null || _workerTask.IsCompleted)
            {
                _workerTask = Task.Run(RunWorkerAsync, CancellationToken.None);
            }
        }

        private void LeaveForeground(string reason)
        {
            bool becameIdle = false;
            lock (_gate)
            {
                if (_foregroundDepth > 0) _foregroundDepth--;
                if (_foregroundDepth <= 0)
                {
                    _lastForegroundFinishedAtUtc = DateTimeOffset.UtcNow;
                    becameIdle = true;
                }
            }
            Publish(
                "ForegroundFinished",
                "foreground",
                reason ?? string.Empty,
                becameIdle
                    ? "Foreground reply request finished; background AI is held briefly so follow-up speech and phone TTS keep priority."
                    : "Foreground reply request finished; another foreground lease remains active.",
                QueuedCount);
            SafeReleaseSignal();
        }

        private void Publish(string phase, string stackName, string requestId, string message, int queuedCount)
        {
            try
            {
                StatusChanged?.Invoke(this, new BackgroundAiRequestStatusEventArgs
                {
                    Phase = phase ?? string.Empty,
                    StackName = stackName ?? string.Empty,
                    RequestId = requestId ?? string.Empty,
                    Message = message ?? string.Empty,
                    QueuedCount = queuedCount,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            catch
            {
            }
        }

        private void SafeReleaseSignal()
        {
            try { _signal.Release(); } catch { }
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BackgroundAiRequestDispatcher));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _queue.Clear();
            }
            try { _activeRequestCts?.Cancel(); } catch { }
            _shutdownCts.Cancel();
            SafeReleaseSignal();
            _signal.Dispose();
            _shutdownCts.Dispose();
        }

        private sealed class ForegroundLease : IDisposable
        {
            private BackgroundAiRequestDispatcher? _owner;
            private readonly string _reason;

            public ForegroundLease(BackgroundAiRequestDispatcher owner, string reason)
            {
                _owner = owner;
                _reason = reason;
            }

            public void Dispose()
            {
                BackgroundAiRequestDispatcher? owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null) owner.LeaveForeground(_reason);
            }
        }

        private sealed class QueuedBackgroundAiRequest
        {
            public string Id { get; set; } = string.Empty;
            public string StackName { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public BackgroundAiRequestPriority Priority { get; set; }
            public long Sequence { get; set; }
            public long OrderSequence { get; set; }
            public DateTimeOffset EnqueuedAt { get; set; }
            public DateTimeOffset NotBefore { get; set; }
            public Func<CancellationToken, Task> ExecuteAsync { get; set; } = default!;
            public CancellationToken CancellationToken { get; set; }
            public bool BypassPostForegroundQuiet { get; set; }
            public bool AllowForegroundPreemption { get; set; } = true;
            public bool BypassPlayerActivitySuppression { get; set; }
            public bool AllowPlayerActivityPreemption { get; set; } = true;
        }
    }

    public enum BackgroundAiRequestPriority
    {
        Critical = 0,
        High = 10,
        Normal = 50,
        Low = 100
    }

    public sealed class BackgroundAiRequestReceipt
    {
        public string RequestId { get; set; } = string.Empty;
        public string StackName { get; set; } = string.Empty;
        public BackgroundAiRequestPriority Priority { get; set; }
        public long Sequence { get; set; }
        public long OrderSequence { get; set; }
    }

    public sealed class BackgroundAiRequestStatusEventArgs : EventArgs
    {
        public string Phase { get; set; } = string.Empty;
        public string StackName { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int QueuedCount { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
