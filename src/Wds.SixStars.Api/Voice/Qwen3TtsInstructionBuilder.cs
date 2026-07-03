using System;
using System.Linq;
using System.Text;

namespace Wds.SixStars.Api.Voice
{
    public static class Qwen3TtsInstructionBuilder
    {
        public static string Build(VoiceSynthesisRequest request, Qwen3TtsOptions options)
        {
            StarVoiceSettings settings = request.VoiceSettings ?? new StarVoiceSettings();
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("You are generating only the spoken audio, not reading these instructions aloud.");

            if (options.IncludeVoiceDesignInInstructions && !string.IsNullOrWhiteSpace(settings.VoiceDesignPrompt))
            {
                sb.AppendLine("STABLE VOICE IDENTITY");
                sb.AppendLine(settings.VoiceDesignPrompt.Trim());
                sb.AppendLine();
            }

            sb.AppendLine("PERFORMANCE FOR THIS EXACT LINE");
            if (!string.IsNullOrWhiteSpace(settings.DeliveryInstructions))
            {
                sb.AppendLine(settings.DeliveryInstructions.Trim());
            }
            else
            {
                sb.AppendLine("Speak in character, naturally and emotionally grounded.");
            }
            sb.AppendLine("Primary emotion: " + (string.IsNullOrWhiteSpace(settings.PrimaryEmotion) ? settings.Style : settings.PrimaryEmotion));
            sb.AppendLine("Emotion intensity: " + Math.Round(Math.Max(0, Math.Min(1, settings.EmotionIntensity)) * 100) + " percent.");
            sb.AppendLine("Pace: " + settings.Pace + ".");
            sb.AppendLine("Pitch: " + settings.PitchDescription + ".");
            sb.AppendLine("Volume: " + settings.Volume + ".");

            if (!string.IsNullOrWhiteSpace(settings.Prosody))
            {
                sb.AppendLine("Prosody: " + settings.Prosody.Trim());
            }

            if (settings.AudioTags.Count > 0)
            {
                sb.AppendLine("Allowed subtle performance tags: " + string.Join(", ", settings.AudioTags.Where(x => !string.IsNullOrWhiteSpace(x))) + ".");
            }

            if (settings.Avoid.Count > 0)
            {
                sb.AppendLine("Avoid: " + string.Join(", ", settings.Avoid.Where(x => !string.IsNullOrWhiteSpace(x))) + ".");
            }

            sb.AppendLine("Do not add extra words. Do not read punctuation, labels, JSON, or this instruction text. Speak only the provided input text.");
            return sb.ToString().Trim();
        }
    }
}
