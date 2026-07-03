namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// High-level listening state for the speech pipeline.
    /// HotwordDetection means audio must pass the light hotword model before Whisper is used.
    /// Conversation means any speech-shaped segment is sent to Whisper until the conversation idles out.
    /// </summary>
    public enum SpeechListenMode
    {
        HotwordDetection,
        Conversation
    }
}
