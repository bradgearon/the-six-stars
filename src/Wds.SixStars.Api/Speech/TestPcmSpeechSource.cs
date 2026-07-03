using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Audio;

namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// Dev/test helper that pushes fake speech-shaped PCM into the same pipeline Unity/mic capture uses.
    /// It proves the VAD -> hotword -> Whisper streamer -> transcript events without needing a microphone yet.
    /// Chunks are intentionally 100 ms to match the intended live stream cadence.
    /// </summary>
    public static class TestPcmSpeechSource
    {
        public static async Task PushFakeUtteranceAsync(WhisperSpeechInputPipeline pipeline, PcmAudioFormat format, CancellationToken cancellationToken)
        {
            await PushSilenceAsync(pipeline, format, 300, cancellationToken).ConfigureAwait(false);
            await PushToneAsync(pipeline, format, 700, 220, 0.12, cancellationToken).ConfigureAwait(false);
            await PushToneAsync(pipeline, format, 700, 330, 0.10, cancellationToken).ConfigureAwait(false);
            await PushSilenceAsync(pipeline, format, 1200, cancellationToken).ConfigureAwait(false);
        }

        private static async Task PushSilenceAsync(WhisperSpeechInputPipeline pipeline, PcmAudioFormat format, int totalMs, CancellationToken cancellationToken)
        {
            for (int remaining = totalMs; remaining > 0; remaining -= 100)
            {
                int duration = remaining >= 100 ? 100 : remaining;
                await PushChunkAsync(pipeline, PcmAudioUtilities.CreateSilencePcm16(format.SampleRateHz, duration, format.Channels), format, "test-silence", cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task PushToneAsync(WhisperSpeechInputPipeline pipeline, PcmAudioFormat format, int totalMs, double frequencyHz, double amplitude, CancellationToken cancellationToken)
        {
            for (int remaining = totalMs; remaining > 0; remaining -= 100)
            {
                int duration = remaining >= 100 ? 100 : remaining;
                await PushChunkAsync(pipeline, PcmAudioUtilities.CreateSinePcm16(format.SampleRateHz, duration, format.Channels, frequencyHz, amplitude), format, "test-speech", cancellationToken).ConfigureAwait(false);
            }
        }

        private static Task PushChunkAsync(WhisperSpeechInputPipeline pipeline, byte[] pcm, PcmAudioFormat format, string source, CancellationToken cancellationToken)
        {
            return pipeline.PushPcm16Async(pcm, pcm.Length, format, source, cancellationToken);
        }
    }
}
