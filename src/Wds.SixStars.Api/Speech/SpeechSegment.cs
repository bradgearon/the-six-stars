using System;
using Wds.SixStars.Api.Audio;

namespace Wds.SixStars.Api.Speech
{
    public sealed class SpeechSegment
    {
        public string SegmentId { get; set; } = Guid.NewGuid().ToString("N");
        public byte[] Pcm16Audio { get; set; } = new byte[0];
        public PcmAudioFormat Format { get; set; } = PcmAudioFormat.Mono16Khz();
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset EndedAt { get; set; } = DateTimeOffset.UtcNow;
        public double PeakRms { get; set; }
        public int AudioChunkCount { get; set; }
        public string Source { get; set; } = "unknown";

        public double DurationMs
        {
            get
            {
                if (Format.BytesPerSecond <= 0)
                {
                    return 0;
                }

                return (double)Pcm16Audio.Length / Format.BytesPerSecond * 1000.0;
            }
        }
    }
}
