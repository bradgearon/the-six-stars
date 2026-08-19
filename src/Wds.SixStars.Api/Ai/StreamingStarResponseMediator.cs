using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Mediates the foreground text stream into sentence-sized speech and compact non-speech
    /// controls. Speech no longer depends on the model remembering a section-sign delimiter:
    /// complete sentences are released from punctuation as soon as they are safe to speak.
    /// Section signs remain useful as explicit control terminators and as a legacy speech boundary.
    /// </summary>
    internal sealed class StreamingStarResponseMediator
    {
        public const char Delimiter = '\xa7';
        public const char ControlPrefix = '~';

        private static readonly HashSet<string> EmotionAnimations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "happy", "confused", "sad", "angry", "love"
        };

        private static readonly HashSet<string> CommonPeriodAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mr.", "mrs.", "ms.", "dr.", "prof.", "sr.", "jr.", "st.", "mt.",
            "vs.", "etc.", "e.g.", "i.e.", "approx.", "dept.", "est.", "fig.",
            "jan.", "feb.", "mar.", "apr.", "jun.", "jul.", "aug.", "sep.", "sept.",
            "oct.", "nov.", "dec.", "a.m.", "p.m."
        };

        // The regex finds punctuation that can close a sentence, including closing quotation marks
        // and brackets. A small semantic check below rejects decimals, initials, abbreviations and
        // ellipses. Quoted sentences therefore remain intact: She said "Run." is one speech chunk.
        private static readonly Regex SentenceEndingRegex = new Regex(
            "[.!?][\\\"'\\u2019\\u201D)\\]}]*?(?=\\s|\\xA7|~?[eqi]\\^|$)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex AcronymRegex = new Regex(
            @"^(?:[A-Za-z]\.){2,}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex EmotionWithoutDelimiterRegex = new Regex(
            @"^\s*~?(?:e|emotion)\^(?<name>[A-Za-z_-]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly string[] KnownControlMarkers =
        {
            // Current compact controls, both preferred tilde form and defensive bare form.
            "~e^", "~q^", "~i^", "e^", "q^", "i^",
            // Backward compatibility for an older in-flight model response.
            "~emotion^", "~unresolved^", "~info^", "emotion^", "unresolved^", "info^"
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
            _buffer.Append(delta);
            ProcessBuffer(false);
        }

        public void Complete()
        {
            ProcessBuffer(true);
        }

        private void ProcessBuffer(bool final)
        {
            while (true)
            {
                TrimLeadingProtocolWhitespace();
                if (_buffer.Length == 0) return;

                string current = _buffer.ToString();
                int controlIndex = FindFirstKnownControlMarker(current, 0);

                if (controlIndex == 0)
                {
                    if (TryConsumeLeadingControl(final)) continue;
                    return;
                }

                int sentenceLimit = controlIndex > 0 ? controlIndex : current.Length;
                int sentenceEnd = FindFirstSentenceEnd(current, sentenceLimit, final);
                int delimiterIndex = current.IndexOf(Delimiter);

                if (sentenceEnd > 0
                    && (controlIndex < 0 || sentenceEnd <= controlIndex)
                    && (delimiterIndex < 0 || sentenceEnd <= delimiterIndex))
                {
                    string sentence = current.Substring(0, sentenceEnd);
                    RemovePrefix(sentenceEnd);
                    _traceEntries.Add("punctuation sentence completed during stream: " + Preview(sentence));
                    AcceptSpeech(sentence);
                    continue;
                }

                // A control marker itself is a hard boundary between speech and non-speech data.
                // If the model glued the control to text without first ending the sentence, preserve
                // the text as speech but never let the control leak into Android text or TTS.
                if (controlIndex > 0)
                {
                    string speechBeforeControl = current.Substring(0, controlIndex);
                    RemovePrefix(controlIndex);
                    if (!string.IsNullOrWhiteSpace(speechBeforeControl))
                    {
                        _traceEntries.Add("speech/control boundary repaired before control marker: " + Preview(speechBeforeControl));
                        AcceptSpeech(speechBeforeControl);
                    }
                    continue;
                }

                // Legacy compatibility: if the model still emits § after a speech chunk, use it as a
                // boundary. The new fast path does not need it for ordinary sentences.
                if (delimiterIndex >= 0)
                {
                    string delimited = current.Substring(0, delimiterIndex);
                    RemovePrefix(delimiterIndex + 1);
                    if (!string.IsNullOrWhiteSpace(delimited))
                    {
                        _traceEntries.Add("legacy section-sign speech boundary accepted: " + Preview(delimited));
                        AcceptSpeech(delimited);
                    }
                    continue;
                }

                if (!final) return;

                // EOS fallback. At this point there is no earlier complete punctuation boundary or
                // terminated control left. A final i/q control may legitimately have omitted §, so
                // route it now; otherwise preserve the remaining natural-language tail as speech.
                current = _buffer.ToString();
                int embeddedControl = FindFirstKnownControlMarker(current, 0);
                if (embeddedControl == 0)
                {
                    _buffer.Clear();
                    _traceEntries.Add("EOS control accepted without section-sign terminator: " + Preview(current));
                    RouteControl(current);
                    return;
                }

                if (embeddedControl > 0)
                {
                    string speech = current.Substring(0, embeddedControl);
                    string control = current.Substring(embeddedControl);
                    _buffer.Clear();
                    if (!string.IsNullOrWhiteSpace(speech))
                    {
                        _traceEntries.Add("EOS speech/control boundary repaired: " + Preview(speech));
                        AcceptSpeech(speech);
                    }
                    if (!string.IsNullOrWhiteSpace(control))
                    {
                        _traceEntries.Add("EOS control accepted without section-sign terminator: " + Preview(control));
                        RouteControlSequence(control);
                    }
                    return;
                }

                _buffer.Clear();
                _traceEntries.Add("EOS speech tail accepted: " + Preview(current));
                AcceptSpeech(current);
                return;
            }
        }

        private bool TryConsumeLeadingControl(bool final)
        {
            string current = _buffer.ToString();
            if (FindFirstKnownControlMarker(current, 0) != 0) return false;

            int delimiterIndex = current.IndexOf(Delimiter);
            if (delimiterIndex >= 0)
            {
                string control = current.Substring(0, delimiterIndex);
                RemovePrefix(delimiterIndex + 1);
                RouteControlSequence(control);
                return true;
            }

            // Emotion controls have a closed vocabulary, so they can be safely recognized as soon
            // as a complete known name arrives even if Gemma forgets the trailing section sign.
            Match emotion = EmotionWithoutDelimiterRegex.Match(current);
            if (emotion.Success)
            {
                string normalized = NormalizeEmotion(emotion.Groups["name"].Value);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    string control = current.Substring(0, emotion.Length);
                    RemovePrefix(emotion.Length);
                    _traceEntries.Add("e control recovered without section-sign terminator: " + Preview(control));
                    RouteControl(control);
                    return true;
                }
            }

            // If another control starts, the next marker itself closes the previous vector even when
            // the model omitted § between them. i/q are otherwise allowed to wait until EOS because
            // their free-text fields have no safe earlier boundary without inventing protocol rules.
            int nextControl = FindFirstKnownControlMarker(current, 1);
            if (nextControl > 0)
            {
                string control = current.Substring(0, nextControl);
                RemovePrefix(nextControl);
                _traceEntries.Add("control boundary recovered from following control marker: " + Preview(control));
                RouteControl(control);
                return true;
            }

            if (final)
            {
                _buffer.Clear();
                _traceEntries.Add("final control accepted without section-sign terminator: " + Preview(current));
                RouteControl(current);
                return true;
            }

            return false;
        }

        private static int FindFirstSentenceEnd(string value, int maxExclusive, bool final)
        {
            if (string.IsNullOrWhiteSpace(value) || maxExclusive <= 0) return -1;
            int limit = Math.Min(value.Length, maxExclusive);
            string candidateText = value.Substring(0, limit);
            foreach (Match match in SentenceEndingRegex.Matches(candidateText))
            {
                int punctuationIndex = match.Index;
                char punctuation = candidateText[punctuationIndex];
                if (punctuation == '.' && IsUnsafePeriodBoundary(candidateText, punctuationIndex)) continue;

                int matchEnd = match.Index + match.Length;
                bool candidateEndsHere = matchEnd == candidateText.Length && limit == value.Length;
                bool closesQuote = MatchContainsClosingQuote(match.Value);

                // SSE can split a closing quote into the next delta. If the candidate currently ends
                // inside a quoted sentence, wait so the quote stays attached to this sentence.
                if (candidateEndsHere && HasUnclosedQuote(candidateText, matchEnd)) continue;

                // If a closing quote itself is the last token we have seen, wait one tiny streaming
                // step before committing it. That lets us distinguish a complete quoted sentence
                // from a quoted fragment such as: The word "cat." is unusual. At EOS it is safe to
                // commit immediately.
                if (!final && candidateEndsHere && closesQuote) continue;

                if (closesQuote && IsLowercaseContinuationAfterQuotedPunctuation(candidateText, matchEnd)) continue;

                return matchEnd;
            }
            return -1;
        }

        private static bool MatchContainsClosingQuote(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.IndexOf('"') >= 0
                || value.IndexOf('\'') >= 0
                || value.IndexOf('”') >= 0
                || value.IndexOf('’') >= 0;
        }

        private static bool IsLowercaseContinuationAfterQuotedPunctuation(string value, int startIndex)
        {
            int index = Math.Max(0, startIndex);
            while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
            return index < value.Length && char.IsLower(value[index]);
        }

        private static bool HasUnclosedQuote(string value, int throughExclusive)
        {
            bool straightDoubleOpen = false;
            bool straightSingleOpen = false;
            int curlyDoubleDepth = 0;
            int curlySingleDepth = 0;
            int limit = Math.Min(value.Length, Math.Max(0, throughExclusive));

            for (int i = 0; i < limit; i++)
            {
                char ch = value[i];
                if (ch == '"')
                {
                    straightDoubleOpen = !straightDoubleOpen;
                    continue;
                }

                if (ch == '“')
                {
                    curlyDoubleDepth++;
                    continue;
                }
                if (ch == '”')
                {
                    if (curlyDoubleDepth > 0) curlyDoubleDepth--;
                    continue;
                }

                if (ch == '\'')
                {
                    bool apostropheInsideWord = i > 0 && i + 1 < value.Length
                        && char.IsLetterOrDigit(value[i - 1]) && char.IsLetterOrDigit(value[i + 1]);
                    if (!apostropheInsideWord) straightSingleOpen = !straightSingleOpen;
                    continue;
                }

                if (ch == '‘')
                {
                    curlySingleDepth++;
                    continue;
                }
                if (ch == '’')
                {
                    bool apostropheInsideWord = i > 0 && i + 1 < value.Length
                        && char.IsLetterOrDigit(value[i - 1]) && char.IsLetterOrDigit(value[i + 1]);
                    if (!apostropheInsideWord && curlySingleDepth > 0) curlySingleDepth--;
                }
            }

            return straightDoubleOpen || straightSingleOpen || curlyDoubleDepth > 0 || curlySingleDepth > 0;
        }

        private static bool IsUnsafePeriodBoundary(string value, int periodIndex)
        {
            if (periodIndex < 0 || periodIndex >= value.Length || value[periodIndex] != '.') return false;

            // Ellipses are hesitation/continuation punctuation, not a reliable sentence boundary.
            if ((periodIndex > 0 && value[periodIndex - 1] == '.')
                || (periodIndex + 1 < value.Length && value[periodIndex + 1] == '.')) return true;

            // Decimal / version-style numeric dot.
            if (periodIndex > 0 && periodIndex + 1 < value.Length
                && char.IsDigit(value[periodIndex - 1]) && char.IsDigit(value[periodIndex + 1])) return true;

            int start = periodIndex - 1;
            while (start >= 0)
            {
                char ch = value[start];
                if (char.IsLetter(ch) || ch == '.') start--;
                else break;
            }

            string token = value.Substring(start + 1, periodIndex - start);
            if (CommonPeriodAbbreviations.Contains(token)) return true;
            if (token.Length == 2 && char.IsLetter(token[0]) && token[1] == '.') return true;
            if (AcronymRegex.IsMatch(token)) return true;
            return false;
        }

        private void AcceptSpeech(string chunk)
        {
            string speech = NormalizeChunk(chunk);
            if (string.IsNullOrWhiteSpace(speech)) return;
            _spokenParts.Add(speech);
            _traceEntries.Add("speech accepted: " + Preview(speech));
            _onSpeechReady?.Invoke(speech);
        }

        private void TrimLeadingProtocolWhitespace()
        {
            int count = 0;
            while (count < _buffer.Length)
            {
                char ch = _buffer[count];
                if (char.IsWhiteSpace(ch) || ch == Delimiter) count++;
                else break;
            }
            if (count > 0) _buffer.Remove(0, count);
        }

        private void RemovePrefix(int count)
        {
            if (count <= 0) return;
            if (count >= _buffer.Length) _buffer.Clear();
            else _buffer.Remove(0, count);
        }

        private static int FindFirstKnownControlMarker(string value, int startIndex)
        {
            if (string.IsNullOrWhiteSpace(value) || startIndex >= value.Length) return -1;
            if (startIndex < 0) startIndex = 0;

            int best = -1;
            foreach (string marker in KnownControlMarkers)
            {
                int search = startIndex;
                while (search < value.Length)
                {
                    int index = value.IndexOf(marker, search, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;
                    if (IsControlMarkerBoundary(value, index, marker))
                    {
                        if (best < 0 || index < best) best = index;
                        break;
                    }
                    search = index + 1;
                }
            }
            return best;
        }

        private static bool IsControlMarkerBoundary(string value, int index, string marker)
        {
            if (index < 0 || index >= value.Length) return false;
            if (marker.StartsWith("~", StringComparison.Ordinal)) return true;
            if (index == 0) return true;

            char previous = value[index - 1];
            return !char.IsLetterOrDigit(previous) && previous != '_';
        }

        private void RouteControlSequence(string controls)
        {
            if (string.IsNullOrWhiteSpace(controls)) return;

            string remaining = controls.Trim();
            while (!string.IsNullOrWhiteSpace(remaining))
            {
                int start = FindFirstKnownControlMarker(remaining, 0);
                if (start < 0)
                {
                    _traceEntries.Add("discarded non-control protocol remainder: " + Preview(remaining));
                    return;
                }
                if (start > 0)
                {
                    _traceEntries.Add("discarded text before recovered control: " + Preview(remaining.Substring(0, start)));
                    remaining = remaining.Substring(start);
                }

                int next = FindFirstKnownControlMarker(remaining, 1);
                string control = next < 0 ? remaining : remaining.Substring(0, next);
                RouteControl(control);
                if (next < 0) return;
                remaining = remaining.Substring(next);
            }
        }

        private void RouteControl(string chunk)
        {
            string raw = NormalizeChunk(chunk);
            if (string.IsNullOrWhiteSpace(raw)) return;
            _traceEntries.Add("control received: " + Preview(raw));

            string payload = raw;
            if (payload[0] == ControlPrefix) payload = payload.Substring(1).Trim();
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
                    // Current compact q format: q^score^question^resolution^effect.
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
                        _traceEntries.Add("q rejected: expected q^score^question^resolution^effect or legacy 7-field vector, received " + parts.Length + " fields");
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
                    _traceEntries.Add("i rejected: expected at least i^source^subject, received " + parts.Length + " fields");
                    return;
                }

                // Preferred control format: i^source^subject^score. Be tolerant of an omitted score
                // or accidental extra ^ inside the subject.
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
