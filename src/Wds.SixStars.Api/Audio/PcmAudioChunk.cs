using System;

namespace Wds.SixStars.Api.Audio
{
    /// <summary>
    /// A small PCM audio frame. Unity can push frames from Microphone.GetData after converting floats to PCM16.
    /// A desktop microphone adapter can push WASAPI/NAudio frames into the same type.
    /// </summary>
    public sealed class PcmAudioChunk
    {
        public byte[] Buffer { get; set; } = new byte[0];
        public int BytesRecorded { get; set; }
        public PcmAudioFormat Format { get; set; } = PcmAudioFormat.Mono16Khz();
        public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
        public string Source { get; set; } = "unknown";
        public bool IsEndOfStream { get; set; }

        public double DurationMs
        {
            get
            {
                if (Format.BytesPerSecond <= 0)
                {
                    return 0;
                }

                return (double)BytesRecorded / Format.BytesPerSecond * 1000.0;
            }
        }

        public PcmAudioChunk CloneRecordedBytes()
        {
            int count = Math.Max(0, Math.Min(BytesRecorded, Buffer.Length));
            byte[] copy = new byte[count];
            if (count > 0)
            {
                Array.Copy(Buffer, 0, copy, 0, count);
            }

            return new PcmAudioChunk
            {
                Buffer = copy,
                BytesRecorded = count,
                Format = Format,
                CapturedAt = CapturedAt,
                Source = Source,
                IsEndOfStream = IsEndOfStream
            };
        }
    }
}
