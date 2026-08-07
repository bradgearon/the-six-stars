using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.State;

namespace Wds.SixStars.Ui.Services;

internal sealed class DesktopStarStateService
{
    private readonly SixStarsRuntime _runtime;
    public DesktopStarStateService(SixStarsRuntime runtime) => _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public Task<StarState> LoadAsync(string sessionId, string starId, CancellationToken cancellationToken)
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
    public Task<StarState> ResetAsync(string sessionId, string starId, CancellationToken cancellationToken)
        => _runtime.ResetStarMemoryAsync(sessionId, starId, cancellationToken);

    public Task<StarState> SetExclusiveGirlfriendAsync(
        string sessionId,
        string starId,
        bool isGirlfriend,
        string reason,
        CancellationToken cancellationToken)
        => _runtime.SetExclusiveGirlfriendAsync(sessionId, starId, isGirlfriend, reason, cancellationToken);

    public Task<StarState> SetCuteModeEnabledAsync(
        string sessionId,
        string starId,
        bool enabled,
        string reason,
        CancellationToken cancellationToken)
        => _runtime.SetGirlfriendCuteModeEnabledAsync(sessionId, starId, enabled, reason, cancellationToken);
}
