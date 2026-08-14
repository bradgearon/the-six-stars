using Wds.SixStars.Api.Forge.Generation;
using Wds.SixStars.Api.Forge.Review;

namespace Wds.SixStars.Api.Forge.Contracts
{
    /// <summary>
    /// First in-process Forge boundary. Network transport can later sit in front of this interface without moving
    /// generation/review logic back into the desktop UI or SixStarsRuntime.
    /// </summary>
    public interface IForgeService : IForgeInteractionReviewService, IForgeContentGenerationService
    {
        string ForgeId { get; }
    }
}
