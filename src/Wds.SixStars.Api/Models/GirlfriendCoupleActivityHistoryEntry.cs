using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Durable girlfriend-mode log of couple activities detected in player text and/or Star foreground speech.
    /// The vector uses compact girlfriend couple activity ids and deterministic score/confidence values.
    /// </summary>
    public sealed class GirlfriendCoupleActivityHistoryEntry
    {
        public string Id { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string PlayerText { get; set; } = string.Empty;
        public string StarResponse { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string RawVector { get; set; } = string.Empty;
        public List<GirlfriendVectorItem> ActivityVector { get; set; } = new List<GirlfriendVectorItem>();
    }
}
