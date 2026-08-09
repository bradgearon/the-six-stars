using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.XPosting
{
    public interface IXPostClient
    {
        Task<StarXPostApiResult> CreatePostAsync(
            StarXAccountOptions account,
            string text,
            int timeoutSeconds,
            CancellationToken cancellationToken);
    }
}
