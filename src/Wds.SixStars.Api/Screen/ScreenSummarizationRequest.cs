using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Screen
{
    public sealed class ScreenSummarizationRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public ScreenImageCapture Capture { get; set; } = new ScreenImageCapture();
        public bool IncludeBase64InSummary { get; set; }
        public string PromptOverride { get; set; } = string.Empty;
        public List<string> BlockedWindowTitleKeywords { get; set; } = new List<string>();
        public List<string> BlockedProcessNames { get; set; } = new List<string>();

        public bool IsBlockedByPolicy()
        {
            string title = Capture.ActiveWindowTitle ?? string.Empty;
            string process = Capture.ActiveProcessName ?? Capture.ActiveApp ?? string.Empty;

            foreach (string blocked in BlockedWindowTitleKeywords)
            {
                if (!string.IsNullOrWhiteSpace(blocked) && title.IndexOf(blocked, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            foreach (string blocked in BlockedProcessNames)
            {
                if (!string.IsNullOrWhiteSpace(blocked) && process.IndexOf(blocked, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
