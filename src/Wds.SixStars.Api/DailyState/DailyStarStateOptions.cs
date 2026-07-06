using System.Collections.Generic;

namespace Wds.SixStars.Api.DailyState
{
    public sealed class DailyStarStateOptions
    {
        public bool Enabled { get; set; } = true;
        public bool GenerateForAllStars { get; set; } = true;
        public bool RefreshSelectedStarOnRequest { get; set; } = true;
        public string StateRoot { get; set; } = "state/daily-star-state";
        public int PollSeconds { get; set; } = 180;
        public int MaxXmlCharsForPrompt { get; set; } = 12000;
        public List<string> StarIds { get; set; } = new List<string>();
        public List<DailyStarStateScheduleSlot> RefreshSlots { get; set; } = CreateDefaultSlots();

        public static List<DailyStarStateScheduleSlot> CreateDefaultSlots()
        {
            return new List<DailyStarStateScheduleSlot>
            {
                new DailyStarStateScheduleSlot { SlotId = "morning", StartsAt = "00:00", RefreshBefore = "10:00" },
                new DailyStarStateScheduleSlot { SlotId = "midday", StartsAt = "10:00", RefreshBefore = "15:00" },
                new DailyStarStateScheduleSlot { SlotId = "night", StartsAt = "15:00", RefreshBefore = "19:00" }
            };
        }
    }

    public sealed class DailyStarStateScheduleSlot
    {
        public string SlotId { get; set; } = string.Empty;
        public string StartsAt { get; set; } = string.Empty;
        public string RefreshBefore { get; set; } = string.Empty;
    }
}
