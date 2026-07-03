using System.Collections.Generic;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.State
{
    public sealed class StarInteractionHistoryPage
    {
        public string SessionId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public int Skip { get; set; }
        public int Take { get; set; }
        public int TotalCount { get; set; }
        public List<ConversationLogEntry> Entries { get; set; } = new List<ConversationLogEntry>();
    }
}
