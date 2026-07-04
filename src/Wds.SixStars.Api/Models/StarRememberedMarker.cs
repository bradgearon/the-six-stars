using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Exact user-requested memory marker. This is for things like coordinates, named locations,
    /// where the player says something is, and the exact description the player asked a Star to remember.
    /// It is stored in StarState and sent back in future prompts so the Star can remind the player later.
    /// </summary>
    public sealed class StarRememberedMarker
    {
        public string Id { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string MarkerName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string LocationText { get; set; } = string.Empty;
        public string Coordinates { get; set; } = string.Empty;
        public string Area { get; set; } = string.Empty;
        public string SourcePlayerQuote { get; set; } = string.Empty;
        public string ReminderText { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastReferencedAt { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
    }
}
