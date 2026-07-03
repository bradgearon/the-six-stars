using System.Collections.Generic;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Voice
{
    public sealed class VoiceSynthesisRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public List<string> TextParts { get; set; } = new List<string>();
        public StarInteractionResponse? SourceResponse { get; set; }
        public StarVoiceSettings VoiceSettings { get; set; } = new StarVoiceSettings();
    }
}
