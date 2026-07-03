using System.Collections.Generic;

namespace Wds.SixStars.Api.Stars
{
    public interface IStarProfileProvider
    {
        StarProfile GetProfile(string starId);
        IReadOnlyList<StarProfile> GetAllProfiles();
    }
}
