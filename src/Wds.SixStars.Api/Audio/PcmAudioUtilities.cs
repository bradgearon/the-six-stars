using System;

namespace Wds.SixStars.Api.Audio
{
    public static class PcmAudioUtilities
    {
        public static byte[] ConvertFloatSamplesToPcm16(float[] samples, int sampleCount)
        {
            int count = Math.Max(0, Math.Min(sampleCount, samples.Length));
            byte[] pcm = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                float clamped = Math.Max(-1.0f, Math.Min(1.0f, samples[i]));
                short value = (short)(clamped * short.MaxValue);
                pcm[i * 2] = (byte)(value & 0xFF);
                pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            return pcm;
        }

        public static byte[] CreateSilencePcm16(int sampleRateHz, int durationMs, int channels)
        {
            int sampleCount = Math.Max(0, sampleRateHz * durationMs / 1000 * channels);
            return new byte[sampleCount * 2];
        }

        public static byte[] CreateSinePcm16(int sampleRateHz, int durationMs, int channels, double frequencyHz, double amplitude)
        {
            int sampleCountPerChannel = Math.Max(0, sampleRateHz * durationMs / 1000);
            byte[] pcm = new byte[sampleCountPerChannel * channels * 2];
            amplitude = Math.Max(0, Math.Min(1, amplitude));

            for (int i = 0; i < sampleCountPerChannel; i++)
            {
                short value = (short)(Math.Sin(2 * Math.PI * frequencyHz * i / sampleRateHz) * amplitude * short.MaxValue);
                for (int c = 0; c < channels; c++)
                {
                    int offset = (i * channels + c) * 2;
                    pcm[offset] = (byte)(value & 0xFF);
                    pcm[offset + 1] = (byte)((value >> 8) & 0xFF);
                }
            }

            return pcm;
        }
    }
}
