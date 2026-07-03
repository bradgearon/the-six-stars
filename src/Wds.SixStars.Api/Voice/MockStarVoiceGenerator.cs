using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Audio;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// Dev/test voice generator. It returns a short quiet tone wrapped as WAV so the pipeline can be tested
    /// before a real TTS sidecar is connected.
    /// </summary>
    public sealed class MockStarVoiceGenerator : IStarVoiceGenerator
    {
        public Task<VoiceSynthesisResult> GenerateAsync(VoiceSynthesisRequest request, CancellationToken cancellationToken)
        {
            PcmAudioFormat format = PcmAudioFormat.Mono16Khz();
            int durationMs = Math.Max(350, Math.Min(2500, (request.Text ?? string.Empty).Length * 20));
            byte[] pcm = PcmAudioUtilities.CreateSinePcm16(format.SampleRateHz, durationMs, format.Channels, 440, 0.03);
            byte[] wav = WaveFileUtilities.WrapPcm16InWav(pcm, format);
            return Task.FromResult(new VoiceSynthesisResult
            {
                StarId = request.StarId,
                VoiceId = request.VoiceSettings.VoiceId,
                Instructions = request.VoiceSettings.DeliveryInstructions,
                WavAudio = wav,
                DurationMs = durationMs,
                Source = "mock-star-voice-generator"
            });
        }
    }
}
