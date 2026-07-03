namespace Wds.SixStars.Api.Speech
{
    public sealed class HotwordDetectionResult
    {
        public bool Detected { get; set; }
        public string StarId { get; set; } = string.Empty;
        public string MatchedAlias { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Source { get; set; } = "unknown";
        public string DebugText { get; set; } = string.Empty;

        public static HotwordDetectionResult None(string source)
        {
            return new HotwordDetectionResult
            {
                Detected = false,
                Confidence = 0,
                Source = source
            };
        }
    }
}
