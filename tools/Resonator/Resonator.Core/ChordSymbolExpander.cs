using System.Text.RegularExpressions;

namespace Resonator.Core;

public static partial class ChordSymbolExpander
{
    [GeneratedRegex(@"^(?<root>[A-Ga-g](?:#|b)?)(?<quality>m11|maj7|m7|add9|sus2|sus4|dim|aug|m|7)?(?:/(?<bass>[A-Ga-g](?:#|b)?))?$")]
    private static partial Regex ChordRegex();

    public static IReadOnlyList<int>? TryExpand(string symbol, int defaultOctave, int midiNoteForC0 = 12)
    {
        var match = ChordRegex().Match(symbol.Trim());
        if (!match.Success) return null;

        string rootText = match.Groups["root"].Value + defaultOctave;
        int root = Pitch.ToMidi(rootText, defaultOctave, midiNoteForC0);
        string quality = match.Groups["quality"].Value;

        int[] intervals = quality switch
        {
            "m" => [0, 3, 7],
            "7" => [0, 4, 7, 10],
            "maj7" => [0, 4, 7, 11],
            "m7" => [0, 3, 7, 10],
            "dim" => [0, 3, 6],
            "aug" => [0, 4, 8],
            "sus2" => [0, 2, 7],
            "sus4" => [0, 5, 7],
            "add9" => [0, 4, 7, 14],
            "m11" => [0, 3, 7, 10, 14, 17],
            _ => [0, 4, 7]
        };

        var notes = intervals.Select(i => Math.Clamp(root + i, 0, 127)).ToList();
        if (match.Groups["bass"].Success)
        {
            int bass = Pitch.ToMidi(match.Groups["bass"].Value + (defaultOctave - 1), defaultOctave - 1, midiNoteForC0);
            notes.Insert(0, bass);
        }
        return notes;
    }
}
