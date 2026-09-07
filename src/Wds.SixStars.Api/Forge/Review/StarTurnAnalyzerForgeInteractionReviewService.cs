using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Engine;

namespace Wds.SixStars.Api.Forge.Review
{
    /// <summary>
    /// Adapter that moves the background review behind a Forge service boundary without changing the existing
    /// StructuredStarAgent review behavior. The adapter can later be replaced by an HTTP/WebSocket Forge client.
    /// </summary>
    public sealed class StarTurnAnalyzerForgeInteractionReviewService : IForgeInteractionReviewService
    {
        private readonly IStarTurnAnalyzer _analyzer;

        public StarTurnAnalyzerForgeInteractionReviewService(IStarTurnAnalyzer analyzer)
        {
            _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        }

        public async Task<ForgeInteractionReviewResult> ReviewInteractionAsync(
            ForgeInteractionReviewRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var analyzed = await _analyzer.AnalyzeTurnAsync(
                request.Profile,
                request.Interaction,
                request.StateBeforeTurn,
                request.ForegroundResponse,
                cancellationToken).ConfigureAwait(false);

            return new ForgeInteractionReviewResult
            {
                ReviewId = request.ReviewId,
                Actor = request.Actor,
                Analysis = analyzed
            };
        }
    }
}
