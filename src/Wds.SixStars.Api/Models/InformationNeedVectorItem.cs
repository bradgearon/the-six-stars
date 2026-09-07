using System.Text.Json.Serialization;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Compact foreground request for one additional information source.
    /// The model-facing JSON shape is exactly: subject, confidence, score.
    /// </summary>
    public sealed class InformationNeedVectorItem
    {
        [JsonPropertyName("subject")]
        public string Subject { get; set; } = string.Empty;

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("score")]
        public double Score { get; set; }

        // Internal routing fields are derived by the host from Subject and are never part of
        // the foreground LLM response contract. Keeping them avoids breaking saved/runtime code.
        [JsonIgnore]
        public string DataType { get; set; } = string.Empty;

        // Optional compact lookup subject emitted by the delimiter-streaming contract. The public
        // JSON schema intentionally remains subject/confidence/score for older structured paths.
        [JsonIgnore]
        public string Query { get; set; } = string.Empty;

        [JsonIgnore]
        public string Reason { get; set; } = string.Empty;
    }
}
