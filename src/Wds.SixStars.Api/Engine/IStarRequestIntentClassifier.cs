using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Engine
{
    /// <summary>
    /// Optional pre-flight classifier for agents that can cheaply decide whether the latest
    /// player input should be handled as normal chat or long-form story narration.
    /// </summary>
    public interface IStarRequestIntentClassifier
    {
        Task<StarRequestIntentResult> ClassifyRequestIntentAsync(
            StarProfile profile,
            StarInteractionRequest request,
            StarState state,
            CancellationToken cancellationToken);
    }
}
