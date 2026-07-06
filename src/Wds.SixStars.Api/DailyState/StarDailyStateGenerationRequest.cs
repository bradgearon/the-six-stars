using System;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.DailyState
{
    public sealed class StarDailyStateGenerationRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public StarProfile Profile { get; set; } = new StarProfile();
        public StarState State { get; set; } = new StarState();
        public string LocalDate { get; set; } = string.Empty;
        public string SlotId { get; set; } = string.Empty;
        public DateTimeOffset LocalNow { get; set; }
        public StarDailyStateRecord? PreviousRecord { get; set; }
    }
}
