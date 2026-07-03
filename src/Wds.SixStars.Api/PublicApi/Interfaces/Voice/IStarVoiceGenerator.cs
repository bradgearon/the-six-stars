using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// Abstraction for the local voice generation model.
    /// A real implementation can call a local HTTP TTS sidecar, Piper, XTTS, RVC chain, etc.
    /// </summary>
    public interface IStarVoiceGenerator
    {
        Task<VoiceSynthesisResult> GenerateAsync(VoiceSynthesisRequest request, CancellationToken cancellationToken);
    }
}
