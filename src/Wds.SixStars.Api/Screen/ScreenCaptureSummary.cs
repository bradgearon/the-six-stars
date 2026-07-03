using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Screen
{
    public sealed class ScreenCaptureSummary
    {
        public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
        public string ActiveApp { get; set; } = string.Empty;
        public string ActivityType { get; set; } = "unknown";
        public string LikelyActivity { get; set; } = string.Empty;
        public string CompanionSafeSummary { get; set; } = string.Empty;
        public List<string> VisibleTextHighlights { get; set; } = new List<string>();
        public List<string> VisibleProblems { get; set; } = new List<string>();
        public string Sensitivity { get; set; } = "normal";
        public string ImageReference { get; set; } = string.Empty;
        public string ImageBase64Jpeg { get; set; } = string.Empty;
        public double Confidence { get; set; }
    }
}
