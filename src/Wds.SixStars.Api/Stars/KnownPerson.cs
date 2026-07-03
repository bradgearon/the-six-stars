using System.Collections.Generic;

namespace Wds.SixStars.Api.Stars
{
    public sealed class KnownPerson
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Relationship { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<string> PrivateNotes { get; set; } = new List<string>();
    }
}
