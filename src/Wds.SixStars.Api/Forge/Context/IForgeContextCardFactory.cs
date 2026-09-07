using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Forge.Context
{
    public interface IForgeContextCardFactory
    {
        ForgeContextBundle CreateInteractionReviewBundle(
            StarProfile profile,
            StarInteractionRequest request,
            StarState state,
            StarInteractionResponse foregroundResponse,
            int recentConversationCount = 6);
    }
}
