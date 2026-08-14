using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Writings;

namespace Wds.SixStars.Api.Forge.Generation
{
    /// <summary>
    /// Places the three shelf generation techniques behind the Forge content-generation boundary.
    /// StarWritingService still contains their orchestration in this migration step; callers no longer depend on it.
    /// </summary>
    public sealed class SixStarsForgeContentGenerationService : IForgeContentGenerationService
    {
        private readonly StarWritingService _writingService;

        public SixStarsForgeContentGenerationService(StarWritingService writingService)
        {
            _writingService = writingService ?? throw new ArgumentNullException(nameof(writingService));
        }

        public async Task<ForgeContentGenerationResult> GenerateContentAsync(
            ForgeContentGenerationRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Actor == null || string.IsNullOrWhiteSpace(request.Actor.ActorId))
                throw new InvalidOperationException("Forge content generation requires an ActorId.");

            string generatorId = ForgeGeneratorIds.Normalize(request.GeneratorId);
            string? requestedMethod = string.Equals(generatorId, ForgeGeneratorIds.Auto, StringComparison.OrdinalIgnoreCase)
                ? null
                : generatorId;

            StarWritingCreateResult result = await _writingService.GenerateAndReviewAsync(
                request.Actor.ActorId,
                requestedMethod,
                cancellationToken,
                request.Progress).ConfigureAwait(false);

            return new ForgeContentGenerationResult
            {
                RequestId = request.RequestId,
                Actor = request.Actor,
                GeneratorId = result.WritingMethod,
                WritingResult = result
            };
        }
    }
}
