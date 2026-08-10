using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Runtime;
using Wds.SixStars.Api.State;

namespace Wds.SixStars.Ui.Services;

/// <summary>Single entry point for foreground runtime work and durable Star state operations.</summary>
internal sealed class DesktopConversationService
{
    private readonly SixStarsRuntime _runtime;
    private readonly SixStarsRuntimeOperationController _operations;

    public DesktopConversationService(SixStarsRuntime runtime, SixStarsRuntimeOperationController operations)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public CancellationToken BeginOperation(string reason) => _operations.BeginNewOperation(reason);
    public CancellationToken CurrentToken => _operations.CurrentToken;

    public Task<StarInteractionResponse> ProcessAsync(StarInteractionRequest request, string reason)
    {
        CancellationToken token = BeginOperation(reason);
        return _runtime.ProcessAsync(request, token);
    }

    public Task<StarInteractionResponse> ProcessAsync(StarInteractionRequest request, CancellationToken cancellationToken)
        => _runtime.ProcessAsync(request, cancellationToken);

    public Task<StarState> GetStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        => _runtime.GetStarStateAsync(sessionId, starId, cancellationToken);

    public Task<StarState> SetMetAsync(string sessionId, string starId, bool value, CancellationToken cancellationToken)
        => _runtime.SetStarMetAsync(sessionId, starId, value, cancellationToken);

    public Task<StarState> SetAllyAsync(string sessionId, string starId, bool value, string reason, CancellationToken cancellationToken)
        => _runtime.SetStarWorldStateAsync(new StarWorldStateUpdateRequest
        {
            SessionId = sessionId,
            StarId = starId,
            IsAlly = value,
            IsMet = value ? true : null,
            Reason = reason ?? string.Empty
        }, cancellationToken);

    public Task<StarState> ResetStarAsync(string sessionId, string starId, CancellationToken cancellationToken)
        => _runtime.ResetStarMemoryAsync(sessionId, starId, cancellationToken);

    public Task TriggerDailyStateRefreshAsync(string sessionId, IReadOnlyCollection<string> starIds, string reason, CancellationToken cancellationToken)
        => _runtime.TriggerDailyStateRefreshAsync(sessionId, starIds, reason, cancellationToken);
}
