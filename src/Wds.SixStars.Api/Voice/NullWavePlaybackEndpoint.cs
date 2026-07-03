using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// Playback endpoint that accepts generated WAV and does nothing.
    /// Useful for tests and for Unity/Desktop until a real player is wired in.
    /// </summary>
    public sealed class NullWavePlaybackEndpoint : IWavePlaybackEndpoint
    {
        public VoiceSynthesisResult? LastSubmittedAudio { get; private set; }

        public Task PlayWavAsync(VoiceSynthesisResult voiceAudio, CancellationToken cancellationToken)
        {
            LastSubmittedAudio = voiceAudio;
            return Task.CompletedTask;
        }
    }
}
