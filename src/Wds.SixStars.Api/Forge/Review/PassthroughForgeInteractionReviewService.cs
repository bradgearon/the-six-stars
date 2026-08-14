using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Forge.Review
{
    /// <summary>
    /// Compatibility delegate used only when the configured agent has no second-pass analyzer. SixStarsRuntime does
    /// not schedule this service in that mode; it lets the Forge composition root remain available for generation.
    /// </summary>
    public sealed class PassthroughForgeInteractionReviewService : IForgeInteractionReviewService
    {
        public Task<ForgeInteractionReviewResult> ReviewInteractionAsync(ForgeInteractionReviewRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return Task.FromResult(new ForgeInteractionReviewResult
            {
                ReviewId = request.ReviewId,
                Actor = request.Actor,
                Analysis = request.ForegroundResponse
            });
        }
    }
}
