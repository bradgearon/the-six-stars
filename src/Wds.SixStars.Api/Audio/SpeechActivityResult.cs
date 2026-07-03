namespace Wds.SixStars.Api.Audio
{
    public sealed class SpeechActivityResult
    {
        public bool IsSpeech { get; set; }
        public double Rms { get; set; }
        public double Threshold { get; set; }
    }
}
