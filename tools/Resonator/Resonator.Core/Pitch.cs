using System.Text.RegularExpressions;

namespace Resonator.Core;

public static partial class Pitch
{
    [GeneratedRegex(@"^(?<letter>[A-Ga-g])(?<acc>#{1,2}|b{1,2})?(?<oct>-?\d+)?$")]
    private static partial Regex PitchRegex();

    private static readonly Dictionary<char, int> NaturalSemitones = new()
    {
        ['C'] = 0, ['D'] = 2, ['E'] = 4, ['F'] = 5,
        ['G'] = 7, ['A'] = 9, ['B'] = 11
    };

    public static int ToMidi(string text, int defaultOctave = 4, int midiNoteForC0 = 12)
    {
        var match = PitchRegex().Match(text.Trim());
        if (!match.Success)
            throw new FormatException($"Invalid pitch '{text}'.");

        char letter = char.ToUpperInvariant(match.Groups["letter"].Value[0]);
        int semitone = NaturalSemitones[letter];
        string accidental = match.Groups["acc"].Value;
        foreach (char c in accidental)
            semitone += c == '#' ? 1 : -1;

        int octave = match.Groups["oct"].Success
            ? int.Parse(match.Groups["oct"].Value)
            : defaultOctave;

        int midi = midiNoteForC0 + octave * 12 + semitone;
        if (midi is < 0 or > 127)
            throw new ArgumentOutOfRangeException(nameof(text), $"Pitch '{text}' is outside MIDI range.");
        return midi;
    }

    public static int SemitoneDistance(int fromMidi, int toMidi) => toMidi - fromMidi;
}
