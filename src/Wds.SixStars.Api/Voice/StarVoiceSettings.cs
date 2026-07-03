using System.Collections.Generic;

namespace Wds.SixStars.Api.Voice
{
    public sealed class StarVoiceSettings
    {
        public string StarId { get; set; } = string.Empty;
        public string VoiceId { get; set; } = string.Empty;
        public string Style { get; set; } = "neutral";
        public string VoiceDesignPrompt { get; set; } = string.Empty;
        public string VoiceCloneAudioFile { get; set; } = string.Empty;
        public string VoiceCloneRefText { get; set; } = string.Empty;
        public bool XVectorOnly { get; set; }
        public string DeliveryInstructions { get; set; } = string.Empty;
        public string PrimaryEmotion { get; set; } = "neutral";
        public string Pace { get; set; } = "normal";
        public string PitchDescription { get; set; } = "natural";
        public string Volume { get; set; } = "normal";
        public string Prosody { get; set; } = string.Empty;
        public string Language { get; set; } = "Auto";
        public List<string> AudioTags { get; set; } = new List<string>();
        public List<string> Avoid { get; set; } = new List<string>();
        public double Speed { get; set; } = 1.0;
        public double Pitch { get; set; } = 1.0;
        public double EmotionIntensity { get; set; } = 0.5;
    }
}
