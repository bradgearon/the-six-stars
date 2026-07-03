using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Screen
{
    public sealed class ScreenAwarenessOptions
    {
        public bool Enabled { get; set; } = true;
        public TimeSpan CaptureInterval { get; set; } = TimeSpan.FromMinutes(5);
        public bool CaptureImmediatelyOnStart { get; set; } = true;
        public bool StoreRawImageBase64 { get; set; }
        public int MaxConsecutiveFailures { get; set; } = 3;
        public List<string> BlockedWindowTitleKeywords { get; set; } = new List<string>
        {
            "password", "bank", "tax", "medical", "login", "secret", "token", "api key", "private key"
        };
        public List<string> BlockedProcessNames { get; set; } = new List<string>
        {
            "1Password", "Bitwarden", "KeePass", "Signal", "WhatsApp"
        };
    }
}
