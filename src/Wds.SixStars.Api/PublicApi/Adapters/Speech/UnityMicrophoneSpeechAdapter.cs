using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Audio;

namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// Small Unity-friendly adapter. Unity can read float samples from Microphone/GetData,
    /// then call this method. The rest of the pipeline remains shared with the desktop app.
    /// </summary>
    public sealed class UnityMicrophoneSpeechAdapter
    {
        private readonly WhisperSpeechInputPipeline _pipeline;

        public UnityMicrophoneSpeechAdapter(WhisperSpeechInputPipeline pipeline)
        {
            _pipeline = pipeline;
        }

        public Task PushUnityFloatSamplesAsync(
            float[] samples,
            int sampleCount,
            int sampleRateHz,
            int channels,
            string source,
            CancellationToken cancellationToken)
        {
            byte[] pcm16 = PcmAudioUtilities.ConvertFloatSamplesToPcm16(samples, sampleCount);
            PcmAudioFormat format = new PcmAudioFormat
            {
                SampleRateHz = sampleRateHz,
                Channels = channels,
                BitsPerSample = 16,
                Encoding = "pcm_s16le"
            };

            return _pipeline.PushPcm16Async(pcm16, pcm16.Length, format, source, cancellationToken);
        }
    }
}
