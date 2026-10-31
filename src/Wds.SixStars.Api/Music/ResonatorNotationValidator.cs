using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Wds.SixStars.Api.Music
{
    /// <summary>Explicit-event composition profile for Resonator v0.1.</summary>
    public static class ResonatorNotationValidator
    {
        private const string Pitch = @"[A-G](?:#|b)?[0-7]";
        private static readonly Regex Meter = new Regex(@"^(?:[1-9]|1[0-2])/(?:2|4|8|16)$");
        private static readonly Regex Header = new Regex(@"^tempo=(?<tempo>\d{2,3})\s+(?<meter>\d{1,2}/(?:2|4|8|16))\s+key=[A-G](?:#|b)?m?(?=\s|\||$)");
        private static readonly Regex Event = new Regex(@"^(?<body>" + Pitch + @"|\[" + Pitch + @"(?:\s+" + Pitch + @")+\]|_|-)(?<duration>::|:(?![0-9v])|,|\.)?(?:@(?<offset>[+-](?:\d+/[1-9]\d*q|\d+(?:\.\d+)?%|\d+t)))?(?::(?<gate>\d+(?:\.\d+)?)%)?(?::v(?<velocity>\d{1,3}))?(?<articulation>[>^!?~])?$");
        private static readonly HashSet<string> Dynamics = new HashSet<string> { "pp", "p", "mp", "mf", "f", "ff", "/", "//" };
        public static bool IsMeter(string meter) => meter != null && Meter.IsMatch(meter);

        /// <summary>Keep every event; add only enough silence to finish the final measure.</summary>
        public static string CompleteFinalMeasure(string notation, MusicCompositionRequest request, out int actualBars)
        {
            var errors = Validate(notation, request);
            if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors));
            notation = notation.Trim();
            int units = 0;
            foreach (Match token in Regex.Matches(notation.Substring(Header.Match(notation).Length).Replace('|', ' '), @"\[[^\[\]]*\][^\s]*|[^\s]+")) {
                var note = Event.Match(token.Value);
                if (!note.Success) continue;
                switch (note.Groups["duration"].Value) {
                    case ",": units += 2; break;
                    case ".": units += 1; break;
                    case ":": units += 8; break;
                    case "::": units += 16; break;
                    default: units += 4; break;
                }
            }
            string[] meter = request.Meter.Split('/');
            int perBar = int.Parse(meter[0]) * 16 / int.Parse(meter[1]);
            actualBars = (units + perBar - 1) / perBar;
            int padding = actualBars * perBar - units;
            var result = new System.Text.StringBuilder(notation);
            // Use duration suffixes already supported by Resonator; never trim or retime a note.
            while (padding >= 4) { result.Append(" _"); padding -= 4; }
            if (padding >= 2) { result.Append(" _,"); padding -= 2; }
            if (padding > 0) result.Append(" _.");
            if (result.Length > 65536) throw new ArgumentException("Notation plus final-measure rests exceeds 65536 characters.");
            return result.ToString();
        }

        public static IReadOnlyList<string> Validate(string notation, MusicCompositionRequest request)
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(notation) || notation.Length > 65536) return new[] { "Notation must contain 1–65536 characters." };
            notation = notation.Trim();
            Match header = Header.Match(notation);
            if (!header.Success) return new[] { "Invalid header. Use tempo=" + request.Tempo + " " + request.Meter + " key=C for C major, or key=Am for A minor. Do not append major/minor words or request fields." };
            if (int.Parse(header.Groups["tempo"].Value, CultureInfo.InvariantCulture) != request.Tempo) errors.Add("Header tempo differs from request.");
            if (header.Groups["meter"].Value != request.Meter) errors.Add("Header meter differs from request.");
            if (!IsMeter(request.Meter)) return new[] { "Invalid requested meter." };
            int units = 0, notes = 0;
            bool canHold = false;
            // Separators are visual annotations, never timing constraints.
            string music = notation.Substring(header.Length).Replace('|', ' ');
            var tokens = Regex.Matches(music, @"\[[^\[\]]*\][^\s]*|[^\s]+", RegexOptions.None);
            foreach (Match token in tokens)
            {
                    string word = token.Value;
                    if (Dynamics.Contains(word)) continue;
                    Match e = Event.Match(word);
                    if (!e.Success) { errors.Add("Unsupported event " + word.Substring(0, Math.Min(60, word.Length))); continue; }
                    string body = e.Groups["body"].Value;
                    if ((body == "_" || body == "-") && (e.Groups["offset"].Success || e.Groups["gate"].Success || e.Groups["velocity"].Success || e.Groups["articulation"].Success))
                        errors.Add("Holds and rests accept only duration modifiers.");
                    if (body == "-" && !canHold) errors.Add("Hold has no preceding note/chord.");
                    if (body != "-") canHold = body != "_";
                    if (body != "_" && body != "-") notes++;
                    if (e.Groups["gate"].Success && (!double.TryParse(e.Groups["gate"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double gate) || gate < 1 || gate > 200))
                        errors.Add("Gate must be 1–200%.");
                    if (e.Groups["velocity"].Success && (int.Parse(e.Groups["velocity"].Value) < 1 || int.Parse(e.Groups["velocity"].Value) > 127))
                        errors.Add("Velocity must be 1–127.");
                    if (units == 0 && e.Groups["offset"].Value.StartsWith("-", StringComparison.Ordinal)) errors.Add("First event cannot have a negative onset.");
                    switch (e.Groups["duration"].Value) {
                        case ",": units += 2; break;
                        case ".": units += 1; break;
                        case ":": units += 8; break;
                        case "::": units += 16; break;
                        default: units += 4; break;
                    }
                }
            if (notes == 0) errors.Add("Score must contain pitched notes or chords.");
            return errors;
        }
    }
}
