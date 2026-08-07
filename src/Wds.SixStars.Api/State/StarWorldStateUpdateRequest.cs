using System.Collections.Generic;

namespace Wds.SixStars.Api.State
{
    /// <summary>
    /// Host/gameplay state update. Unity should call this when a Star is met, becomes an ally,
    /// loses ally status, or similar story/world flags change outside normal conversation.
    /// </summary>
    public sealed class StarWorldStateUpdateRequest
    {
        public string SessionId { get; set; } = "local-dev";
        public string StarId { get; set; } = string.Empty;
        public bool? IsMet { get; set; }
        public bool? IsAlly { get; set; }
        public string Reason { get; set; } = string.Empty;
        public List<string> RelationshipEvents { get; set; } = new List<string>();
    }
}
