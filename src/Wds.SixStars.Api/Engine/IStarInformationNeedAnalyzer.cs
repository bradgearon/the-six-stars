using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Engine
{
    /// <summary>
    /// Optional very fast selector that interprets the foreground informationNeeds array.
    /// It does not fetch data and it does not answer the player; the runtime uses its result
    /// as one input to the deterministic local data-selection decision.
    /// </summary>
    public interface IStarInformationNeedAnalyzer
    {
        Task<InformationNeedSelectionResult> AnalyzeInformationNeedAsync(
            StarProfile profile,
            StarInteractionRequest request,
            StarState state,
            StarInteractionResponse foregroundResponse,
            IReadOnlyList<InformationNeedVectorItem> foregroundVector,
            IReadOnlyList<InformationNeedAvailabilityItem> availableData,
            CancellationToken cancellationToken);
    }
}
