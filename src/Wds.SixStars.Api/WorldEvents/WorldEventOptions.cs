using System.Collections.Generic;

namespace Wds.SixStars.Api.WorldEvents
{
    public sealed class WorldEventOptions
    {
        public bool Enabled { get; set; } = true;
        public string StateRoot { get; set; } = "state/world-events";
        public int MaximumStoredEvents { get; set; } = 2000;
        public int PromptEventCount { get; set; } = 32;
        public int RefreshDebounceSeconds { get; set; } = 8;
        public string ApiKey { get; set; } = string.Empty;
        public List<WorldEventFeedOptions> Feeds { get; set; } = new List<WorldEventFeedOptions>();
    }

    public sealed class WorldEventFeedOptions
    {
        public string FeedId { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public int PollSeconds { get; set; } = 20;
        public string ApiKey { get; set; } = string.Empty;
    }
}
