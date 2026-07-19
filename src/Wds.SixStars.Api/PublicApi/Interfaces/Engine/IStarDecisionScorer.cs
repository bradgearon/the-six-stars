using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Engine
{
    public interface IStarDecisionScorer
    {
        Task<StarDecisionScoreResponse> ScoreDecisionAsync(StarProfile profile, StarDecisionScoreRequest request, StarState state, CancellationToken cancellationToken);
    }
}
