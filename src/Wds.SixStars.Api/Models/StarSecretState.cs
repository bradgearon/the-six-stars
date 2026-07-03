using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Tracks secrets/lore permissions unlocked with a Star. These are serializable
    /// so Unity can persist them in the save-game chunk.
    /// </summary>
    public sealed class StarSecretState
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTimeOffset UnlockedAt { get; set; } = DateTimeOffset.UtcNow;
        public bool KnownByStar { get; set; } = true;
        public bool KnownByPlayer { get; set; } = true;
        public List<string> Tags { get; set; } = new List<string>();
    }
}
