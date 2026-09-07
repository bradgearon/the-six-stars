using System;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Durable human-readable conversation rollup. Ten-turn blocks are generated every tenth
    /// direct interaction; hundred-turn blocks are generated every hundredth interaction from the
    /// ten-turn blocks. Source conversation entries remain available in the normal durable archive.
    /// </summary>
    public sealed class StarConversationSummaryBlock
    {
        public int StartTurn { get; set; }
        public int EndTurn { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string Summary { get; set; } = string.Empty;
    }
}
