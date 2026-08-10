using System.Collections.Generic;

namespace Wds.SixStars.Api.Writings
{
    /// <summary>Creates optional word ideas shared by every pass in one writing cycle.</summary>
    public interface IStarWritingSeedWordProvider
    {
        IReadOnlyList<string> CreateSeedWords();
        IReadOnlyList<string> CreateNounSeedWords();
    }
}
