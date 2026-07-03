using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// Explicit no-op audio hotword detector. The current desktop voice path does not use
    /// OpenWakeWord; it records by amplitude VAD, transcribes the utterance, then optionally
    /// applies text wake-name filtering in SpeechToStarCoordinator.
    /// </summary>
    public sealed class NoAudioHotwordDetector : IAudioHotwordDetector
    {
        public Task<HotwordDetectionResult> DetectAsync(AudioHotwordDetectionRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(HotwordDetectionResult.None("audio-hotword-disabled"));
        }
    }
}
