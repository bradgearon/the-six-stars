using System.Collections.Generic;

namespace Wds.SixStars.Api.DailyState
{
    public sealed class DailyStarStateOptions
    {
        public bool Enabled { get; set; } = true;
        public bool GenerateForAllStars { get; set; } = true;
        public bool RefreshSelectedStarOnRequest { get; set; } = true;
        public string StateRoot { get; set; } = "state/daily-star-state";
        public int PollSeconds { get; set; } = 60;
        public int MaxJsonCharsForPrompt { get; set; } = 12000;
        /// <summary>Legacy alias for older config files; daily state is now JSON.</summary>
        public int MaxXmlCharsForPrompt { get; set; } = 12000;
        public List<string> StarIds { get; set; } = new List<string>();
        public List<DailyStarStateScheduleSlot> RefreshSlots { get; set; } = CreateDefaultSlots();

        public static List<DailyStarStateScheduleSlot> CreateDefaultSlots()
        {
            return new List<DailyStarStateScheduleSlot>
            {
                new DailyStarStateScheduleSlot { SlotId = "overnight", StartsAt = "00:00", RefreshBefore = "06:00" },
                new DailyStarStateScheduleSlot { SlotId = "morning", StartsAt = "06:00", RefreshBefore = "12:00" },
                new DailyStarStateScheduleSlot { SlotId = "afternoon", StartsAt = "12:00", RefreshBefore = "18:00" },
                new DailyStarStateScheduleSlot { SlotId = "evening", StartsAt = "18:00", RefreshBefore = "00:00" }
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
