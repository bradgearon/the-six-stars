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

        /// <summary>
        /// Immutable logical start time for this player turn. Conversation history must use this
        /// instead of the later background-review completion time, because a preempted/retried
        /// background review can settle after a newer turn.
        /// </summary>
        public DateTimeOffset TurnStartedAt { get; set; } = DateTimeOffset.UtcNow;

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
        /// Optional host callback for fully received non-speech control chunks from the fast stream.
        /// The mediator invokes this only after the complete §-terminated vector has arrived.
        /// </summary>
        [JsonIgnore]
        public Action<StarStreamAction>? StreamActionReady { get; set; }

        /// <summary>
        /// Recent safe summaries of the user's screen. The API keeps the last five per session,
        /// but callers can also pass explicit recent captures here for stateless integrations.
        /// </summary>
        public List<ScreenCaptureSummary> RecentScreenCaptures { get; set; } = new List<ScreenCaptureSummary>();
    }
}
