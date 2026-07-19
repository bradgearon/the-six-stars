using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    public sealed class StarDecisionScoreRequest
    {
        public string SessionId { get; set; } = "local-dev";
        public string StarId { get; set; } = "Elusiaa";
        public string SceneKey { get; set; } = string.Empty;
        public string CharacterAttributesOrder { get; set; } = string.Empty;
        public string CharacterAttributes { get; set; } = string.Empty;
        public string RelationshipState { get; set; } = string.Empty;
        public string PlayerRecentAction { get; set; } = string.Empty;
        public string RequestedScoreOrder { get; set; } = string.Empty;
        public string AdditionalInstructions { get; set; } = string.Empty;
        public Dictionary<string, string> SceneFacts { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> AdditionalData { get; set; } = new Dictionary<string, string>();
        public GameContext GameContext { get; set; } = new GameContext();
    }
}
