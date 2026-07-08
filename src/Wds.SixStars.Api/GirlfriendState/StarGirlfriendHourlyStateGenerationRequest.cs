using System;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.GirlfriendState
{
    public sealed class StarGirlfriendHourlyStateGenerationRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public StarProfile Profile { get; set; } = new StarProfile();
        public StarState State { get; set; } = new StarState();
        public string SlotId { get; set; } = string.Empty;
        public DateTimeOffset LocalNow { get; set; }
        public string Reason { get; set; } = string.Empty;
        public StarGirlfriendHourlyStateContext? PreviousState { get; set; }
    }
}
