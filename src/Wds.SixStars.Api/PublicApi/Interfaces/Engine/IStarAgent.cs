using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Engine
{
    public interface IStarAgent
    {
        Task<StarInteractionResponse> GenerateAsync(StarProfile profile, StarInteractionRequest request, StarState state, CancellationToken cancellationToken);
    }
}
