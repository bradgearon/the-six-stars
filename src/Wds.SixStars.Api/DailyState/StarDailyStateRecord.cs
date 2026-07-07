using System;

namespace Wds.SixStars.Api.DailyState
{
    public sealed class StarDailyStateRecord
    {
        public string SessionId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string LocalDate { get; set; } = string.Empty;
        public string SlotId { get; set; } = string.Empty;
        public DateTimeOffset GeneratedAt { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        /// <summary>Daily Star state serialized as deterministic JSON for prompt injection and Unity/runtime integrations.</summary>
        public string JsonContent { get; set; } = string.Empty;

        /// <summary>Legacy compatibility only. New daily state writes JSON, not XML.</summary>
        public string XmlContent { get; set; } = string.Empty;
    }
}
