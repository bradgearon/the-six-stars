using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Wds.SixStars.Api.Writings
{
    /// <summary>
    /// Canonical open-ended reader-emotion IU vector used by every newly reviewed shelf work,
    /// regardless of whether the output is a compact Spark or a Constellation Story.
    ///
    /// The serialized contract is deliberately positional and compact for local LLM efficiency:
    /// one semicolon-delimited string in Categories order. Overall is the final position so a
    /// complete review is one string rather than an array/object per emotion.
    /// </summary>
    public static class StarWritingReaderEmotionVector
    {
        public static readonly string[] EmotionCategories = StarWritingStoryEmotionFeedback.Emotions;

        public static readonly string[] Categories = EmotionCategories
            .Concat(new[] { "Overall" })
            .ToArray();

        public static string CategoryOrder => string.Join(",", Categories);

        public static bool TryNormalizeComplete(
            string? raw,
            out string normalized,
            out Dictionary<string, StarWritingEmotionValue> values)
        {
            normalized = string.Empty;
            values = new Dictionary<string, StarWritingEmotionValue>(StringComparer.OrdinalIgnoreCase);
            string text = (raw ?? string.Empty).Trim().Trim('"').Trim('[', ']');
            if (string.IsNullOrWhiteSpace(text)) return false;

            string[] parts = text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                parts = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            }
            if (parts.Length != Categories.Length) return false;

            for (int i = 0; i < Categories.Length; i++)
            {
                string token = (parts[i] ?? string.Empty).Trim().Trim('"');
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    || double.IsNaN(value)
                    || double.IsInfinity(value)
                    || value < 0)
                {
                    return false;
                }
                values[Categories[i]] = new StarWritingEmotionValue { Value = value };
            }

            normalized = Format(values);
            return true;
        }

        public static string FormatValues(IReadOnlyDictionary<string, double> values)
        {
            if (values == null) return string.Empty;
            Dictionary<string, StarWritingEmotionValue> normalized = new Dictionary<string, StarWritingEmotionValue>(StringComparer.OrdinalIgnoreCase);
            foreach (string category in Categories)
            {
                if (!values.TryGetValue(category, out double value)
                    || double.IsNaN(value)
                    || double.IsInfinity(value)
                    || value < 0)
                {
                    return string.Empty;
                }
                normalized[category] = new StarWritingEmotionValue { Value = value };
            }
            return Format(normalized);
        }

        public static string FromLegacyEmotionFeedback(
            IEnumerable<StarWritingStoryEmotionValue>? feedback,
            double overallValue)
        {
            if (!StarWritingStoryEmotionFeedback.TryNormalizeComplete(
                    feedback,
                    out _,
                    out Dictionary<string, StarWritingEmotionValue> emotionValues)
                || double.IsNaN(overallValue)
                || double.IsInfinity(overallValue)
                || overallValue < 0)
            {
                return string.Empty;
            }

            emotionValues["Overall"] = new StarWritingEmotionValue { Value = overallValue };
            return Format(emotionValues);
        }

        public static Dictionary<string, StarWritingEmotionValue> ToValueDictionary(string? vector)
        {
            return TryNormalizeComplete(vector, out _, out Dictionary<string, StarWritingEmotionValue> values)
                ? values
                : new Dictionary<string, StarWritingEmotionValue>(StringComparer.OrdinalIgnoreCase);
        }

        public static double ReadValue(IReadOnlyDictionary<string, StarWritingEmotionValue> values, string category)
        {
            return values != null
                && values.TryGetValue(category ?? string.Empty, out StarWritingEmotionValue? value)
                && value != null
                && !double.IsNaN(value.Value)
                && !double.IsInfinity(value.Value)
                ? Math.Max(0.0, value.Value)
                : 0.0;
        }

        public static string FormatNamed(string? vector, int maximum = 30)
        {
            if (!TryNormalizeComplete(vector, out _, out Dictionary<string, StarWritingEmotionValue> values))
            {
                return string.Empty;
            }

            return string.Join("; ", EmotionCategories
                .Take(Math.Max(0, maximum))
                .Select(category => category + "=" + ReadValue(values, category).ToString("0.##", CultureInfo.InvariantCulture)));
        }

        private static string Format(IReadOnlyDictionary<string, StarWritingEmotionValue> values)
        {
            return string.Join(";", Categories.Select(category =>
                values[category].Value.ToString("0.##", CultureInfo.InvariantCulture)));
        }
    }
}
