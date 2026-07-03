using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    public sealed class UnityEventEnvelope
    {
        public string Type { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public Dictionary<string, string> Data { get; set; } = new Dictionary<string, string>();
    }
}
