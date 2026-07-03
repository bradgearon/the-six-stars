using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Wds.SixStars.Api.Runtime
{
    /// <summary>
    /// Shared cancellation/operation controller for UI and Unity hosts.
    ///
    /// Use BeginNewOperation before starting a user-driven request, pass CurrentToken into
    /// microphone/transcription/LLM/TTS operations, and call CancelAllRuntimeOperations when
    /// the player pauses, changes scenes, presses a cancel button, or otherwise needs the
    /// full live conversation loop to stop immediately.
    /// </summary>
    public sealed class SixStarsRuntimeOperationController : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Action> _cancelActions = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _currentCts = new CancellationTokenSource();
        private int _operationVersion;
        private bool _disposed;

        public event EventHandler<RuntimeOperationCancelledEventArgs>? RuntimeOperationCancelled;

        public int OperationVersion
        {
            get
            {
                lock (_gate)
                {
                    return _operationVersion;
                }
            }
        }

        public CancellationToken CurrentToken
        {
            get
            {
                lock (_gate)
                {
                    return _currentCts.Token;
                }
            }
        }

        public CancellationToken BeginNewOperation(string reason)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!_currentCts.IsCancellationRequested)
                {
                    _currentCts.Cancel();
                }

                _currentCts.Dispose();
                _currentCts = new CancellationTokenSource();
                _operationVersion++;
                Debug.WriteLine($"[RuntimeOps] Begin operation {_operationVersion}: {reason}");
                return _currentCts.Token;
            }
        }

        public CancellationToken GetLinkedToken(CancellationToken externalToken)
        {
            CancellationToken current = CurrentToken;
            if (!externalToken.CanBeCanceled)
            {
                return current;
            }

            // Callers that need disposal should create their own linked source. This helper is
            // intentionally simple for hosts that only need the active runtime token.
            return current;
        }

        public void RegisterCancelAction(string name, Action cancelAction)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Cancel action name is required.", nameof(name));
            if (cancelAction == null) throw new ArgumentNullException(nameof(cancelAction));

            lock (_gate)
            {
                ThrowIfDisposed();
                _cancelActions[name] = cancelAction;
            }
        }

        public void UnregisterCancelAction(string name)
        {
            lock (_gate)
            {
                _cancelActions.Remove(name);
            }
        }

        public void CancelAllRuntimeOperations(string reason = "Cancel requested")
        {
            List<KeyValuePair<string, Action>> actions;
            int version;

            lock (_gate)
            {
                if (_disposed) return;
                version = _operationVersion;
                if (!_currentCts.IsCancellationRequested)
                {
                    _currentCts.Cancel();
                }

                actions = new List<KeyValuePair<string, Action>>(_cancelActions);
            }

            Debug.WriteLine($"[RuntimeOps] Cancel all runtime operations v{version}: {reason}. Registered cancel actions: {actions.Count}");
            foreach (KeyValuePair<string, Action> pair in actions)
            {
                try
                {
                    pair.Value();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RuntimeOps] Cancel action '{pair.Key}' failed: {ex.Message}");
                }
            }

            RuntimeOperationCancelled?.Invoke(this, new RuntimeOperationCancelledEventArgs
            {
                Reason = reason,
                OperationVersion = version,
                CancelledAt = DateTimeOffset.UtcNow
            });
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _currentCts.Cancel();
                _currentCts.Dispose();
                _cancelActions.Clear();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SixStarsRuntimeOperationController));
        }
    }

    public sealed class RuntimeOperationCancelledEventArgs : EventArgs
    {
        public string Reason { get; set; } = string.Empty;
        public int OperationVersion { get; set; }
        public DateTimeOffset CancelledAt { get; set; }
    }
}
