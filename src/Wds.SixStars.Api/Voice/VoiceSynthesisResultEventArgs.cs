using System;

namespace Wds.SixStars.Api.Voice
{
    public sealed class VoiceSynthesisResultEventArgs : EventArgs
    {
        public VoiceSynthesisResultEventArgs(VoiceSynthesisResult result)
        {
            Result = result;
        }

        public VoiceSynthesisResult Result { get; }
    }
}
