using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Engine
{
    /// <summary>
    /// Optional second-pass analyzer for agents that can return a fast spoken response first,
    /// then run heavy memory/emotion/state analysis in the background.
    /// </summary>
    public interface IStarTurnAnalyzer
    {
        Task<StarInteractionResponse> AnalyzeTurnAsync(
            StarProfile profile,
            StarInteractionRequest request,
            StarState state,
            StarInteractionResponse foregroundResponse,
            CancellationToken cancellationToken);
    }
}
