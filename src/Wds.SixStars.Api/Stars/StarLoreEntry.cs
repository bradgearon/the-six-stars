using System.Collections.Generic;

namespace Wds.SixStars.Api.Stars
{
    public sealed class StarLoreEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<string> Themes { get; set; } = new List<string>();
        public int UnlockedAtBond { get; set; }
    }
}
