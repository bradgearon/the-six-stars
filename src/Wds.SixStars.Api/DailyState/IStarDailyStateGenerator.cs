using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.DailyState
{
    public interface IStarDailyStateGenerator
    {
        Task<StarDailyStateModelResponse> GenerateDailyStateAsync(StarDailyStateGenerationRequest request, CancellationToken cancellationToken);
    }
}
