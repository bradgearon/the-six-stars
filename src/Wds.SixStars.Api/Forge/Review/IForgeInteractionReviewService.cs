using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Forge.Review
{
    public interface IForgeInteractionReviewService
    {
        Task<ForgeInteractionReviewResult> ReviewInteractionAsync(
            ForgeInteractionReviewRequest request,
            CancellationToken cancellationToken);
    }
}
