using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Screen
{
    public interface IScreenContextStore
    {
        Task AddAsync(string sessionId, ScreenCaptureSummary capture, CancellationToken cancellationToken);
        Task<IReadOnlyList<ScreenCaptureSummary>> GetRecentAsync(string sessionId, int maxCount, CancellationToken cancellationToken);
        Task ClearAsync(string sessionId, CancellationToken cancellationToken);
    }
}
