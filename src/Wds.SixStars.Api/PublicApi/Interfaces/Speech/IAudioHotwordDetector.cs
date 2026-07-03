using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// Abstraction for a lightweight local hotword / keyword spotting model.
    /// It receives the first short audio probe from an utterance, not the whole segment.
    /// The real implementation can call openWakeWord, Porcupine, a tiny local model, or any sidecar process.
    /// </summary>
    public interface IAudioHotwordDetector
    {
        Task<HotwordDetectionResult> DetectAsync(AudioHotwordDetectionRequest request, CancellationToken cancellationToken);
    }
}
