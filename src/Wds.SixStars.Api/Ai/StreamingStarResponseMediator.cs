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

        private static readonly string[] KnownControlMarkers =
        {
            // Current compact fast-speech controls.
            "~e^", "~q^", "~i^",
            // Backward compatibility for saved prompts / an old in-flight model response.
            "~emotion^", "~unresolved^", "~info^"
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

            int embeddedControl = FindFirstKnownControlMarker(tail, 0);
            if (tail[0] == ControlPrefix || embeddedControl == 0)
            {
                _traceEntries.Add("discarded unterminated control tail at EOS: " + Preview(tail));
                _buffer.Clear();
                return;
            }

            if (embeddedControl > 0)
            {
                string speech = Clean(tail.Substring(0, embeddedControl));
                if (!string.IsNullOrWhiteSpace(speech))
                {
                    _traceEntries.Add("accepted speech before unterminated embedded control at EOS: " + Preview(speech));
                    AcceptSpeech(speech);
                }
                _traceEntries.Add("discarded unterminated embedded control at EOS: " + Preview(tail.Substring(embeddedControl)));
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

            RouteCompletedChunk(chunk);
        }

        private void RouteCompletedChunk(string chunk)
        {
            // Speech and controls should occupy separate § chunks. Recover an accidentally
            // attached explicit control marker without doing sentence splitting; the model still
            // owns all sentence boundaries.
            int controlIndex = FindFirstKnownControlMarker(chunk, 0);
            if (controlIndex < 0)
            {
                if (chunk[0] == ControlPrefix) RouteControl(chunk);
                else AcceptSpeech(chunk);
                return;
            }

            if (controlIndex > 0)
            {
                string speech = Clean(chunk.Substring(0, controlIndex));
                if (!string.IsNullOrWhiteSpace(speech))
                {
                    _traceEntries.Add("mixed speech/control chunk repaired; speech separated before embedded control: " + Preview(speech));
                    AcceptSpeech(speech);
                }
            }

            string controls = Clean(chunk.Substring(controlIndex));
            RouteControlSequence(controls);
        }

        private void RouteControlSequence(string controls)
        {
            if (string.IsNullOrWhiteSpace(controls)) return;

            int cursor = 0;
            while (cursor < controls.Length)
            {
                int start = FindFirstKnownControlMarker(controls, cursor);
                if (start < 0)
                {
                    string remainder = Clean(controls.Substring(cursor));
                    if (!string.IsNullOrWhiteSpace(remainder))
                    {
                        _traceEntries.Add("discarded text after protocol-control recovery because it was not a complete control chunk: " + Preview(remainder));
                    }
                    return;
                }

                int next = FindFirstKnownControlMarker(controls, start + 1);
                string control = Clean(next < 0
                    ? controls.Substring(start)
                    : controls.Substring(start, next - start));
                if (!string.IsNullOrWhiteSpace(control)) RouteControl(control);
                if (next < 0) return;
                cursor = next;
            }
        }

        private void AcceptSpeech(string chunk)
        {
            string speech = Clean(chunk);
            if (string.IsNullOrWhiteSpace(speech)) return;
            _spokenParts.Add(speech);
            _traceEntries.Add("speech accepted: " + Preview(speech));
            _onSpeechReady?.Invoke(speech);
        }

        private static int FindFirstKnownControlMarker(string value, int startIndex)
        {
            if (string.IsNullOrWhiteSpace(value) || startIndex >= value.Length) return -1;
            if (startIndex < 0) startIndex = 0;

            int best = -1;
            foreach (string marker in KnownControlMarkers)
            {
                int index = value.IndexOf(marker, startIndex, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (best < 0 || index < best)) best = index;
            }
            return best;
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

            if (kind == "e" || kind == "emotion")
            {
                if (parts.Length < 2)
                {
                    _traceEntries.Add("e rejected: missing animation name");
                    return;
                }

                string emotion = NormalizeEmotion(parts[1]);
                if (string.IsNullOrWhiteSpace(emotion))
                {
                    _traceEntries.Add("e rejected: unsupported animation '" + Clean(parts[1]) + "'");
                    return;
                }

                _traceEntries.Add("e accepted: " + emotion);
                _onActionReady?.Invoke(new StarStreamAction
                {
                    ActionType = "emotion",
                    Name = emotion,
                    Vector = "e^" + emotion
                });
                return;
            }

            if (kind == "q" || kind == "unresolved")
            {
                List<StarUnresolvedVectorCodec.OutgoingThread> parsed;
                if (kind == "q" && parts.Length >= 4 && parts.Length < 7)
                {
                    // Current compact q format: ~q^score^question^resolution^effect§.
                    // The durable codec generates a stable id from the question text; the slower
                    // background review can add tags later if they are useful.
                    double priority = 70;
                    if (double.TryParse(Clean(parts[1]), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedPriority))
                    {
                        priority = Math.Max(0, Math.Min(100, parsedPriority));
                    }
                    string question = Clean(parts.Length > 2 ? parts[2] : string.Empty);
                    string resolution = Clean(parts.Length > 3 ? parts[3] : string.Empty);
                    string effect = Clean(parts.Length > 4 ? parts[4] : string.Empty);
                    if (string.IsNullOrWhiteSpace(question))
                    {
                        _traceEntries.Add("q rejected: empty question");
                        return;
                    }
                    StarUnresolvedVectorCodec.OutgoingThread draft = new StarUnresolvedVectorCodec.OutgoingThread
                    {
                        Priority = priority,
                        OpenThread = question,
                        ResolutionIntent = resolution,
                        IntendedEffects = effect
                    };
                    parsed = StarUnresolvedVectorCodec.ParseOutgoingVector(StarUnresolvedVectorCodec.EncodeOutgoing(draft), 1);
                }
                else
                {
                    // Backward compatibility: old q/unresolved format carried id, priority and tags.
                    if (parts.Length < 7)
                    {
                        _traceEntries.Add("q rejected: expected ~q^score^question^resolution^effect or legacy 7-field vector, received " + parts.Length + " fields");
                        return;
                    }
                    string rawVector = string.Join("^", parts.Skip(1));
                    parsed = StarUnresolvedVectorCodec.ParseOutgoingVector(rawVector, 1);
                }

                if (parsed.Count == 0)
                {
                    _traceEntries.Add("q rejected: compact question vector parser did not accept it");
                    return;
                }

                StarUnresolvedVectorCodec.OutgoingThread item = parsed[0];
                int existingIndex = _outgoingUnresolved.FindIndex(x =>
                    string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.OpenThread, item.OpenThread, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0) _outgoingUnresolved[existingIndex] = item;
                else _outgoingUnresolved.Add(item);
                _traceEntries.Add("q accepted: id=" + item.Id + "; priority=" + item.Priority.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }

            if (kind == "i" || kind == "info")
            {
                if (parts.Length < 3)
                {
                    _traceEntries.Add("i rejected: expected at least ~i^source^subject, received " + parts.Length + " fields");
                    return;
                }

                // Preferred control format: ~i^source^subject^score§.
                // Be tolerant of an omitted score or accidental extra ^ inside the subject.
                string source = NormalizeInformationSource(parts[1]);
                if (string.IsNullOrWhiteSpace(source))
                {
                    _traceEntries.Add("i rejected: unsupported source '" + Clean(parts[1]) + "'");
                    return;
                }

                double score = 100;
                bool hasExplicitScore = false;
                int subjectFieldCount = Math.Max(1, parts.Length - 2);
                if (parts.Length >= 4 && double.TryParse(Clean(parts[parts.Length - 1]), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedScore))
                {
                    score = parsedScore;
                    hasExplicitScore = true;
                    subjectFieldCount = Math.Max(1, parts.Length - 3);
                }

                string subject = string.Join(" ", parts.Skip(2).Take(subjectFieldCount).Select(Clean).Where(x => !string.IsNullOrWhiteSpace(x)));
                if (string.IsNullOrWhiteSpace(subject))
                {
                    _traceEntries.Add("i rejected: empty subject for source=" + source);
                    return;
                }

                if (!hasExplicitScore)
                {
                    _traceEntries.Add("i repaired: score omitted/invalid; defaulted to 100 for explicit lookup source=" + source + "; subject=" + Preview(subject));
                }
                else if (parts.Length > 4)
                {
                    _traceEntries.Add("i repaired: extra ^ field(s) inside subject were joined; source=" + source + "; subject=" + Preview(subject));
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
                        _traceEntries.Add("i accepted/replaced: source=" + source + "; subject=" + Preview(subject) + "; score=" + score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _traceEntries.Add("i ignored: weaker duplicate source=" + source + "; subject=" + Preview(subject) + "; score=" + score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
                else if (_informationNeeds.Count < 3)
                {
                    _informationNeeds.Add(item);
                    _traceEntries.Add("i accepted: source=" + source + "; subject=" + Preview(subject) + "; score=" + score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    _traceEntries.Add("i ignored: already reached 3-need cap; source=" + source + "; subject=" + Preview(subject));
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
                case "beastiary":
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
            if (EmotionAnimations.Contains(normalized)) return normalized;

            // Keep the Android protocol normalized to the five installed animation names even if
            // the local model uses an obvious nearby adjective. The prompt still asks for exact
            // names; these aliases are only a defensive compatibility layer.
            switch (normalized)
            {
                case "tender":
                case "tenderness":
                case "affection":
                case "affectionate":
                case "warm":
                case "warmth":
                case "romantic":
                    return "love";
                case "joy":
                case "joyful":
                case "excited":
                case "amused":
                    return "happy";
                case "confusion":
                case "puzzled":
                    return "confused";
                case "sorrow":
                case "sadness":
                case "grief":
                    return "sad";
                case "anger":
                case "mad":
                case "furious":
                    return "angry";
                default:
                    return string.Empty;
            }
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
