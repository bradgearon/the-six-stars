using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.SpeechIntents
{
    public static class StarSpeechIntentPriorities
    {
        public const string Low = "low";
        public const string Medium = "medium";
        public const string High = "high";
        public const string Critical = "critical";

        public static string Normalize(string? value)
        {
            string text = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (text == Critical) return Critical;
            if (text == High) return High;
            if (text == Medium) return Medium;
            return Low;
        }

        public static int Rank(string? value)
        {
            switch (Normalize(value))
            {
                case Critical: return 4;
                case High: return 3;
                case Medium: return 2;
                default: return 1;
            }
        }
    }

    public static class StarSpeechIntentStatuses
    {
        public const string Pending = "pending";
        public const string Preparing = "preparing";
        public const string Ready = "ready";
        public const string Leased = "leased";
        public const string Playing = "playing";
        public const string Spoken = "spoken";
        public const string Dismissed = "dismissed";
        public const string Expired = "expired";
        public const string Failed = "failed";
        public const string Superseded = "superseded";
    }

    /// <summary>LLM-authored candidate stored inside daily-state output.</summary>
    public sealed class StarSpeechIntentCandidate
    {
        public string IntentId { get; set; } = string.Empty;
        public string Category { get; set; } = "daily_state";
        public string Topic { get; set; } = string.Empty;
        public string SpeechGoal { get; set; } = string.Empty;
        public double BaseScore { get; set; } = 50;
        public string Priority { get; set; } = StarSpeechIntentPriorities.Medium;
        public double Confidence { get; set; } = 0.75;
        public List<string> SourceEventIds { get; set; } = new List<string>();
        public string DedupeKey { get; set; } = string.Empty;
        public int ExpiresAfterHours { get; set; } = 24;
        public bool RequiresAcknowledgement { get; set; } = true;
    }

    public sealed class StarSpeechIntent
    {
        public string IntentId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string Category { get; set; } = "daily_state";
        public string Topic { get; set; } = string.Empty;
        public string SpeechGoal { get; set; } = string.Empty;
        public double BaseScore { get; set; } = 50;
        public double EffectiveScore { get; set; } = 50;
        public string Priority { get; set; } = StarSpeechIntentPriorities.Medium;
        public double Confidence { get; set; } = 0.75;
        public List<string> SourceEventIds { get; set; } = new List<string>();
        public string DedupeKey { get; set; } = string.Empty;
        public bool RequiresAcknowledgement { get; set; } = true;
        public string Status { get; set; } = StarSpeechIntentStatuses.Pending;
        public string PreparedArtifactId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? LeaseExpiresAt { get; set; }
        public DateTimeOffset? PlaybackStartedAt { get; set; }
        public DateTimeOffset? SpokenAt { get; set; }
        public int PrepareAttempts { get; set; }
        public int PlayAttempts { get; set; }
        public string LastError { get; set; } = string.Empty;
    }

    public sealed class StarSpeechIntentManifest
    {
        public bool Ok { get; set; } = true;
        public DateTimeOffset GeneratedAt { get; set; }
        public List<StarSpeechIntent> Intents { get; set; } = new List<StarSpeechIntent>();
        public double HighestEffectiveScore { get; set; }
        public string HighestPriority { get; set; } = StarSpeechIntentPriorities.Low;
    }

    public sealed class StarSpeechIntentChangedEventArgs : EventArgs
    {
        public StarSpeechIntent Intent { get; set; } = new StarSpeechIntent();
        public string ChangeType { get; set; } = string.Empty;
    }
}
