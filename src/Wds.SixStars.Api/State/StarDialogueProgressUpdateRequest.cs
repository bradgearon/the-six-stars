using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.State
{
    public sealed class StarDialogueProgressUpdateRequest
    {
        public string SessionId { get; set; } = "local-dev";
        public string StarId { get; set; } = string.Empty;
        public StarDialogueProgressState DialogueState { get; set; } = new StarDialogueProgressState();
    }
}
