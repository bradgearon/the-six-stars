namespace Wds.SixStars.Api.Stars
{
    /// <summary>
    /// Relationship point threshold and per-turn cap for one deterministic relationship stat.
    /// Scores are accumulated by the deterministic emotion scorer; the LLM does not directly award these points.
    /// </summary>
    public sealed class StarRelationshipStatRule
    {
        public double ScorePerPoint { get; set; } = 80;
        public int MaxPerTurn { get; set; } = 2;
    }
}
