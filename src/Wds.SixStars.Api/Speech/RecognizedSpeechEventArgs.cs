using System;

namespace Wds.SixStars.Api.Speech
{
    public sealed class RecognizedSpeechEventArgs : EventArgs
    {
        public RecognizedSpeechEventArgs(RecognizedSpeech recognizedSpeech)
        {
            RecognizedSpeech = recognizedSpeech;
        }

        public RecognizedSpeech RecognizedSpeech { get; }
    }
}
