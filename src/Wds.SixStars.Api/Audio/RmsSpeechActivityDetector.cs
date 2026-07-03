using System;

namespace Wds.SixStars.Api.Audio
{
    /// <summary>
    /// Lightweight starter VAD. This is intentionally simple and dependency-free so it works in Unity too.
    /// Replace or wrap it later with WebRTC VAD / Silero VAD if you want better noise handling.
    /// </summary>
    public sealed class RmsSpeechActivityDetector : ISpeechActivityDetector
    {
        public SpeechActivityResult Analyze(PcmAudioChunk chunk, VoiceCaptureSettings settings)
        {
            double rms = CalculateRmsPcm16(chunk.Buffer, chunk.BytesRecorded);
            return new SpeechActivityResult
            {
                Rms = rms,
                Threshold = settings.RmsSpeechThreshold,
                IsSpeech = rms >= settings.RmsSpeechThreshold
            };
        }

        public static double CalculateRmsPcm16(byte[] buffer, int bytesRecorded)
        {
            int usable = Math.Max(0, Math.Min(bytesRecorded, buffer.Length));
            usable -= usable % 2;
            if (usable <= 0)
            {
                return 0;
            }

            double sumSquares = 0;
            int sampleCount = usable / 2;
            for (int i = 0; i < usable; i += 2)
            {
                short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                double normalized = sample / 32768.0;
                sumSquares += normalized * normalized;
            }

            return Math.Sqrt(sumSquares / sampleCount);
        }
    }
}
