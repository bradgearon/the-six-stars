using Wds.SixStars.Api.Audio;

namespace Wds.SixStars.Api.Speech
{
    public sealed class AudioHotwordDetectionRequest
    {
        public SpeechSegment ProbeSegment { get; set; } = new SpeechSegment();
        public WakeAliasConfig WakeAliases { get; set; } = new WakeAliasConfig();
        public string PreferredStarId { get; set; } = string.Empty;
        public int ProbeIndex { get; set; }
        public double ProbeWindowEndMs { get; set; }
        public bool IsFinalProbe { get; set; }
    }
}
