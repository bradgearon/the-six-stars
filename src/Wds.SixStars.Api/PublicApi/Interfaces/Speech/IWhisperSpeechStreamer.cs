using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Audio;

namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// Abstraction for a local Whisper process, whisper.cpp server, Whisper.net wrapper,
    /// or any ASR service that accepts PCM16 audio. Unity and desktop mic capture both feed this interface.
    /// </summary>
    public interface IWhisperSpeechStreamer
    {
        Task BeginSegmentAsync(string segmentId, PcmAudioFormat format, WhisperTranscriptionOptions options, CancellationToken cancellationToken);
        Task PushAudioAsync(string segmentId, byte[] pcm16Buffer, int offset, int count, CancellationToken cancellationToken);
        Task<SpeechTranscript> EndSegmentAsync(string segmentId, CancellationToken cancellationToken);
        Task CancelSegmentAsync(string segmentId, CancellationToken cancellationToken);
    }
}
