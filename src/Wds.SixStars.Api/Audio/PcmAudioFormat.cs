namespace Wds.SixStars.Api.Audio
{
    /// <summary>
    /// Unity, WPF, NAudio, WASAPI, or any other client should convert microphone input
    /// into this simple PCM shape before pushing it into the Six Stars speech pipeline.
    /// The first implementation assumes little-endian signed 16-bit PCM.
    /// </summary>
    public sealed class PcmAudioFormat
    {
        public int SampleRateHz { get; set; } = 16000;
        public int Channels { get; set; } = 1;
        public int BitsPerSample { get; set; } = 16;
        public string Encoding { get; set; } = "pcm_s16le";

        public int BytesPerSecond
        {
            get { return SampleRateHz * Channels * (BitsPerSample / 8); }
        }

        public static PcmAudioFormat Mono16Khz()
        {
            return new PcmAudioFormat
            {
                SampleRateHz = 16000,
                Channels = 1,
                BitsPerSample = 16,
                Encoding = "pcm_s16le"
            };
        }
    }
}
