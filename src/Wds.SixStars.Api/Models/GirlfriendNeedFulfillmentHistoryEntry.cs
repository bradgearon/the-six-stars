using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Durable girlfriend-mode history entry for needs/wants the latest player turn appears to have met.
    /// The model only marks items from the current girlfriend vectors; the API accumulates these into
    /// performance vectors used by future girlfriend prompts.
    /// </summary>
    public sealed class GirlfriendNeedFulfillmentHistoryEntry
    {
        public string Id { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string PlayerText { get; set; } = string.Empty;
        public string StarResponse { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<GirlfriendVectorItem> MetNeedVector { get; set; } = new List<GirlfriendVectorItem>();
        public List<GirlfriendVectorItem> MetWantVector { get; set; } = new List<GirlfriendVectorItem>();
    }
}
