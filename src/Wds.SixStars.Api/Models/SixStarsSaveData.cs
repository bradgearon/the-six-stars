using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Serializable save chunk for the Six Stars relationship/memory system.
    /// The desktop UI stores this as JSON. Unity can embed this object in its own save file.
    /// </summary>
    public sealed class SixStarsSaveData
    {
        public int Version { get; set; } = 1;
        public string SessionId { get; set; } = "local-dev";
        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
        public List<StarState> Stars { get; set; } = new List<StarState>();
    }
}
