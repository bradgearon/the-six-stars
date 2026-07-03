using System;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Speech
{
    public sealed class StarSpeechResponseEventArgs : EventArgs
    {
        public StarSpeechResponseEventArgs(RecognizedSpeech recognizedSpeech, StarInteractionResponse response)
        {
            RecognizedSpeech = recognizedSpeech;
            Response = response;
        }

        public RecognizedSpeech RecognizedSpeech { get; }
        public StarInteractionResponse Response { get; }
    }
}
