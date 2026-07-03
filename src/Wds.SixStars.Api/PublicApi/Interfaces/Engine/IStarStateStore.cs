using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Engine
{
    public interface IStarStateStore
    {
        Task<StarState> GetAsync(string sessionId, string starId, CancellationToken cancellationToken);
        Task SaveAsync(string sessionId, StarState state, CancellationToken cancellationToken);
        Task<IReadOnlyList<StarState>> GetAllAsync(string sessionId, CancellationToken cancellationToken);
        Task ReplaceSessionAsync(string sessionId, IEnumerable<StarState> states, CancellationToken cancellationToken);
        Task ResetStarAsync(string sessionId, string starId, CancellationToken cancellationToken);
    }
}
