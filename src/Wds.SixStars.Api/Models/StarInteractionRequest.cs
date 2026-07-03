using System.Collections.Generic;
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

        /// <summary>
        /// Recent safe summaries of the user's screen. The API keeps the last five per session,
        /// but callers can also pass explicit recent captures here for stateless integrations.
        /// </summary>
        public List<ScreenCaptureSummary> RecentScreenCaptures { get; set; } = new List<ScreenCaptureSummary>();
    }
}
