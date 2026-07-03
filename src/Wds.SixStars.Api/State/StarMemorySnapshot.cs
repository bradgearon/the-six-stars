using System.Collections.Generic;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.State
{
    public sealed class StarMemorySnapshot
    {
        public StarState State { get; set; } = new StarState();
        public List<ConversationLogEntry> RecentHistory { get; set; } = new List<ConversationLogEntry>();
    }
}
