using System;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.GirlfriendState
{
    public sealed class StarGirlfriendHourlyStateUpdatedEventArgs : EventArgs
    {
        public string SessionId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public StarGirlfriendHourlyStateContext State { get; set; } = new StarGirlfriendHourlyStateContext();
    }
}
