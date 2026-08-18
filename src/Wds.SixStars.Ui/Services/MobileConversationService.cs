using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.State;

namespace Wds.SixStars.Ui.Services;

/// <summary>
/// Owns the Android foreground runtime lane. Automatic initiative requests can probe the lane
/// without waiting while player text/voice/photo requests take an awaited lease and preempt lower
/// priority background work.
/// </summary>
internal sealed class MobileConversationService : IDisposable
{
    internal sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;
        internal Lease(SemaphoreSlim gate) => _gate = gate;
        public void Dispose()
        {
            SemaphoreSlim? gate = Interlocked.Exchange(ref _gate, null);
            if (gate == null) return;
            try { gate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    private readonly SixStarsRuntime _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MobileConversationService(SixStarsRuntime runtime)
        => _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public async Task<Lease?> TryAcquireAsync(CancellationToken cancellationToken)
        => await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false) ? new Lease(_gate) : null;

    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_gate);
    }

    public Task<StarInteractionResponse> ProcessAsync(StarInteractionRequest request, CancellationToken cancellationToken)
        => _runtime.ProcessAsync(request, cancellationToken);

    public Task<StarInteractionResponse> ProcessWhenForegroundIdleAsync(
        StarInteractionRequest request,
        string reason,
        CancellationToken cancellationToken)
        => _runtime.ProcessWhenForegroundIdleAsync(request, reason, cancellationToken);

    public Task<StarState> GetStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        => _runtime.GetStarStateAsync(sessionId, starId, cancellationToken);

    public void SuppressBackgroundAnalysisForPlayerActivity(TimeSpan quietWindow, string reason)
        => _runtime.SuppressBackgroundAnalysisForPlayerActivity(quietWindow, reason);

    public void CancelBackgroundAnalysis(string reason) => _runtime.CancelBackgroundAnalysis(reason);

    public void Dispose() => _gate.Dispose();
}
