using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Engine
{
    public interface IRelationshipDeltaCalculator
    {
        RelationshipDelta Calculate(StarProfile profile, StarInteractionRequest request, StarInteractionResponse response);
    }
}
