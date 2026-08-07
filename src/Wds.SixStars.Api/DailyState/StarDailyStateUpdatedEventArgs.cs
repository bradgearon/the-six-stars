using System;

namespace Wds.SixStars.Api.DailyState
{
    public sealed class StarDailyStateUpdatedEventArgs : EventArgs
    {
        public StarDailyStateRecord Record { get; set; } = new StarDailyStateRecord();
        public bool Generated { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}
