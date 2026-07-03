namespace Wds.SixStars.Api.Speech
{
    public enum SpeechPipelineStatus
    {
        Stopped,
        HotwordListening,
        HotwordCandidate,
        ConversationListening,
        SpeechActive,
        Transcribing,
        StarSpeaking,
        Error
    }
}
