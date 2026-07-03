using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Audio;

namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// Fails loudly when no Whisper endpoint is configured. This prevents real microphone
    /// testing from silently returning the old mock transcript.
    /// </summary>
    public sealed class UnconfiguredWhisperSpeechStreamer : IWhisperSpeechStreamer
    {
        public Task BeginSegmentAsync(string segmentId, PcmAudioFormat format, WhisperTranscriptionOptions options, CancellationToken cancellationToken)
        {
            throw CreateException();
        }

        public Task PushAudioAsync(string segmentId, byte[] pcm16Buffer, int offset, int count, CancellationToken cancellationToken)
        {
            throw CreateException();
        }

        public Task<SpeechTranscript> EndSegmentAsync(string segmentId, CancellationToken cancellationToken)
        {
            throw CreateException();
        }

        public Task CancelSegmentAsync(string segmentId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private static InvalidOperationException CreateException()
        {
            return new InvalidOperationException("No real Whisper endpoint is configured. Start scripts\\faster_whisper_openai_server.py through start.cmd or set SIX_STARS_WHISPER_ENDPOINT, for example http://localhost:8000/v1/audio/transcriptions. Set SIX_STARS_USE_MOCK_WHISPER=true only for deliberate mock testing.");
        }
    }
}
