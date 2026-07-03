namespace Wds.SixStars.Api.Audio
{
    public interface ISpeechActivityDetector
    {
        SpeechActivityResult Analyze(PcmAudioChunk chunk, VoiceCaptureSettings settings);
    }
}
