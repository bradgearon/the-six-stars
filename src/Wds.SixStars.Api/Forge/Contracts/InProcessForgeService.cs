using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Forge.Generation;
using Wds.SixStars.Api.Forge.Review;

namespace Wds.SixStars.Api.Forge.Contracts
{
    /// <summary>
    /// Composition root for the current in-process Forge. Replacing this object with a transport client is the future
    /// service split; the desktop app and SixStarsRuntime can keep speaking the same Forge contracts.
    /// </summary>
    public sealed class InProcessForgeService : IForgeService
    {
        private readonly IForgeInteractionReviewService _review;
        private readonly IForgeContentGenerationService _generation;

        public InProcessForgeService(
            IForgeInteractionReviewService review,
            IForgeContentGenerationService generation,
            string forgeId = "six-stars")
        {
            _review = review ?? throw new ArgumentNullException(nameof(review));
            _generation = generation ?? throw new ArgumentNullException(nameof(generation));
            ForgeId = string.IsNullOrWhiteSpace(forgeId) ? "six-stars" : forgeId.Trim();
        }

        public string ForgeId { get; }

        public Task<ForgeInteractionReviewResult> ReviewInteractionAsync(
            ForgeInteractionReviewRequest request,
            CancellationToken cancellationToken)
        {
            return _review.ReviewInteractionAsync(request, cancellationToken);
        }

        public Task<ForgeContentGenerationResult> GenerateContentAsync(
            ForgeContentGenerationRequest request,
            CancellationToken cancellationToken)
        {
            return _generation.GenerateContentAsync(request, cancellationToken);
        }
    }
}
