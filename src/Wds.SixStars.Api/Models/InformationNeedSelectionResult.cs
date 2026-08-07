using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Quick selector output used between the fast spoken response and the deep information pass.
    /// The AI may suggest the top area, but the API/runtime still makes the deterministic final
    /// decision using local availability, thresholds, and this ranked vector.
    /// </summary>
    public sealed class InformationNeedSelectionResult
    {
        public bool ShouldRequestMoreInformation { get; set; }
        public string SelectedDataType { get; set; } = string.Empty;
        public string Subject { get; set; } = "self";
        public double Confidence { get; set; }
        public double Score { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public List<InformationNeedVectorItem> RankedNeeds { get; set; } = new List<InformationNeedVectorItem>();
    }
}
