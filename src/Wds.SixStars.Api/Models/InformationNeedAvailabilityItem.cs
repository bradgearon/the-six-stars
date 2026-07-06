using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Deterministic local inventory for one information area. The foreground Star may return
    /// a whole uncertain information-needs vector; the runtime uses this inventory to decide
    /// whether a second/deep pass can actually be grounded in local data. Unity-compatible POCO.
    /// </summary>
    public sealed class InformationNeedAvailabilityItem
    {
        public string DataType { get; set; } = string.Empty;
        public bool Available { get; set; }
        public int Count { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<string> SampleSubjects { get; set; } = new List<string>();
    }
}
