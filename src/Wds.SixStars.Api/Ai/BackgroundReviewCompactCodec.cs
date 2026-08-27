using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Parser for the deliberately primitive-only background-review response contract.
    /// Nested records are emitted by the LLM as ^-delimited strings so the response schema itself
    /// never needs arrays of objects. Keep these positional formats synchronized with review_system.sbn
    /// and android/six-stars-standalone/BackgroundReviewCompactCodec.java.
    /// </summary>
    public static class BackgroundReviewCompactCodec
    {
        public static RelationshipDelta ParseSuggestedStateDeltas(string? value)
        {
            string[] p = Split(value, 4);
            return new RelationshipDelta
            {
                Heart = ClampInt(ParseDouble(p, 0), -8, 8),
                Trust = ClampInt(ParseDouble(p, 1), -8, 8),
                Romance = ClampInt(ParseDouble(p, 2), -6, 6),
                EmotionalConnectionPercent = Math.Max(-5, Math.Min(5, ParseDouble(p, 3)))
            };
        }

        public static StarRememberedMarker? ParseRememberedMarker(string? value)
        {
            string[] p = Split(value, 11);
            if (p.All(string.IsNullOrWhiteSpace)) return null;
            string id = Clean(p[0], 140);
            string name = Clean(p[1], 160);
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)) return null;
            return new StarRememberedMarker
            {
                Id = id,
                MarkerName = name,
                Description = Clean(p[2], 500),
                LocationText = Clean(p[3], 300),
                Coordinates = Clean(p[4], 120),
                Area = Clean(p[5], 120),
                SourcePlayerQuote = Clean(p[6], 500),
                ReminderText = Clean(p[7], 600),
                Confidence = Clamp01(ParseDouble(p, 8)),
                IsActive = ParseBool(p[9], true),
                Tags = SplitTags(p[10], 6)
            };
        }

        public static StarLearnedFunction? ParseLearnedFunction(string? value)
        {
            string[] p = Split(value, 7);
            if (p.All(string.IsNullOrWhiteSpace)) return null;
            string id = Clean(p[0], 140);
            string name = Clean(p[1], 120);
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)) return null;
            return new StarLearnedFunction
            {
                Id = id,
                Name = name,
                Description = Clean(p[2], 360),
                SourcePlayerQuote = Clean(p[3], 300),
                Confidence = Clamp01(ParseDouble(p, 4)),
                IsActive = ParseBool(p[5], true),
                Tags = SplitTags(p[6], 5)
            };
        }

        public static GirlfriendVectorItem? ParseGirlfriendVectorItem(string? value)
        {
            string[] p = Split(value, 5);
            string itemId = Clean(p[0], 120);
            if (string.IsNullOrWhiteSpace(itemId)) return null;
            return new GirlfriendVectorItem
            {
                ItemId = itemId,
                DisplayName = Clean(p[1], 160),
                Confidence = Clamp01(ParseDouble(p, 2)),
                Score = Math.Max(0, Math.Min(100, ParseDouble(p, 3))),
                Reason = Clean(p[4], 360)
            };
        }

        public static bool TryParseKnownListItem<T>(string? value, out T? item)
        {
            object? parsed = null;
            if (typeof(T) == typeof(StarRememberedMarker)) parsed = ParseRememberedMarker(value);
            else if (typeof(T) == typeof(StarLearnedFunction)) parsed = ParseLearnedFunction(value);
            else if (typeof(T) == typeof(GirlfriendVectorItem)) parsed = ParseGirlfriendVectorItem(value);

            if (parsed is T typed)
            {
                item = typed;
                return true;
            }
            item = default;
            return false;
        }

        private static string[] Split(string? value, int expected)
        {
            string[] raw = (value ?? string.Empty).Split('^');
            string[] result = new string[expected];
            for (int i = 0; i < expected; i++) result[i] = i < raw.Length ? raw[i].Trim() : string.Empty;
            return result;
        }

        private static List<string> SplitTags(string? value, int max)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Clean(x, 80))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList();
        }

        private static double ParseDouble(string[] p, int index)
        {
            if (index < 0 || index >= p.Length) return 0;
            return double.TryParse(p[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && !double.IsNaN(value) && !double.IsInfinity(value) ? value : 0;
        }

        private static int ClampInt(double value, int min, int max)
        {
            int rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
            return Math.Max(min, Math.Min(max, rounded));
        }

        private static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));

        private static bool ParseBool(string? value, bool fallback)
        {
            if (bool.TryParse(value, out bool parsed)) return parsed;
            if (string.Equals(value, "1", StringComparison.Ordinal)) return true;
            if (string.Equals(value, "0", StringComparison.Ordinal)) return false;
            return fallback;
        }

        private static string Clean(string? value, int max)
        {
            string text = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("^", " ").Trim();
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            return max > 0 && text.Length > max ? text.Substring(0, max).Trim() : text;
        }
    }
}
