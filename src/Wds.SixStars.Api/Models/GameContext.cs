using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    public sealed class GameContext
    {
        public string Area { get; set; } = "DesktopDevHarness";
        public string Quest { get; set; } = "FreeTalk";
        public string BattleState { get; set; } = "Safe";
        public bool RomanceMode { get; set; }
        public int BenaveilPressureLevel { get; set; }
        public List<string> EquippedStars { get; set; } = new List<string>();
        public List<string> RecentEvents { get; set; } = new List<string>();
        public Dictionary<string, string> Flags { get; set; } = new Dictionary<string, string>();
    }
}
