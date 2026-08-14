using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Forge.Generation
{
    public interface IForgeContentGenerationService
    {
        Task<ForgeContentGenerationResult> GenerateContentAsync(
            ForgeContentGenerationRequest request,
            CancellationToken cancellationToken);
    }
}
