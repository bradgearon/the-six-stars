using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Unity-save-friendly record of something the player taught this Star.
    /// This can be a social lesson, a new story pattern, a spell/ability idea,
    /// a debugging trick, or any durable function the AI should remember.
    /// </summary>
    public sealed class StarLearnedFunction
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string SourcePlayerQuote { get; set; } = string.Empty;
        public DateTimeOffset LearnedAt { get; set; } = DateTimeOffset.UtcNow;
        public double Confidence { get; set; }
        public bool IsActive { get; set; } = true;
        public List<string> Tags { get; set; } = new List<string>();
    }
}
