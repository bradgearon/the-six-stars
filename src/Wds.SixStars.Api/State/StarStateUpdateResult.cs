using System.Collections.Generic;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.State
{
    public sealed class StarStateUpdateResult
    {
        public StarState State { get; set; } = new StarState();
        public ConversationLogEntry LogEntry { get; set; } = new ConversationLogEntry();
        public List<UnityEventEnvelope> UnityEvents { get; set; } = new List<UnityEventEnvelope>();
    }
}
