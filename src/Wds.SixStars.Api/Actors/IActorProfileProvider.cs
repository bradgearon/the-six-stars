using System.Collections.Generic;

namespace Wds.SixStars.Api.Actors
{
    public interface IActorProfileProvider
    {
        ActorProfile GetActor(string actorId);
        IReadOnlyList<ActorProfile> GetAllActors();
    }
}
