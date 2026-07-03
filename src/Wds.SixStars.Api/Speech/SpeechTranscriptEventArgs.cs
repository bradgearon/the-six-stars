using System;

namespace Wds.SixStars.Api.Speech
{
    public sealed class SpeechTranscriptEventArgs : EventArgs
    {
        public SpeechTranscriptEventArgs(SpeechTranscript transcript)
        {
            Transcript = transcript;
        }

        public SpeechTranscript Transcript { get; }
    }
}
