using System.Collections.Generic;

namespace Wds.SixStars.Api.Stars
{
    /// <summary>
    /// Author-facing rule that decides whether a completed conversation with this Star should be
    /// allowed to ripple to another Star. The LLM only creates the small relay text and target emotion
    /// vectors; this rule keeps the inter-Star gossip/empathy system bounded and game-state driven.
    /// </summary>
    public sealed class StarInterstarInfluenceRule
    {
        public string TargetStarId { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public bool RequiresOriginAlly { get; set; } = true;
        public bool RequiresTargetMet { get; set; } = true;
        public bool RequiresTargetAlly { get; set; }
        public double MinOriginConnectionPercent { get; set; }
        public double MinTargetConnectionPercent { get; set; }
        public double RelayWeight { get; set; } = 1.0;
        public double PositiveMultiplier { get; set; } = 1.0;
        public double NegativeMultiplier { get; set; } = 1.0;
        public int CooldownSeconds { get; set; } = 600;
        public int MaxPerCycle { get; set; } = 1;
        public List<string> TopicTags { get; set; } = new List<string>();
    }
}
