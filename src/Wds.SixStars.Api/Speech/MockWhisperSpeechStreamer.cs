using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Audio;

namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// Test double for the real local Whisper streamer. It lets the UI or unit tests queue the text
    /// that should come back after fake audio is pushed through the speech pipeline.
    /// </summary>
    public sealed class MockWhisperSpeechStreamer : IWhisperSpeechStreamer
    {
        private readonly ConcurrentQueue<string> _queuedTexts = new ConcurrentQueue<string>();
        private readonly ConcurrentDictionary<string, MemoryStream> _segments = new ConcurrentDictionary<string, MemoryStream>();
        private readonly ConcurrentDictionary<string, DateTimeOffset> _startedAt = new ConcurrentDictionary<string, DateTimeOffset>();

        public void QueueTranscript(string text)
        {
            _queuedTexts.Enqueue(text ?? string.Empty);
        }

        public Task BeginSegmentAsync(string segmentId, PcmAudioFormat format, WhisperTranscriptionOptions options, CancellationToken cancellationToken)
        {
            _segments[segmentId] = new MemoryStream();
            _startedAt[segmentId] = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }

        public Task PushAudioAsync(string segmentId, byte[] pcm16Buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_segments.TryGetValue(segmentId, out MemoryStream? stream))
            {
                stream.Write(pcm16Buffer, offset, count);
            }

            return Task.CompletedTask;
        }

        public Task<SpeechTranscript> EndSegmentAsync(string segmentId, CancellationToken cancellationToken)
        {
            _segments.TryRemove(segmentId, out MemoryStream? stream);
            _startedAt.TryRemove(segmentId, out DateTimeOffset startedAt);

            if (!_queuedTexts.TryDequeue(out string? text))
            {
                text = "Elusiaa, tell me a story while I work.";
            }

            SpeechTranscript transcript = new SpeechTranscript
            {
                SegmentId = segmentId,
                Text = text,
                Confidence = 0.99,
                Language = "en",
                StartedAt = startedAt == default(DateTimeOffset) ? DateTimeOffset.UtcNow : startedAt,
                EndedAt = DateTimeOffset.UtcNow,
                AudioDurationMs = stream == null ? 0 : stream.Length / 32.0,
                Source = "mock-whisper"
            };

            if (stream != null)
            {
                stream.Dispose();
            }

            return Task.FromResult(transcript);
        }

        public Task CancelSegmentAsync(string segmentId, CancellationToken cancellationToken)
        {
            if (_segments.TryRemove(segmentId, out MemoryStream? stream))
            {
                stream.Dispose();
            }

            _startedAt.TryRemove(segmentId, out DateTimeOffset _);
            return Task.CompletedTask;
        }
    }
}
