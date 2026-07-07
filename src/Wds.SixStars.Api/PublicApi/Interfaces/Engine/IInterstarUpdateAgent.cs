using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Engine
{
    public interface IInterstarUpdateAgent
    {
        Task<InterstarRelayMessage> GenerateInterstarRelayAsync(
            StarProfile originProfile,
            StarProfile targetProfile,
            StarInteractionRequest originalRequest,
            StarInteractionResponse originResponse,
            StarState originState,
            StarState targetState,
            CancellationToken cancellationToken);

        Task<StarInteractionResponse> AnalyzeInterstarReactionAsync(
            StarProfile targetProfile,
            InterstarRelayMessage relay,
            StarInteractionRequest originalRequest,
            StarInteractionResponse originResponse,
            StarState targetState,
            CancellationToken cancellationToken);

        Task<StarInteractionResponse> AnalyzeInterstarFeedbackAsync(
            StarProfile originProfile,
            StarInteractionRequest originalRequest,
            StarInteractionResponse originResponse,
            StarState originState,
            IReadOnlyList<InterstarReactionSummary> reactions,
            CancellationToken cancellationToken);
    }
}
