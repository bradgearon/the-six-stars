namespace Wds.SixStars.Api.Stars
{
    /// <summary>
    /// Per-Star deterministic relationship thresholds. Heart/Trust usually support ally growth;
    /// Romance supports girlfriend readiness when romance mode and relationship gates allow it.
    /// </summary>
    public sealed class StarRelationshipStatsConfig
    {
        public StarRelationshipStatRule Heart { get; set; } = new StarRelationshipStatRule { ScorePerPoint = 80, MaxPerTurn = 2 };
        public StarRelationshipStatRule Trust { get; set; } = new StarRelationshipStatRule { ScorePerPoint = 90, MaxPerTurn = 2 };
        public StarRelationshipStatRule Romance { get; set; } = new StarRelationshipStatRule { ScorePerPoint = 120, MaxPerTurn = 1 };
    }
}
