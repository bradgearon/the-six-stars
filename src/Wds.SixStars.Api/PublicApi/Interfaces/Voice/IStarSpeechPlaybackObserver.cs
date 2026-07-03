namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// Implemented by speech-input systems that need to know when generated Star audio is playing.
    /// This lets the microphone pipeline mute or gate input so it does not transcribe the Star's own voice.
    /// </summary>
    public interface IStarSpeechPlaybackObserver
    {
        void NotifyStarSpeechStarted();
        void NotifyStarSpeechCompleted();
    }
}
