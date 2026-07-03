using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Structured voice-performance data produced by the Star LLM and consumed by the TTS layer.
    /// Keep this in Models so UI, Unity, and Voice can all use the same contract.
    /// </summary>
    public sealed class VoiceDirective
    {
        public string VoiceId { get; set; } = string.Empty;

        /// <summary>
        /// Stable voice identity/persona description for Qwen3-TTS voice design or cloned-voice context.
        /// </summary>
        public string VoiceDesignPrompt { get; set; } = string.Empty;

        /// <summary>
        /// Optional clone reference audio filename/path resolved by the TTS sidecar.
        /// </summary>
        public string VoiceCloneAudioFile { get; set; } = string.Empty;

        /// <summary>
        /// Optional transcript for the reference clone audio (recommended for best quality).
        /// </summary>
        public string VoiceCloneRefText { get; set; } = string.Empty;

        public bool XVectorOnly { get; set; }

        /// <summary>
        /// Per-line delivery instructions: tone, emotion, pace, intensity, breath, and acting direction.
        /// </summary>
        public string DeliveryInstructions { get; set; } = string.Empty;

        public string PrimaryEmotion { get; set; } = "neutral";
        public double EmotionIntensity { get; set; } = 0.5;
        public string Pace { get; set; } = "normal";
        public string Pitch { get; set; } = "natural";
        public string Volume { get; set; } = "normal";
        public string Prosody { get; set; } = string.Empty;
        public string Language { get; set; } = "Auto";
        public List<string> AudioTags { get; set; } = new List<string>();
        public List<string> Avoid { get; set; } = new List<string>();

        public static VoiceDirective Empty()
        {
            return new VoiceDirective();
        }
    }
}
