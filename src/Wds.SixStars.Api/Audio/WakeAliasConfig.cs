using System.Collections.Generic;

namespace Wds.SixStars.Api.Audio
{
    public sealed class WakeAliasConfig
    {
        public List<string> EquippedStars { get; set; } = new List<string>();
        public Dictionary<string, List<string>> WakeAliases { get; set; } = new Dictionary<string, List<string>>();
        public VoiceCaptureSettings Audio { get; set; } = new VoiceCaptureSettings();
    }
}
