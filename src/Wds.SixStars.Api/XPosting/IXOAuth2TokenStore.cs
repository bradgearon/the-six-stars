using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.XPosting
{
    /// <summary>Persists per-Star OAuth 2.0 user tokens outside normal JSON configuration.</summary>
    public interface IXOAuth2TokenStore
    {
        Task<StarXOAuth2Token?> LoadAsync(string accountId, CancellationToken cancellationToken);
        Task SaveAsync(StarXOAuth2Token token, CancellationToken cancellationToken);
        Task DeleteAsync(string accountId, CancellationToken cancellationToken);
    }
}
