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
            "~e^", "~q^", "~i^"
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
            // The model contract requires speech and controls to occupy separate § chunks. Local
            // models occasionally append a control to the end of an otherwise valid sentence, e.g.
            // "Do you need that?~info^bestiary^slime hit points^100§". Recover that protocol
            // mistake here without doing any sentence splitting: the model still owns sentence
            // boundaries; this mediator only recognizes explicit ~ control markers.
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

            if (kind == "e")
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

            if (kind == "q")
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

            if (kind == "i")
            {
                if (parts.Length < 3)
                {
                    _traceEntries.Add("info rejected: expected at least ~info^source^subject, received " + parts.Length + " fields");
                    return;
                }

                // Preferred control format: ~info^source^subject^score§
                // Be tolerant of two common local-model protocol mistakes so a valid lookup does
                // not leak into speech or get lost: omitted score, or an accidental extra ^ inside
                // the subject. An explicit ~info means the model is asking for data, so a missing
                // score safely defaults to a strong request.
                string source = NormalizeInformationSource(parts[1]);
                if (string.IsNullOrWhiteSpace(source))
                {
                    _traceEntries.Add("info rejected: unsupported source '" + Clean(parts[1]) + "'");
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
                    _traceEntries.Add("info rejected: empty subject for source=" + source);
                    return;
                }

                if (!hasExplicitScore)
                {
                    _traceEntries.Add("info repaired: score omitted/invalid; defaulted to 100 for explicit lookup source=" + source + "; subject=" + Preview(subject));
                }
                else if (parts.Length > 4)
                {
                    _traceEntries.Add("info repaired: extra ^ field(s) inside subject were joined; source=" + source + "; subject=" + Preview(subject));
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
