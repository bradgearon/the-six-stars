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
        private readonly StringBuilder _rawReceived = new StringBuilder();
        private readonly List<string> _spokenParts = new List<string>();
        private readonly List<string> _traceEntries = new List<string>();
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
        public IReadOnlyList<string> TraceEntries => _traceEntries;
        public string RawReceivedText => _rawReceived.ToString();
        public string OutgoingUnresolvedVector => StarUnresolvedVectorCodec.EncodeOutgoingVector(_outgoingUnresolved, 10);
        public IReadOnlyList<InformationNeedVectorItem> InformationNeeds => _informationNeeds;

        public void Append(string? delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            _rawReceived.Append(delta);
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
                _traceEntries.Add("discarded unterminated control tail at EOS: " + Preview(tail));
                _buffer.Clear();
                return;
            }

            _traceEntries.Add("accepted unterminated speech tail at EOS for compatibility: " + Preview(tail));

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
            _traceEntries.Add("speech accepted: " + Preview(chunk));
            _onSpeechReady?.Invoke(chunk);
        }

        private void RouteControl(string chunk)
        {
            _traceEntries.Add("control received: " + Preview(chunk));
            string payload = chunk.Substring(1).Trim();
            if (string.IsNullOrWhiteSpace(payload))
            {
                _traceEntries.Add("control rejected: empty payload");
                return;
            }

            string[] parts = payload.Split(new[] { '^' }, StringSplitOptions.None);
            if (parts.Length == 0)
            {
                _traceEntries.Add("control rejected: no fields");
                return;
            }

            string kind = Clean(parts[0]).ToLowerInvariant();

            if (kind == "emotion")
            {
                if (parts.Length < 2)
                {
                    _traceEntries.Add("emotion rejected: missing animation name");
                    return;
                }

                string emotion = NormalizeEmotion(parts[1]);
                if (string.IsNullOrWhiteSpace(emotion))
                {
                    _traceEntries.Add("emotion rejected: unsupported animation '" + Clean(parts[1]) + "'");
                    return;
                }

                _traceEntries.Add("emotion accepted: " + emotion);
                _onActionReady?.Invoke(new StarStreamAction
                {
                    ActionType = "emotion",
                    Name = emotion,
                    Vector = "emotion^" + emotion
                });
                return;
            }

            if (kind == "unresolved")
            {
                if (parts.Length < 7)
                {
                    _traceEntries.Add("unresolved rejected: expected 7 fields including kind, received " + parts.Length);
                    return;
                }

                // Control format: ~unresolved^id^priority^tags^openThread^resolutionIntent^intendedEffects§
                string rawVector = string.Join("^", parts.Skip(1));
                List<StarUnresolvedVectorCodec.OutgoingThread> parsed = StarUnresolvedVectorCodec.ParseOutgoingVector(rawVector, 1);
                if (parsed.Count == 0)
                {
                    _traceEntries.Add("unresolved rejected: compact vector parser did not accept it");
                    return;
                }

                StarUnresolvedVectorCodec.OutgoingThread item = parsed[0];
                int existingIndex = _outgoingUnresolved.FindIndex(x =>
                    string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.OpenThread, item.OpenThread, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0) _outgoingUnresolved[existingIndex] = item;
                else _outgoingUnresolved.Add(item);
                _traceEntries.Add("unresolved accepted: id=" + item.Id + "; priority=" + item.Priority.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }

            if (kind == "info")
            {
                if (parts.Length < 4)
                {
                    _traceEntries.Add("info rejected: expected ~info^source^subject^score, received " + parts.Length + " fields");
                    return;
                }

                // Control format: ~info^source^subject^score§
                // source is one of the runtime's compact local-data categories; subject is the
                // actual detail/query to search for; score is 0-100 and doubles as confidence.
                string source = NormalizeInformationSource(parts[1]);
                string subject = Clean(parts[2]);
                if (string.IsNullOrWhiteSpace(source))
                {
                    _traceEntries.Add("info rejected: unsupported source '" + Clean(parts[1]) + "'");
                    return;
                }

                if (string.IsNullOrWhiteSpace(subject))
                {
                    _traceEntries.Add("info rejected: empty subject for source=" + source);
                    return;
                }

                if (!double.TryParse(Clean(parts[3]), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double score))
                {
                    _traceEntries.Add("info rejected: invalid score '" + Clean(parts[3]) + "' for source=" + source + "; subject=" + Preview(subject));
                    return;
                }

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
                    if (_informationNeeds[existingIndex].Score <= item.Score)
                    {
                        _informationNeeds[existingIndex] = item;
                        _traceEntries.Add("info accepted/replaced: source=" + source + "; subject=" + Preview(subject) + "; score=" + score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _traceEntries.Add("info ignored: weaker duplicate source=" + source + "; subject=" + Preview(subject) + "; score=" + score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
                else if (_informationNeeds.Count < 3)
                {
                    _informationNeeds.Add(item);
                    _traceEntries.Add("info accepted: source=" + source + "; subject=" + Preview(subject) + "; score=" + score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    _traceEntries.Add("info ignored: already reached 3-need cap; source=" + source + "; subject=" + Preview(subject));
                }
                return;
            }

            _traceEntries.Add("control rejected: unknown kind '" + kind + "'");
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

        private static string Preview(string? value)
        {
            string text = Clean(value).Replace("\r", " ").Replace("\n", " ");
            while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ");
            return text.Length <= 220 ? text : text.Substring(0, 220) + "...";
        }

        private static string Clean(string? value)
        {
            return (value ?? string.Empty).Trim();
        }
    }
}
