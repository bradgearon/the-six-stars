using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Mediates delimiter-streamed foreground output into speech and non-speech controls.
    /// Every chunk is buffered until the § delimiter arrives. Chunks beginning with '~' are
    /// protocol controls and are never displayed or spoken; all other chunks are immutable speech.
    /// </summary>
    internal sealed class StreamingStarResponseMediator
    {
        public const char Delimiter = '\xa7';
        public const char ControlPrefix = '~';

        private static readonly HashSet<string> EmotionAnimations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "happy", "confused", "sad", "angry", "love"
        };

        private readonly StringBuilder _buffer = new StringBuilder();
        private readonly List<string> _spokenParts = new List<string>();
        private readonly List<StarUnresolvedVectorCodec.OutgoingThread> _outgoingUnresolved = new List<StarUnresolvedVectorCodec.OutgoingThread>();
        private readonly List<InformationNeedVectorItem> _informationNeeds = new List<InformationNeedVectorItem>();
        private readonly Action<string>? _onSpeechReady;
        private readonly Action<StarStreamAction>? _onActionReady;

        public StreamingStarResponseMediator(Action<string>? onSpeechReady, Action<StarStreamAction>? onActionReady)
        {
            _onSpeechReady = onSpeechReady;
            _onActionReady = onActionReady;
        }

        public IReadOnlyList<string> SpokenParts => _spokenParts;
        public string OutgoingUnresolvedVector => StarUnresolvedVectorCodec.EncodeOutgoingVector(_outgoingUnresolved, 10);
        public IReadOnlyList<InformationNeedVectorItem> InformationNeeds => _informationNeeds;

        public void Append(string? delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            foreach (char ch in delta)
            {
                if (ch == Delimiter) EmitBufferedChunk();
                else _buffer.Append(ch);
            }
        }

        public void Complete()
        {
            // Speech can safely use an EOS tail as a compatibility fallback. Controls cannot:
            // a non-speech vector is actionable only after its explicit § terminator arrives.
            // This also protects against max-token truncation halfway through a control vector.
            string tail = NormalizeChunk(_buffer.ToString());
            if (string.IsNullOrWhiteSpace(tail))
            {
                _buffer.Clear();
                return;
            }

            if (tail[0] == ControlPrefix)
            {
                _buffer.Clear();
                return;
            }

            EmitBufferedChunk();
        }

        private void EmitBufferedChunk()
        {
            string chunk = NormalizeChunk(_buffer.ToString());
            _buffer.Clear();
            if (string.IsNullOrWhiteSpace(chunk)) return;

            if (chunk[0] == ControlPrefix)
            {
                RouteControl(chunk);
                return;
            }

            _spokenParts.Add(chunk);
            _onSpeechReady?.Invoke(chunk);
        }

        private void RouteControl(string chunk)
        {
            string payload = chunk.Substring(1).Trim();
            if (string.IsNullOrWhiteSpace(payload)) return;
            string[] parts = payload.Split(new[] { '^' }, StringSplitOptions.None);
            if (parts.Length == 0) return;
            string kind = Clean(parts[0]).ToLowerInvariant();

            if (kind == "emotion" && parts.Length >= 2)
            {
                string emotion = NormalizeEmotion(parts[1]);
                if (string.IsNullOrWhiteSpace(emotion)) return;
                _onActionReady?.Invoke(new StarStreamAction
                {
                    ActionType = "emotion",
                    Name = emotion,
                    Vector = "emotion^" + emotion
                });
                return;
            }

            if (kind == "unresolved" && parts.Length >= 7)
            {
                // Control format: ~unresolved^id^priority^tags^openThread^resolutionIntent^intendedEffects§
                string rawVector = string.Join("^", parts.Skip(1));
                List<StarUnresolvedVectorCodec.OutgoingThread> parsed = StarUnresolvedVectorCodec.ParseOutgoingVector(rawVector, 1);
                if (parsed.Count == 0) return;
                StarUnresolvedVectorCodec.OutgoingThread item = parsed[0];
                int existingIndex = _outgoingUnresolved.FindIndex(x =>
                    string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.OpenThread, item.OpenThread, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0) _outgoingUnresolved[existingIndex] = item;
                else _outgoingUnresolved.Add(item);
            }

            if (kind == "info" && parts.Length >= 4)
            {
                // Control format: ~info^source^subject^score§
                // source is one of the runtime's compact local-data categories; subject is the
                // actual detail/query to search for; score is 0-100 and doubles as confidence.
                string source = NormalizeInformationSource(parts[1]);
                string subject = Clean(parts[2]);
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(subject)) return;
                if (!double.TryParse(Clean(parts[3]), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double score)) return;
                if (score < 0) score = 0;
                if (score > 100) score = 100;

                InformationNeedVectorItem item = new InformationNeedVectorItem
                {
                    Subject = source,
                    DataType = source,
                    Query = subject,
                    Reason = subject,
                    Score = score,
                    Confidence = score / 100.0
                };

                int existingIndex = _informationNeeds.FindIndex(x => string.Equals(x.DataType, source, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    if (_informationNeeds[existingIndex].Score <= item.Score) _informationNeeds[existingIndex] = item;
                }
                else if (_informationNeeds.Count < 3)
                {
                    _informationNeeds.Add(item);
                }
            }
        }


        private static string NormalizeInformationSource(string? value)
        {
            string normalized = Clean(value).ToLowerInvariant().Replace('_', '-');
            switch (normalized)
            {
                case "history":
                case "memory":
                case "conversation":
                case "conversation-history":
                case "archive":
                    return "history";
                case "quote":
                case "quotes":
                    return "quotes";
                case "marker":
                case "markers":
                case "coordinate":
                case "coordinates":
                    return "markers";
                case "bestiary":
                case "monster":
                case "monsters":
                case "creature":
                case "creatures":
                    return "bestiary";
                case "character":
                case "characters":
                case "profile":
                case "profiles":
                    return "characters";
                case "lore":
                case "skills":
                case "powers":
                case "mechanics":
                    return "lore";
                case "world":
                case "places":
                case "locations":
                    return "world";
                case "screen":
                case "vision":
                    return "screen";
                case "story":
                case "stories":
                    return "story";
                default:
                    return string.Empty;
            }
        }

        private static string NormalizeEmotion(string? value)
        {
            string normalized = Clean(value).ToLowerInvariant();
            return EmotionAnimations.Contains(normalized) ? normalized : string.Empty;
        }

        private static string NormalizeChunk(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
            while (normalized.Contains("  ", StringComparison.Ordinal)) normalized = normalized.Replace("  ", " ");
            return normalized;
        }

        private static string Clean(string? value)
        {
            return (value ?? string.Empty).Trim();
        }
    }
}
