namespace Wds.SixStars.Api.Speech
{
    public sealed class WhisperTranscriptionOptions
    {
        public string Language { get; set; } = "en";
        public bool TranslateToEnglish { get; set; }
        public bool IncludeTimestamps { get; set; }
        public string Prompt { get; set; } = "Names that may appear: Elusiaa, Kaelia, Maera, Khavia, Auerlis, Nyxari, Benaveil, Leviathan, Virethorn.";
    }
}
