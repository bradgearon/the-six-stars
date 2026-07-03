using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// Abstraction for the playback side of the API.
    /// Desktop can implement this with a WPF/NAudio player. Unity can implement it by receiving WAV bytes and playing an AudioClip.
    /// </summary>
    public interface IWavePlaybackEndpoint
    {
        Task PlayWavAsync(VoiceSynthesisResult voiceAudio, CancellationToken cancellationToken);
    }
}
