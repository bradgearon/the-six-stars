using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// Guards the TTS layer from LLM-produced performance descriptions accidentally
    /// landing in voiceDirective.language. Qwen3-TTS expects a real language name,
    /// not style text such as "Warm and poetic.".
    /// </summary>
    public static class VoiceLanguageNormalizer
    {
        private static readonly Dictionary<string, string> Languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "auto", "Auto" },
            { "en", "English" },
            { "eng", "English" },
            { "english", "English" },
            { "enus", "English" },
            { "engb", "English" },
            { "zh", "Chinese" },
            { "zho", "Chinese" },
            { "chi", "Chinese" },
            { "chinese", "Chinese" },
            { "cn", "Chinese" },
            { "fr", "French" },
            { "fra", "French" },
            { "fre", "French" },
            { "french", "French" },
            { "de", "German" },
            { "deu", "German" },
            { "ger", "German" },
            { "german", "German" },
            { "es", "Spanish" },
            { "spa", "Spanish" },
            { "spanish", "Spanish" },
            { "it", "Italian" },
            { "ita", "Italian" },
            { "italian", "Italian" },
            { "ja", "Japanese" },
            { "jpn", "Japanese" },
            { "japanese", "Japanese" },
            { "ko", "Korean" },
            { "kor", "Korean" },
            { "korean", "Korean" },
            { "pt", "Portuguese" },
            { "por", "Portuguese" },
            { "portuguese", "Portuguese" },
            { "ru", "Russian" },
            { "rus", "Russian" },
            { "russian", "Russian" }
        };

        public static string Normalize(string? value, string? fallback = "English")
        {
            string safeFallback = NormalizeCore(fallback) ?? "English";
            string? normalized = NormalizeCore(value);
            return normalized ?? safeFallback;
        }

        public static bool IsSupported(string? value)
        {
            return NormalizeCore(value) != null;
        }

        private static string? NormalizeCore(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            string text = value.Trim().Trim('[', ']', '"', '\'', '.', ',', ';', ':');
            if (string.IsNullOrWhiteSpace(text)) return null;

            string compact = CompactLetters(text);
            if (Languages.TryGetValue(compact, out string? canonical))
            {
                return canonical;
            }

            // Common BCP-47 forms such as en-US, fr-FR, pt-BR.
            int dash = text.IndexOfAny(new[] { '-', '_' });
            if (dash > 0)
            {
                string prefix = CompactLetters(text.Substring(0, dash));
                if (Languages.TryGetValue(prefix, out canonical))
                {
                    return canonical;
                }
            }

            return null;
        }

        private static string CompactLetters(string text)
        {
            char[] buffer = new char[text.Length];
            int length = 0;
            foreach (char ch in text)
            {
                if (char.IsLetter(ch))
                {
                    buffer[length++] = char.ToLowerInvariant(ch);
                }
            }
            return new string(buffer, 0, length);
        }
    }
}
