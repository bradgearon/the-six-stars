using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.XPosting
{
    public sealed class StarXPostingOptions
    {
        public bool Enabled { get; set; }
        public string StateRoot { get; set; } = "state/x-posting";
        public int CandidateLimit { get; set; } = 500;
        public int DelayBetweenPostsSeconds { get; set; } = 120;
        public int RequestTimeoutSeconds { get; set; } = 45;
        public int PollSeconds { get; set; } = 60;
        public int RetryDelaySeconds { get; set; } = 300;
        public int OAuthAuthorizationTimeoutSeconds { get; set; } = 180;
        public int OAuthTokenRefreshSkewSeconds { get; set; } = 120;
        public string OAuthScopes { get; set; } = "tweet.read tweet.write users.read offline.access";
        public List<StarXPostingScheduleSlot> RefreshSlots { get; set; } = CreateDefaultSlots();
        public List<StarXAccountOptions> StarAccounts { get; set; } = new List<StarXAccountOptions>();

        public static List<StarXPostingScheduleSlot> CreateDefaultSlots()
        {
            return new List<StarXPostingScheduleSlot>
            {
                new StarXPostingScheduleSlot { SlotId = "overnight", StartsAt = "00:00", RefreshBefore = "06:00" },
                new StarXPostingScheduleSlot { SlotId = "morning", StartsAt = "06:00", RefreshBefore = "12:00" },
                new StarXPostingScheduleSlot { SlotId = "afternoon", StartsAt = "12:00", RefreshBefore = "18:00" },
                new StarXPostingScheduleSlot { SlotId = "evening", StartsAt = "18:00", RefreshBefore = "00:00" }
            };
        }
    }

    public sealed class StarXPostingScheduleSlot
    {
        public string SlotId { get; set; } = string.Empty;
        public string StartsAt { get; set; } = string.Empty;
        public string RefreshBefore { get; set; } = string.Empty;
    }

    public sealed class StarXAccountOptions
    {
        /// <summary>Lower-case Star id, for example elusiaa.</summary>
        public string Id { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        /// <summary>OAuth 2.0 Client ID from the X Developer Console.</summary>
        public string ClientId { get; set; } = string.Empty;
        /// <summary>Exact loopback callback registered for this X app.</summary>
        public string RedirectUri { get; set; } = string.Empty;

        public bool HasClientConfiguration => !string.IsNullOrWhiteSpace(ClientId)
            && Uri.TryCreate(RedirectUri, UriKind.Absolute, out Uri? callback)
            && callback.IsLoopback
            && callback.Port > 0
            && string.Equals(callback.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class StarXOAuth2Token
    {
        public string AccountId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public string TokenType { get; set; } = "bearer";
        public string Scope { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public bool HasRefreshToken => !string.IsNullOrWhiteSpace(RefreshToken);

        public bool NeedsRefresh(DateTimeOffset now, TimeSpan skew)
        {
            return string.IsNullOrWhiteSpace(AccessToken) || ExpiresAt <= now.Add(skew);
        }
    }

    public sealed class StarXOAuth2AuthorizationResult
    {
        public bool Success { get; set; }
        public string AccountId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTimeOffset? ExpiresAt { get; set; }
    }

    public sealed class StarXPostSelectionItem
    {
        public string WritingId { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    public sealed class StarXPostSelectionResult
    {
        public string StarId { get; set; } = string.Empty;
        public List<StarXPostSelectionItem> Posts { get; set; } = new List<StarXPostSelectionItem>();
        public string RawModelJson { get; set; } = string.Empty;
    }

    public sealed class StarXPostApiResult
    {
        public bool Success { get; set; }
        public string PostId { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public int StatusCode { get; set; }
        public string Error { get; set; } = string.Empty;
    }

    public sealed class StarXPostingRunItemResult
    {
        public string WritingId { get; set; } = string.Empty;
        public string PostText { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string XPostId { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }

    public sealed class StarXPostingProgress
    {
        public string StarId { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public int CurrentPostNumber { get; set; }
        public int TotalPosts { get; set; }
        public int PostedCount { get; set; }
        public int FailedCount { get; set; }
        public int DelaySeconds { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public sealed class StarXPostingRunResult
    {
        public string StarId { get; set; } = string.Empty;
        public int CandidateCount { get; set; }
        public int RequestedCount { get; set; }
        public int AttemptedCount { get; set; }
        public int PostedCount { get; set; }
        public int FailedCount { get; set; }
        public bool BatchCompleted { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<StarXPostingRunItemResult> Items { get; set; } = new List<StarXPostingRunItemResult>();
    }

    public sealed class StarXPostingScheduleState
    {
        public int Version { get; set; } = 1;
        public DateTimeOffset UpdatedAt { get; set; }
        public List<StarXPostingSlotRunRecord> CompletedRuns { get; set; } = new List<StarXPostingSlotRunRecord>();
    }

    public sealed class StarXPostingSlotRunRecord
    {
        public string LocalDate { get; set; } = string.Empty;
        public string SlotId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public DateTimeOffset CompletedAt { get; set; }
        public int PostedCount { get; set; }
        public int FailedCount { get; set; }
    }
}
