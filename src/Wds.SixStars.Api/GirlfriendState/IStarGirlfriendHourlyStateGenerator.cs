using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.GirlfriendState
{
    public interface IStarGirlfriendHourlyStateGenerator
    {
        Task<StarGirlfriendHourlyStateModelResponse> GenerateGirlfriendHourlyStateAsync(StarGirlfriendHourlyStateGenerationRequest request, CancellationToken cancellationToken);
    }
}
