using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Resonator.Core;

public sealed partial class ResonatorParser
{
    private readonly ResonatorProfile _profile;
    private readonly List<string> _warnings = [];
    private readonly Dictionary<int, long> _microChannelAvailableAt = new();
    private List<NoteEvent> _lastSoundingGroup = [];
    private int _currentVelocity;
    private bool _percussionMode;

    public ResonatorParser(ResonatorProfile profile)
    {
        _profile = profile;
        _currentVelocity = profile.DefaultVelocity;
        for (int ch = 0; ch < 16; ch++)
        {
            if (ch != profile.DefaultChannel && ch != profile.Percussion.Channel)
                _microChannelAvailableAt[ch] = 0;
        }
    }

    public ParseResult Parse(string notation)
    {
        if (string.IsNullOrWhiteSpace(notation))
            throw new ResonatorParseException("Notation cannot be empty.");

        _warnings.Clear();
        _lastSoundingGroup = [];
        _currentVelocity = _profile.DefaultVelocity;
        _percussionMode = false;

        var composition = new ResonatorComposition
        {
            Ppq = _profile.Ppq,
            Tempo = _profile.DefaultTempo
        };

        long cursor = 0;
        var tokens = TokenizeAndExpand(notation);

        foreach (string rawToken in tokens)
        {
            string token = rawToken.Trim();
            if (token.Length == 0 || token is "|" or "/" or "//")
                continue;

            if (TryModeCommand(token))
                continue;

            if (TryHeader(token, composition))
                continue;

            if (TryDynamic(token))
                continue;

            if (token.StartsWith("art=", StringComparison.OrdinalIgnoreCase))
            {
                EmitArticulation(token[4..], cursor, composition);
                continue;
            }

            if (token.StartsWith("payoff(", StringComparison.OrdinalIgnoreCase) && token.EndsWith(')'))
            {
                token = token[7..^1] + "^";
            }

            if (token.StartsWith('(') && token.EndsWith(')'))
            {
                string inner = token[1..^1];
                var innerResult = ParseInline(inner, cursor, composition);
                cursor = innerResult;
                continue;
            }

            if (token.StartsWith('['))
            {
                cursor += ParseChord(token, cursor, composition);
                continue;
            }

            if (token.StartsWith('_'))
            {
                var spec = ParseModifiers(token, baseLength: 1);
                cursor += DurationTicks(spec.DurationKind, _profile.Ppq);
                _lastSoundingGroup = [];
                continue;
            }

            if (token.StartsWith('-'))
            {
                var spec = ParseModifiers(token, baseLength: 1);
                long extension = DurationTicks(spec.DurationKind, _profile.Ppq);
                if (_lastSoundingGroup.Count == 0)
                    throw new ResonatorParseException("Hold '-' has no preceding note or chord to extend.");

                foreach (var oldNote in _lastSoundingGroup.ToArray())
                {
                    int index = composition.Events.IndexOf(oldNote);
                    var extended = oldNote with { DurationTicks = oldNote.DurationTicks + extension };
                    composition.Events[index] = extended;
                    _lastSoundingGroup[_lastSoundingGroup.IndexOf(oldNote)] = extended;
                }
                cursor += extension;
                continue;
            }

            cursor += ParseSingleEvent(token, cursor, composition);
        }

        composition.LengthTicks = Math.Max(cursor, composition.Events.OfType<NoteEvent>().Select(n => n.Tick + n.DurationTicks).DefaultIfEmpty(0).Max());
        return new ParseResult(composition, _warnings.ToArray());
    }

    private long ParseInline(string notation, long cursor, ResonatorComposition composition)
    {
        foreach (string token in TokenizeAndExpand(notation))
        {
            if (token is "|" or "/" or "//" || token.Length == 0) continue;
            if (TryModeCommand(token)) continue;
            if (token.StartsWith("art=", StringComparison.OrdinalIgnoreCase)) { EmitArticulation(token[4..], cursor, composition); continue; }
            if (token.StartsWith('[')) cursor += ParseChord(token, cursor, composition);
            else if (token.StartsWith('_')) cursor += DurationTicks(ParseModifiers(token, 1).DurationKind, _profile.Ppq);
            else cursor += ParseSingleEvent(token, cursor, composition);
        }
        return cursor;
    }

    private long ParseSingleEvent(string token, long cursor, ResonatorComposition composition)
    {
        var spec = ParseModifiers(token, baseLength: 1);
        long nominal = DurationTicks(spec.DurationKind, _profile.Ppq);
        long offset = ParseOffsetTicks(spec.OffsetText, _profile.Ppq);
        long start = Math.Max(0, cursor + offset);
        long gate = Math.Max(1, (long)Math.Round(nominal * spec.GatePercent / 100.0));
        int velocity = ApplyArticulationVelocity(spec, spec.Velocity ?? _currentVelocity);
        if (spec.Staccato) gate = Math.Max(1, gate / 2);
        if (spec.Tenuto) gate = Math.Max(gate, nominal);
        if (spec.Legato) gate = Math.Max(gate, nominal + Math.Max(1, _profile.Ppq / 32));

        if (_percussionMode)
        {
            int drumNote = MapPercussionNote(spec.BaseToken);

            if (!spec.HasExplicitGate)
                gate = Math.Min(gate, Math.Max(1, _profile.Percussion.DefaultDurationTicks));

            var note = new NoteEvent(start, gate, drumNote, velocity, _profile.Percussion.Channel, IsPercussion: true);
            composition.Events.Add(note);
            _lastSoundingGroup = [note];
            return nominal;
        }

        string pitchToken = spec.BaseToken;
        string? bendTarget = null;
        int bendIndex = FindBendSeparator(pitchToken);
        if (bendIndex > 0)
        {
            bendTarget = pitchToken[(bendIndex + 1)..];
            pitchToken = pitchToken[..bendIndex];
        }

        double cents = ExtractCents(ref pitchToken);
        int midi = Pitch.ToMidi(pitchToken, _profile.DefaultOctave, _profile.MidiNoteForC0);
        double? bendTargetCents = null;
        if (!string.IsNullOrWhiteSpace(bendTarget))
        {
            int targetMidi = Pitch.ToMidi(bendTarget, _profile.DefaultOctave, _profile.MidiNoteForC0);
            bendTargetCents = (targetMidi - midi) * 100.0;
        }

        int channel = (Math.Abs(cents) > 0.0001 || bendTargetCents.HasValue)
            ? AllocateMicroChannel(start, start + gate)
            : _profile.DefaultChannel;

        var evt = new NoteEvent(start, gate, midi, velocity, channel, cents, bendTargetCents, false);
        composition.Events.Add(evt);
        _lastSoundingGroup = [evt];
        return nominal;
    }

    private long ParseChord(string token, long cursor, ResonatorComposition composition)
    {
        int close = token.IndexOf(']');
        if (close < 0) throw new ResonatorParseException($"Unclosed chord token '{token}'.");

        string inside = token[1..close].Trim();
        string suffix = token[(close + 1)..];
        var spec = ParseModifiers("X" + suffix, baseLength: 1);
        long nominal = DurationTicks(spec.DurationKind, _profile.Ppq);
        long offset = ParseOffsetTicks(spec.OffsetText, _profile.Ppq);
        long start = Math.Max(0, cursor + offset);
        long gate = Math.Max(1, (long)Math.Round(nominal * spec.GatePercent / 100.0));
        int velocity = ApplyArticulationVelocity(spec, spec.Velocity ?? _currentVelocity);
        if (spec.Staccato) gate = Math.Max(1, gate / 2);
        if (spec.Tenuto) gate = Math.Max(gate, nominal);

        var created = new List<NoteEvent>();
        var parts = inside.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!_percussionMode && parts.Length == 1 && LooksLikeChordSymbol(parts[0]))
        {
            var expanded = ChordSymbolExpander.TryExpand(parts[0], _profile.DefaultOctave, _profile.MidiNoteForC0)
                ?? throw new ResonatorParseException($"Unknown chord symbol '[{inside}]'.");
            foreach (int midi in expanded)
            {
                var evt = new NoteEvent(start, gate, midi, velocity, _profile.DefaultChannel);
                composition.Events.Add(evt);
                created.Add(evt);
            }
        }
        else
        {
            foreach (string part in parts)
            {
                if (_percussionMode)
                {
                    int drumNote = MapPercussionNote(part);
                    long drumGate = spec.HasExplicitGate
                        ? gate
                        : Math.Min(gate, Math.Max(1, _profile.Percussion.DefaultDurationTicks));
                    var evt = new NoteEvent(start, drumGate, drumNote, velocity, _profile.Percussion.Channel, IsPercussion: true);
                    composition.Events.Add(evt);
                    created.Add(evt);
                }
                else
                {
                    string pitchPart = part;
                    double cents = ExtractCents(ref pitchPart);
                    int midi = Pitch.ToMidi(pitchPart, _profile.DefaultOctave, _profile.MidiNoteForC0);
                    int channel = Math.Abs(cents) > 0.0001 ? AllocateMicroChannel(start, start + gate) : _profile.DefaultChannel;
                    var evt = new NoteEvent(start, gate, midi, velocity, channel, cents);
                    composition.Events.Add(evt);
                    created.Add(evt);
                }
            }
        }

        _lastSoundingGroup = created;
        return nominal;
    }

    private void EmitArticulation(string name, long cursor, ResonatorComposition composition)
    {
        if (!_profile.Articulations.TryGetValue(name, out var articulation))
            throw new ResonatorParseException($"Unknown articulation '{name}'. Add it to resonator.config.json.");

        int channel = articulation.Channel ?? _profile.DefaultChannel;
        long lead = articulation.LeadTicks ?? _profile.ArticulationDefaults.LeadTicks;
        long tick = Math.Max(0, cursor - lead);

        if (articulation.KeySwitchMidiNote.HasValue || !string.IsNullOrWhiteSpace(articulation.KeySwitch))
        {
            int midi = articulation.KeySwitchMidiNote ?? Pitch.ToMidi(articulation.KeySwitch!, 0, _profile.MidiNoteForC0);
            int velocity = articulation.Velocity ?? _profile.ArticulationDefaults.Velocity;
            int duration = articulation.DurationTicks ?? _profile.ArticulationDefaults.DurationTicks;
            composition.Events.Add(new NoteEvent(tick, duration, midi, velocity, channel));
        }

        foreach (var cc in articulation.ControlChanges)
            composition.Events.Add(new ControlChangeEvent(tick, cc.Channel ?? channel, cc.Controller, cc.Value));
    }

    private int AllocateMicroChannel(long start, long end)
    {
        foreach (var pair in _microChannelAvailableAt.OrderBy(p => p.Key))
        {
            if (pair.Value <= start)
            {
                _microChannelAvailableAt[pair.Key] = end;
                return pair.Key;
            }
        }

        int fallback = _microChannelAvailableAt.Keys.FirstOrDefault();
        _warnings.Add("Too many overlapping independent pitch bends; reusing a MIDI channel may cause bend conflicts.");
        if (fallback == 0 && _profile.DefaultChannel != 0) fallback = 0;
        if (_microChannelAvailableAt.ContainsKey(fallback)) _microChannelAvailableAt[fallback] = end;
        return fallback;
    }

    private bool TryHeader(string token, ResonatorComposition composition)
    {
        if (token.StartsWith("tempo=", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(token[6..], out int bpm) || bpm <= 0)
                throw new ResonatorParseException($"Invalid tempo '{token}'.");
            composition.Tempo = bpm;
            return true;
        }

        if (int.TryParse(token, out int bareTempo) && bareTempo is >= 20 and <= 400)
        {
            composition.Tempo = bareTempo;
            return true;
        }

        var meter = MeterRegex().Match(token);
        if (meter.Success)
        {
            composition.Numerator = int.Parse(meter.Groups[1].Value);
            composition.Denominator = int.Parse(meter.Groups[2].Value);
            return true;
        }

        if (token.StartsWith("key=", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("home=", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("ahd=", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("world", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("swing=", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("humanize.", StringComparison.OrdinalIgnoreCase) ||
            (token.StartsWith('<') && token.EndsWith('>')))
        {
            // Metadata is accepted now so model output remains forward-compatible.
            return true;
        }

        return false;
    }

    private bool TryDynamic(string token)
    {
        int? velocity = token.ToLowerInvariant() switch
        {
            "pp" => 42,
            "p" => 56,
            "mp" => 72,
            "mf" => 88,
            "f" => 104,
            "ff" => 120,
            _ => null
        };
        if (!velocity.HasValue) return false;
        _currentVelocity = velocity.Value;
        return true;
    }

    private static int ApplyArticulationVelocity(EventSpec spec, int velocity)
    {
        if (spec.StrongAccent) velocity += 22;
        else if (spec.Accent) velocity += 12;
        return Math.Clamp(velocity, 1, 127);
    }

    private static bool LooksLikeChordSymbol(string text)
    {
        // A single literal pitch inside [] stays a single note; symbols such as Am, C#m11, D/F# expand.
        return Regex.IsMatch(text, @"^[A-Ga-g](?:#|b)?(?:m|7|maj7|m7|m11|add9|sus2|sus4|dim|aug|/).+") ||
               Regex.IsMatch(text, @"^[A-Ga-g](?:#|b)?m$") ||
               Regex.IsMatch(text, @"^[A-Ga-g](?:#|b)?7$");
    }

    private bool TryModeCommand(string token)
    {
        if (token.Equals("drums", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("percussion", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("mode=drums", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("mode=percussion", StringComparison.OrdinalIgnoreCase))
        {
            _percussionMode = true;
            return true;
        }

        if (token.Equals("melody", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("melodic", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("mode=melody", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("mode=melodic", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("mode=notes", StringComparison.OrdinalIgnoreCase))
        {
            _percussionMode = false;
            return true;
        }

        return false;
    }

    private int MapPercussionNote(string inputToken)
    {
        string token = inputToken.Trim();
        if (_profile.Percussion.NoteMap.TryGetValue(token, out int exact))
            return ValidateMidiNote(exact, token);

        string pitchClass = StripOctave(token);
        if (_profile.Percussion.NoteMap.TryGetValue(pitchClass, out int mapped))
            return ValidateMidiNote(mapped, pitchClass);

        throw new ResonatorParseException(
            $"No percussion mapping exists for '{inputToken}'. Add '{pitchClass}' (or the exact token) to percussion.noteMap in resonator.config.json.");
    }

    private static int ValidateMidiNote(int midiNote, string mappingKey)
    {
        if (midiNote is < 0 or > 127)
            throw new ResonatorParseException($"Percussion mapping '{mappingKey}' has invalid MIDI note {midiNote}; expected 0-127.");
        return midiNote;
    }

    private static string StripOctave(string token)
    {
        var match = Regex.Match(token, @"^(?<pc>[A-Ga-g](?:#|b)?)(?:-?\d+)?$");
        return match.Success ? match.Groups["pc"].Value : token;
    }

    private static int FindBendSeparator(string token)
    {
        int idx = token.IndexOf('~');
        return idx > 0 && idx < token.Length - 1 ? idx : -1;
    }

    private static double ExtractCents(ref string token)
    {
        var match = CentsRegex().Match(token);
        if (!match.Success) return 0;
        double cents = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        token = token.Remove(match.Index, match.Length);
        return cents;
    }

    private static long DurationTicks(DurationKind kind, int ppq) => kind switch
    {
        DurationKind.Whole => ppq * 4L,
        DurationKind.Half => ppq * 2L,
        DurationKind.Quarter => ppq,
        DurationKind.Eighth => ppq / 2L,
        DurationKind.Sixteenth => ppq / 4L,
        _ => ppq
    };

    private static long ParseOffsetTicks(string? offsetText, int ppq)
    {
        if (string.IsNullOrWhiteSpace(offsetText)) return 0;
        string s = offsetText.Trim();
        int sign = s[0] == '-' ? -1 : 1;
        s = s[1..];

        if (s.EndsWith('t'))
            return sign * long.Parse(s[..^1], CultureInfo.InvariantCulture);

        if (s.EndsWith('%'))
        {
            double pct = double.Parse(s[..^1], CultureInfo.InvariantCulture);
            return (long)Math.Round(sign * ppq * pct / 100.0);
        }

        if (s.EndsWith('q'))
        {
            string amount = s[..^1];
            double quarters;
            if (amount.Contains('/'))
            {
                var parts = amount.Split('/');
                quarters = double.Parse(parts[0], CultureInfo.InvariantCulture) /
                           double.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            else quarters = double.Parse(amount, CultureInfo.InvariantCulture);
            return (long)Math.Round(sign * quarters * ppq);
        }

        throw new ResonatorParseException($"Unsupported onset offset '@{offsetText}'. Use %, ticks (t), or quarter fractions (q).");
    }

    private static EventSpec ParseModifiers(string token, int baseLength)
    {
        var spec = new EventSpec { Original = token, BaseToken = token };
        string work = token;

        // A trailing @ is a payoff marker. It currently affects no MIDI data by itself.
        if (work.EndsWith('@') && !work.Contains("@+") && !work.Contains("@-"))
            work = work[..^1];

        var velocity = VelocityRegex().Match(work);
        if (velocity.Success)
        {
            spec.Velocity = int.Parse(velocity.Groups[1].Value);
            work = work.Remove(velocity.Index, velocity.Length);
        }

        var gate = GateRegex().Match(work);
        if (gate.Success)
        {
            spec.GatePercent = double.Parse(gate.Groups[1].Value, CultureInfo.InvariantCulture);
            spec.HasExplicitGate = true;
            work = work.Remove(gate.Index, gate.Length);
        }

        var offset = OffsetRegex().Match(work);
        if (offset.Success)
        {
            spec.OffsetText = offset.Groups[1].Value;
            work = work.Remove(offset.Index, offset.Length);
        }

        // Articulation suffixes.
        bool changed = true;
        while (changed && work.Length > 0)
        {
            changed = false;
            switch (work[^1])
            {
                case '>': spec.Accent = true; work = work[..^1]; changed = true; break;
                case '^': spec.StrongAccent = true; work = work[..^1]; changed = true; break;
                case '!': spec.Staccato = true; work = work[..^1]; changed = true; break;
                case '?': spec.Tenuto = true; work = work[..^1]; changed = true; break;
                case '~': spec.Legato = true; work = work[..^1]; changed = true; break;
            }
        }

        if (work.EndsWith('@'))
            work = work[..^1];

        if (work.EndsWith("::", StringComparison.Ordinal))
        {
            spec.DurationKind = DurationKind.Whole;
            work = work[..^2];
        }
        else if (work.EndsWith(':'))
        {
            spec.DurationKind = DurationKind.Half;
            work = work[..^1];
        }
        else if (work.EndsWith(','))
        {
            spec.DurationKind = DurationKind.Eighth;
            work = work[..^1];
        }
        else if (work.EndsWith('.'))
        {
            spec.DurationKind = DurationKind.Sixteenth;
            work = work[..^1];
        }
        else
        {
            spec.DurationKind = DurationKind.Quarter;
        }

        spec.BaseToken = work;
        return spec;
    }

    private static IReadOnlyList<string> TokenizeAndExpand(string notation)
    {
        string expanded = ExpandRepeats(notation);
        var tokens = new List<string>();
        var sb = new StringBuilder();
        int square = 0, round = 0;

        void Flush()
        {
            if (sb.Length == 0) return;
            tokens.Add(sb.ToString());
            sb.Clear();
        }

        for (int i = 0; i < expanded.Length; i++)
        {
            char c = expanded[i];
            if (c == '[') square++;
            if (c == ']') square--;
            if (c == '(') round++;
            if (c == ')') round--;

            if (square == 0 && round == 0 && char.IsWhiteSpace(c))
            {
                Flush();
                continue;
            }

            if (square == 0 && round == 0 && c == '|')
            {
                Flush(); tokens.Add("|"); continue;
            }

            if (square == 0 && round == 0 && c == '/')
            {
                // A numeric slash belongs to a meter or onset fraction, not a phrase.
                // Keep 4/4 and A@+1/8q intact while still accepting A/B as a boundary.
                if (sb.Length > 0 && char.IsDigit(sb[sb.Length - 1])
                    && i + 1 < expanded.Length && char.IsDigit(expanded[i + 1]))
                {
                    sb.Append(c);
                    continue;
                }
                Flush();
                if (i + 1 < expanded.Length && expanded[i + 1] == '/')
                {
                    tokens.Add("//"); i++;
                }
                else tokens.Add("/");
                continue;
            }

            sb.Append(c);
        }
        Flush();
        return tokens;
    }

    private static string ExpandRepeats(string input)
    {
        // Expands xN{...}. Nested repeat groups are supported recursively.
        var regex = new Regex(@"x(?<count>\d+)\{(?<body>[^{}]*)\}", RegexOptions.IgnoreCase);
        string current = input;
        while (true)
        {
            var match = regex.Match(current);
            if (!match.Success) break;
            int count = int.Parse(match.Groups["count"].Value);
            string body = match.Groups["body"].Value;
            string replacement = string.Join(' ', Enumerable.Repeat(body, count));
            current = current[..match.Index] + replacement + current[(match.Index + match.Length)..];
        }
        return current;
    }

    private sealed class EventSpec
    {
        public string Original { get; set; } = "";
        public string BaseToken { get; set; } = "";
        public DurationKind DurationKind { get; set; }
        public string? OffsetText { get; set; }
        public double GatePercent { get; set; } = 100;
        public bool HasExplicitGate { get; set; }
        public int? Velocity { get; set; }
        public bool Accent { get; set; }
        public bool StrongAccent { get; set; }
        public bool Staccato { get; set; }
        public bool Tenuto { get; set; }
        public bool Legato { get; set; }
    }

    private enum DurationKind { Whole, Half, Quarter, Eighth, Sixteenth }

    [GeneratedRegex(@"^(\d{1,2})/(\d{1,2})$")]
    private static partial Regex MeterRegex();

    [GeneratedRegex(@":v(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex VelocityRegex();

    [GeneratedRegex(@":([0-9]+(?:\.[0-9]+)?)%")]
    private static partial Regex GateRegex();

    [GeneratedRegex(@"@([+-](?:[0-9]+(?:\.[0-9]+)?%|[0-9]+t|[0-9]+(?:/[0-9]+)?q))")]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"([+-][0-9]+(?:\.[0-9]+)?)c", RegexOptions.IgnoreCase)]
    private static partial Regex CentsRegex();
}
