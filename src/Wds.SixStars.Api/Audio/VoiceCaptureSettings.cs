namespace Wds.SixStars.Api.Audio
{
    public sealed class VoiceCaptureSettings
    {
        /// <summary>
        /// Any sound above RMS threshold for this long becomes a candidate utterance.
        /// Keep this low because the hotword may be at the very start of speech.
        /// </summary>
        public int MinSoundMs { get; set; } = 100;

        /// <summary>
        /// Recommended incoming audio chunk size for Unity/desktop mic adapters.
        /// The pipeline accepts other sizes, but 100 ms is easy to reason about.
        /// </summary>
        public int TargetChunkMs { get; set; } = 100;

        /// <summary>
        /// Size of each rolling audio window sent to the lightweight hotword model.
        /// The first probe can happen as soon as MinSoundMs is reached; later probes use
        /// this rolling window size so names that are not exactly at the first syllable can still be detected.
        /// </summary>
        public int HotwordProbeMs { get; set; } = 700;

        /// <summary>
        /// How often to launch another rolling hotword probe while the early utterance is still being captured.
        /// Example: 300 ms means probes around 100 ms, 400 ms, 700 ms, 1000 ms, etc. until HotwordMaxSearchMs.
        /// </summary>
        public int HotwordProbeEveryMs { get; set; } = 300;

        /// <summary>
        /// Maximum amount of speech audio to search for the hotword before waiting for the utterance to end.
        /// Useful for phrases like "hey... Elusiaa" without sending the entire utterance to the hotword model forever.
        /// </summary>
        public int HotwordMaxSearchMs { get; set; } = 1800;

        /// <summary>
        /// How much silence ends the current speech segment.
        /// </summary>
        public int SilenceEndMs { get; set; } = 900;

        /// <summary>
        /// Audio kept before speech begins, so the first syllable/hotword is not clipped.
        /// </summary>
        public int PreRollMs { get; set; } = 500;

        /// <summary>
        /// Minimum delay between wake detections.
        /// </summary>
        public int WakeCooldownMs { get; set; } = 2000;

        /// <summary>
        /// Once a wake word opens conversation mode, this much non-speech input returns to hotword mode.
        /// </summary>
        public int ConversationIdleMs { get; set; } = 15000;

        /// <summary>
        /// If true, idle mode requires a hotword before audio is sent to Whisper.
        /// If false, every speech segment is transcribed. Useful for early dev/testing.
        /// </summary>
        public bool RequireHotwordWhenIdle { get; set; } = true;

        /// <summary>
        /// If true, a detected hotword opens conversation mode so follow-up speech can be transcribed without repeating the name.
        /// </summary>
        public bool EnterConversationModeOnHotword { get; set; } = true;

        /// <summary>
        /// If true, mic chunks are ignored while generated Star speech is being played.
        /// This prevents the app from transcribing its own TTS output.
        /// </summary>
        public bool MuteInputWhileStarSpeaking { get; set; } = true;

        /// <summary>
        /// RMS threshold for the starter dependency-free VAD. Tune per microphone.
        /// Normal speech near the mic is often roughly 0.02-0.12 after normalization.
        /// </summary>
        public double RmsSpeechThreshold { get; set; } = 0.018;

        /// <summary>
        /// Prevent very long accidental captures. The pipeline will flush once this is exceeded.
        /// </summary>
        public int MaxSegmentMs { get; set; } = 30000;
    }
}
