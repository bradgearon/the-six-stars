using System;

namespace Wds.SixStars.Api.Voice
{
    public sealed class VoiceSynthesisResult
    {
        public string StarId { get; set; } = string.Empty;
        public string VoiceId { get; set; } = string.Empty;
        public string Instructions { get; set; } = string.Empty;
        public byte[] WavAudio { get; set; } = new byte[0];
        public string MimeType { get; set; } = "audio/wav";
        public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
        public double DurationMs { get; set; }
        public string Source { get; set; } = "unknown";
    }
}
