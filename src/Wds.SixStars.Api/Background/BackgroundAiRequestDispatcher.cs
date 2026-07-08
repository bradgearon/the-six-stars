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
        private bool _disposed;
        private const int MaxDelayWakeSeconds = 15;

        public event EventHandler<BackgroundAiRequestStatusEventArgs>? StatusChanged;

        public bool HasForegroundRequestActive
        {
            get { lock (_gate) return _foregroundDepth > 0; }
        }

        public int QueuedCount
        {
            get { lock (_gate) return _queue.Count; }
        }

        public IDisposable EnterForegroundRequest(string reason)
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                _foregroundDepth++;
            }

            Publish("ForegroundStarted", "foreground", reason ?? string.Empty, "A foreground reply request is active; background AI stacks will wait.", 0);
            SafeReleaseSignal();
            return new ForegroundLease(this, reason ?? string.Empty);
        }

        public BackgroundAiRequestReceipt Enqueue(
            string stackName,
            string description,
            BackgroundAiRequestPriority priority,
            Func<CancellationToken, Task> executeAsync,
            CancellationToken cancellationToken = default(CancellationToken),
            TimeSpan? delay = null)
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
                    EnqueuedAt = DateTimeOffset.UtcNow,
                    NotBefore = DateTimeOffset.UtcNow.Add(delay.GetValueOrDefault(TimeSpan.Zero)),
                    ExecuteAsync = executeAsync,
                    CancellationToken = cancellationToken
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
                Sequence = sequence
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
                            .Where(x => x.NotBefore <= now)
                            .OrderBy(x => (int)x.Priority)
                            .ThenBy(x => x.Sequence)
                            .FirstOrDefault();

                        if (request != null)
                        {
                            _queue.Remove(request);
                        }
                        else if (_queue.Count > 0)
                        {
                            DateTimeOffset next = _queue.Min(x => x.NotBefore);
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

                try
                {
                    using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, request.CancellationToken))
                    {
                        Publish("Started", request.StackName, request.Id, request.Description, QueuedCount);
                        await request.ExecuteAsync(linked.Token).ConfigureAwait(false);
                        Publish("Completed", request.StackName, request.Id, request.Description, QueuedCount);
                    }
                }
                catch (OperationCanceledException)
                {
                    Publish("Cancelled", request.StackName, request.Id, request.Description, QueuedCount);
                }
                catch (Exception ex)
                {
                    Publish("Failed", request.StackName, request.Id, ex.Message, QueuedCount);
                }
            }
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
            lock (_gate)
            {
                if (_foregroundDepth > 0) _foregroundDepth--;
            }
            Publish("ForegroundFinished", "foreground", reason ?? string.Empty, "Foreground reply request finished; background AI stacks may resume.", QueuedCount);
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
            public DateTimeOffset EnqueuedAt { get; set; }
            public DateTimeOffset NotBefore { get; set; }
            public Func<CancellationToken, Task> ExecuteAsync { get; set; } = default!;
            public CancellationToken CancellationToken { get; set; }
        }
    }

    public enum BackgroundAiRequestPriority
    {
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
