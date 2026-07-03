using System;
using System.IO;
using System.Text;

namespace Wds.SixStars.Api.Audio
{
    public static class WaveFileUtilities
    {
        public static byte[] WrapPcm16InWav(byte[] pcm16, PcmAudioFormat format)
        {
            if (format.BitsPerSample != 16 || format.Encoding != "pcm_s16le")
            {
                throw new NotSupportedException("Only little-endian PCM16 WAV wrapping is supported by this helper.");
            }

            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                int byteRate = format.SampleRateHz * format.Channels * format.BitsPerSample / 8;
                short blockAlign = (short)(format.Channels * format.BitsPerSample / 8);
                int subChunk2Size = pcm16.Length;
                int chunkSize = 36 + subChunk2Size;

                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(chunkSize);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)format.Channels);
                writer.Write(format.SampleRateHz);
                writer.Write(byteRate);
                writer.Write(blockAlign);
                writer.Write((short)format.BitsPerSample);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(subChunk2Size);
                writer.Write(pcm16);
                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
