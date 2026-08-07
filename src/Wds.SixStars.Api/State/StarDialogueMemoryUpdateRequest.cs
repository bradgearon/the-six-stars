using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.State
{
    public sealed class StarDialogueMemoryUpdateRequest
    {
        public string SessionId { get; set; } = "local-dev";
        public string StarId { get; set; } = string.Empty;
        public StarDialogueMemoryEntry Entry { get; set; } = new StarDialogueMemoryEntry();
    }
}
