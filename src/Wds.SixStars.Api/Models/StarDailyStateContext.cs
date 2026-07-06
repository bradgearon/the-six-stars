using System;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Compact pointer to the generated per-Star daily XML state. The XML file lives beside
    /// the durable Star save state for desktop/prebuilt builds, while Unity can serialize
    /// these simple fields or ignore the file path and provide its own XML string.
    /// </summary>
    public sealed class StarDailyStateContext
    {
        public bool IsAvailable { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string LocalDate { get; set; } = string.Empty;
        public string SlotId { get; set; } = string.Empty;
        public DateTimeOffset? GeneratedAt { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string Xml { get; set; } = string.Empty;
    }
}
