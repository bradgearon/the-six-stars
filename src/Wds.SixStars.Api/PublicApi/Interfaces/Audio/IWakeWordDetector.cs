using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Audio
{
    public interface IWakeWordDetector
    {
        Task<string?> TryDetectStarAsync(string transcribedText, WakeAliasConfig config, CancellationToken cancellationToken);
    }
}
