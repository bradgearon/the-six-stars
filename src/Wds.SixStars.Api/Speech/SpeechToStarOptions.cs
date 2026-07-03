using Wds.SixStars.Api.Audio;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Speech
{
    public sealed class SpeechToStarOptions
    {
        public string SessionId { get; set; } = "desktop-local-dev";
        public string Speaker { get; set; } = "LastCat";
        public string DefaultStarId { get; set; } = "Elusiaa";
        public RequestType RequestType { get; set; } = RequestType.Chat;
        public bool RequireWakeWord { get; set; } = false;
        public WakeAliasConfig WakeAliases { get; set; } = new WakeAliasConfig();
        public GameContext GameContext { get; set; } = new GameContext();
        public StoryOptions StoryOptions { get; set; } = new StoryOptions();
    }
}
