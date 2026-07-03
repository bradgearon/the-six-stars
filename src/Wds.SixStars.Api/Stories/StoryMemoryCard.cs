using System.Collections.Generic;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Stories
{
    public sealed class StoryMemoryCard
    {
        public string Id { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public StoryKind Kind { get; set; } = StoryKind.PersonalMemory;
        public string Summary { get; set; } = string.Empty;
        public List<string> Themes { get; set; } = new List<string>();
        public List<string> AllowedUses { get; set; } = new List<string>();
        public List<string> ForbiddenUses { get; set; } = new List<string>();
        public int UnlockedAtBond { get; set; }
    }
}
