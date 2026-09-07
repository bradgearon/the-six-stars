using System;
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

    /// <summary>
    /// Optional extension for hosts that can keep one physical audio output open while many small
    /// WAV/PCM windows arrive. This is the preferred path for qwentts.cpp PCM streaming because
    /// reopening an audio device for every 100-250 ms transport packet creates audible gaps.
    /// </summary>
    public interface IStreamingWavePlaybackEndpoint : IWavePlaybackEndpoint
    {
        IStreamingWavePlaybackSession CreateStreamingSession(string reason);
    }

    /// <summary>
    /// One continuous playback lane. EnqueueAsync should copy/consume the supplied audio before it
    /// returns. CompleteAsync waits until queued sound has actually drained to the device.
    /// </summary>
    public interface IStreamingWavePlaybackSession : IDisposable
    {
        Task EnqueueAsync(VoiceSynthesisResult voiceAudio, CancellationToken cancellationToken);
        Task CompleteAsync(CancellationToken cancellationToken);
        void Cancel();
    }
}
