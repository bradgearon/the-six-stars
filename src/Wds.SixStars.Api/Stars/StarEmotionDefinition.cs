using System.Collections.Generic;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Stars
{
    public sealed class StarEmotionDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public EmotionValence Valence { get; set; } = EmotionValence.Neutral;
        public List<string> Tags { get; set; } = new List<string>();
    }
}
