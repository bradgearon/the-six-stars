using Wds.SixStars.Api.Audio;

namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// Reusable voice input setup helpers for desktop and Unity. Unity can use this same
    /// configuration shape, convert microphone samples to PCM16, and push them into
    /// WhisperSpeechInputPipeline without any WPF or NAudio dependency.
    /// </summary>
    public static class VoiceInputConfigurationFactory
    {
        public static VoiceCaptureSettings CreateTranscribeOnActivitySettings(
            int minSoundMs = 120,
            int silenceEndMs = 850,
            int preRollMs = 450,
            double rmsSpeechThreshold = 0.018,
            int maxSegmentMs = 30000,
            bool muteInputWhileStarSpeaking = true)
        {
            return new VoiceCaptureSettings
            {
                RequireHotwordWhenIdle = false,
                EnterConversationModeOnHotword = false,
                MinSoundMs = minSoundMs,
                SilenceEndMs = silenceEndMs,
                PreRollMs = preRollMs,
                RmsSpeechThreshold = rmsSpeechThreshold,
                MaxSegmentMs = maxSegmentMs,
                MuteInputWhileStarSpeaking = muteInputWhileStarSpeaking,
                ConversationIdleMs = 0,
                WakeCooldownMs = 0,
                HotwordProbeMs = 0,
                HotwordProbeEveryMs = 0,
                HotwordMaxSearchMs = 0
            };
        }
    }
}
