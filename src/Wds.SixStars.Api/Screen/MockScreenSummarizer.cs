using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Screen
{
    public sealed class MockScreenSummarizer : IScreenSummarizer
    {
        public Task<ScreenCaptureSummary> SummarizeAsync(ScreenSummarizationRequest request, CancellationToken cancellationToken)
        {
            ScreenImageCapture capture = request.Capture;
            string app = string.IsNullOrWhiteSpace(capture.ActiveApp) ? capture.ActiveProcessName : capture.ActiveApp;
            string title = capture.ActiveWindowTitle ?? string.Empty;
            string activity = GuessActivity(app, title);

            ScreenCaptureSummary summary = new ScreenCaptureSummary
            {
                CapturedAt = capture.CapturedAt,
                ActiveApp = app,
                ActivityType = activity,
                LikelyActivity = "The user is active in " + (string.IsNullOrWhiteSpace(app) ? "an unknown app" : app) + ".",
                CompanionSafeSummary = "The user appears to be working in " + (string.IsNullOrWhiteSpace(app) ? "a desktop app" : app) + (string.IsNullOrWhiteSpace(title) ? "." : " with a window titled '" + title + "'."),
                Sensitivity = "normal",
                ImageReference = capture.ImageReference,
                Confidence = 0.45
            };
            if (!string.IsNullOrWhiteSpace(title)) summary.VisibleTextHighlights.Add(title);
            return Task.FromResult(summary);
        }

        private static string GuessActivity(string app, string title)
        {
            string combined = (app + " " + title).ToLowerInvariant();
            if (combined.Contains("visual studio") || combined.Contains("code") || combined.Contains("rider") || combined.Contains(".cs") || combined.Contains("github")) return "programming";
            if (combined.Contains("word") || combined.Contains("notepad") || combined.Contains("obsidian")) return "writing";
            if (combined.Contains("chrome") || combined.Contains("edge") || combined.Contains("firefox")) return "browsing";
            if (combined.Contains("discord") || combined.Contains("slack") || combined.Contains("teams")) return "chat";
            if (combined.Contains("gmail") || combined.Contains("outlook")) return "email";
            return "unknown";
        }
    }
}
