using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Compact unresolved-thread transport used by the live Star cognition loop.
    /// The LLM sees and returns delimited strings instead of arrays of per-thread JSON objects.
    /// Active state remains tiny (ten active threads total across incoming + outgoing per Star).
    /// </summary>
    public static class StarUnresolvedVectorCodec
    {
        public const char FieldDelimiter = '^';
        public const char ItemDelimiter = '~';

        // incoming: id^confidence^tags^openQuestion^resolutionIntent
        // outgoing: id^priority^tags^openThread^resolutionIntent^intendedEffects
        // resolved: id^direction(in|out)^confidence^resolution^landedEffect

        public sealed class IncomingThread
        {
            public string Id { get; set; } = string.Empty;
            public double Confidence { get; set; }
            public string Tags { get; set; } = string.Empty;
            public string OpenQuestion { get; set; } = string.Empty;
            public string ResolutionIntent { get; set; } = string.Empty;
        }

        public sealed class OutgoingThread
        {
            public string Id { get; set; } = string.Empty;
            public double Priority { get; set; }
            public string Tags { get; set; } = string.Empty;
            public string OpenThread { get; set; } = string.Empty;
            public string ResolutionIntent { get; set; } = string.Empty;
            public string IntendedEffects { get; set; } = string.Empty;
        }

        public sealed class Resolution
        {
            public string Id { get; set; } = string.Empty;
            public string Direction { get; set; } = string.Empty;
            public double Confidence { get; set; }
            public string ResolutionText { get; set; } = string.Empty;
            public string LandedEffect { get; set; } = string.Empty;
            public string ResolutionIntent { get; set; } = string.Empty;
            public string IntendedEffects { get; set; } = string.Empty;
        }

        public static List<IncomingThread> ParseIncomingVector(string? raw, int maxItems = 10)
        {
            return ParseIncomingItems(SplitItems(raw), maxItems);
        }

        public static List<IncomingThread> ParseIncomingItems(IEnumerable<string>? rawItems, int maxItems = 10)
        {
            Dictionary<string, IncomingThread> byKey = new Dictionary<string, IncomingThread>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in rawItems ?? Enumerable.Empty<string>())
            {
                string[] parts = SplitFields(raw);
                if (parts.Length < 4) continue;
                string question = CleanField(parts.Length > 3 ? parts[3] : string.Empty, 280);
                if (string.IsNullOrWhiteSpace(question)) continue;
                IncomingThread item = new IncomingThread
                {
                    Id = NormalizeId(parts.Length > 0 ? parts[0] : string.Empty, "in", question),
                    Confidence = Clamp01(ParseNumber(parts.Length > 1 ? parts[1] : string.Empty)),
                    Tags = CleanTags(parts.Length > 2 ? parts[2] : string.Empty),
                    OpenQuestion = question,
                    ResolutionIntent = CleanField(parts.Length > 4 ? parts[4] : string.Empty, 320)
                };
                string key = StableTextKey(item.OpenQuestion);
                byKey[key] = item;
            }
            return byKey.Values
                .OrderByDescending(x => x.Confidence)
                .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maxItems))
                .ToList();
        }

        public static List<OutgoingThread> ParseOutgoingVector(string? raw, int maxItems = 10)
        {
            return ParseOutgoingItems(SplitItems(raw), maxItems);
        }

        public static List<OutgoingThread> ParseOutgoingItems(IEnumerable<string>? rawItems, int maxItems = 10)
        {
            Dictionary<string, OutgoingThread> byKey = new Dictionary<string, OutgoingThread>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in rawItems ?? Enumerable.Empty<string>())
            {
                string[] parts = SplitFields(raw);
                if (parts.Length < 4) continue;
                string thread = CleanField(parts.Length > 3 ? parts[3] : string.Empty, 280);
                if (string.IsNullOrWhiteSpace(thread)) continue;
                OutgoingThread item = new OutgoingThread
                {
                    Id = NormalizeId(parts.Length > 0 ? parts[0] : string.Empty, "out", thread),
                    Priority = Clamp100(ParseNumber(parts.Length > 1 ? parts[1] : string.Empty)),
                    Tags = CleanTags(parts.Length > 2 ? parts[2] : string.Empty),
                    OpenThread = thread,
                    ResolutionIntent = CleanField(parts.Length > 4 ? parts[4] : string.Empty, 320),
                    IntendedEffects = CleanField(parts.Length > 5 ? parts[5] : string.Empty, 420)
                };
                string key = StableTextKey(item.OpenThread);
                byKey[key] = item;
            }
            return byKey.Values
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maxItems))
                .ToList();
        }

        public static List<Resolution> ParseResolutionVector(string? raw, int maxItems = 8)
        {
            return ParseResolutionItems(SplitItems(raw), maxItems);
        }

        public static List<Resolution> ParseResolutionItems(IEnumerable<string>? rawItems, int maxItems = 8)
        {
            List<Resolution> result = new List<Resolution>();
            foreach (string itemRaw in rawItems ?? Enumerable.Empty<string>())
            {
                string[] parts = SplitFields(itemRaw);
                if (parts.Length < 4) continue;
                string direction = CleanField(parts.Length > 1 ? parts[1] : string.Empty, 12).ToLowerInvariant();
                if (direction != "in" && direction != "out") continue;
                string resolution = CleanField(parts.Length > 3 ? parts[3] : string.Empty, 360);
                if (string.IsNullOrWhiteSpace(resolution)) continue;
                result.Add(new Resolution
                {
                    Id = CleanId(parts.Length > 0 ? parts[0] : string.Empty),
                    Direction = direction,
                    Confidence = Clamp01(ParseNumber(parts.Length > 2 ? parts[2] : string.Empty)),
                    ResolutionText = resolution,
                    LandedEffect = CleanField(parts.Length > 4 ? parts[4] : string.Empty, 280),
                    ResolutionIntent = CleanField(parts.Length > 5 ? parts[5] : string.Empty, 320),
                    IntendedEffects = CleanField(parts.Length > 6 ? parts[6] : string.Empty, 220)
                });
            }
            return result
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .GroupBy(x => x.Direction + ":" + x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Last())
                .OrderByDescending(x => x.Confidence)
                .Take(Math.Max(1, maxItems))
                .ToList();
        }

        public static string EncodeIncoming(IncomingThread item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.OpenQuestion)) return string.Empty;
            return string.Join(FieldDelimiter.ToString(), new[]
            {
                NormalizeId(item.Id, "in", item.OpenQuestion),
                Clamp01(item.Confidence).ToString("0.00", CultureInfo.InvariantCulture),
                CleanTags(item.Tags),
                CleanField(item.OpenQuestion, 280),
                CleanField(item.ResolutionIntent, 320)
            });
        }

        public static string EncodeOutgoing(OutgoingThread item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.OpenThread)) return string.Empty;
            return string.Join(FieldDelimiter.ToString(), new[]
            {
                NormalizeId(item.Id, "out", item.OpenThread),
                Clamp100(item.Priority).ToString("0", CultureInfo.InvariantCulture),
                CleanTags(item.Tags),
                CleanField(item.OpenThread, 280),
                CleanField(item.ResolutionIntent, 320),
                CleanField(item.IntendedEffects, 220)
            });
        }

        public static string EncodeResolution(Resolution item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.ResolutionText)) return string.Empty;
            string direction = string.Equals(item.Direction, "in", StringComparison.OrdinalIgnoreCase) ? "in" : "out";
            return string.Join(FieldDelimiter.ToString(), new[]
            {
                CleanId(item.Id),
                direction,
                Clamp01(item.Confidence).ToString("0.00", CultureInfo.InvariantCulture),
                CleanField(item.ResolutionText, 360),
                CleanField(item.LandedEffect, 280)
            });
        }

        public static string EncodeResolutionHistory(Resolution item)
        {
            string baseVector = EncodeResolution(item);
            if (string.IsNullOrWhiteSpace(baseVector)) return string.Empty;
            return baseVector + FieldDelimiter
                + CleanField(item.ResolutionIntent, 320) + FieldDelimiter
                + CleanField(item.IntendedEffects, 220);
        }

        public static string EncodeIncomingVector(IEnumerable<IncomingThread>? items, int maxItems = 10)
        {
            return string.Join(ItemDelimiter.ToString(), (items ?? Enumerable.Empty<IncomingThread>())
                .OrderByDescending(x => x?.Confidence ?? 0)
                .Take(Math.Max(1, maxItems))
                .Select(EncodeIncoming)
                .Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        public static string EncodeOutgoingVector(IEnumerable<OutgoingThread>? items, int maxItems = 10)
        {
            return string.Join(ItemDelimiter.ToString(), (items ?? Enumerable.Empty<OutgoingThread>())
                .OrderByDescending(x => x?.Priority ?? 0)
                .Take(Math.Max(1, maxItems))
                .Select(EncodeOutgoing)
                .Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        public static string EncodeResolutionVector(IEnumerable<Resolution>? items, int maxItems = 8)
        {
            return string.Join(ItemDelimiter.ToString(), (items ?? Enumerable.Empty<Resolution>())
                .OrderByDescending(x => x?.Confidence ?? 0)
                .Take(Math.Max(1, maxItems))
                .Select(EncodeResolution)
                .Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        public static string NormalizeIncomingVector(string? raw, int maxItems = 10) => EncodeIncomingVector(ParseIncomingVector(raw, maxItems), maxItems);
        public static string NormalizeOutgoingVector(string? raw, int maxItems = 10) => EncodeOutgoingVector(ParseOutgoingVector(raw, maxItems), maxItems);
        public static string NormalizeResolutionVector(string? raw, int maxItems = 8) => EncodeResolutionVector(ParseResolutionVector(raw, maxItems), maxItems);

        private static IEnumerable<string> SplitItems(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Enumerable.Empty<string>();
            return raw.Split(new[] { ItemDelimiter, '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x));
        }

        private static string[] SplitFields(string raw)
        {
            return (raw ?? string.Empty).Split(new[] { FieldDelimiter }, StringSplitOptions.None);
        }

        private static double ParseNumber(string? raw)
        {
            if (!double.TryParse((raw ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return 0;
            return value;
        }

        private static double Clamp01(double value)
        {
            if (value > 1 && value <= 100) value /= 100.0;
            return Math.Max(0, Math.Min(1, value));
        }

        private static double Clamp100(double value)
        {
            if (value > 0 && value <= 1) value *= 100.0;
            return Math.Max(0, Math.Min(100, value));
        }

        private static string CleanTags(string? value)
        {
            string clean = CleanField(value, 160).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(clean)) return string.Empty;
            return string.Join(",", clean.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(CleanId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8));
        }

        private static string CleanField(string? value, int maxLength)
        {
            string clean = (value ?? string.Empty)
                .Replace(FieldDelimiter, ' ')
                .Replace(ItemDelimiter, ' ')
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
            if (clean.Length > Math.Max(0, maxLength)) clean = clean.Substring(0, maxLength).Trim();
            return clean;
        }

        private static string CleanId(string? value)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in (value ?? string.Empty).Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
                if (sb.Length >= 72) break;
            }
            return sb.ToString().Trim('-');
        }

        private static string NormalizeId(string? id, string direction, string seed)
        {
            string clean = CleanId(id);
            if (!string.IsNullOrWhiteSpace(clean)) return clean;
            using SHA256 sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes((direction ?? string.Empty) + "|" + StableTextKey(seed)));
            StringBuilder hex = new StringBuilder();
            for (int i = 0; i < 6 && i < bytes.Length; i++) hex.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return (direction == "in" ? "in-" : "out-") + hex;
        }

        private static string StableTextKey(string? text)
        {
            string value = CleanField(text, 280).ToLowerInvariant();
            return new string(value.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray()).Trim();
        }
    }
}
