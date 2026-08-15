using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wds.SixStars.Api.Screen;

namespace Wds.SixStars.Api.Models
{
    public sealed class StarInteractionRequest
    {
        public string SessionId { get; set; } = "local-dev";
        public string StarId { get; set; } = "Elusiaa";
        public string Speaker { get; set; } = "LastCat";
        public RequestType RequestType { get; set; } = RequestType.Chat;
        public string PlayerText { get; set; } = string.Empty;
        public GameContext GameContext { get; set; } = new GameContext();
        public StoryOptions StoryOptions { get; set; } = new StoryOptions();
        public StarPhotoAttachment AttachedPhoto { get; set; } = new StarPhotoAttachment();

        /// <summary>
        /// Optional host callback for normal foreground speech. The streaming LLM path invokes this
        /// as soon as a complete delimiter-terminated spoken chunk is available. The callback must
        /// return quickly; hosts should enqueue work rather than synthesize/play audio inline.
        /// Story and StarInitiative requests keep their existing structured response contracts.
        /// </summary>
        [JsonIgnore]
        public Action<string>? SpokenChunkReady { get; set; }

        /// <summary>
        /// Recent safe summaries of the user's screen. The API keeps the last five per session,
        /// but callers can also pass explicit recent captures here for stateless integrations.
        /// </summary>
        public List<ScreenCaptureSummary> RecentScreenCaptures { get; set; } = new List<ScreenCaptureSummary>();
    }
}
