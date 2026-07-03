using System;

namespace Wds.SixStars.Api.Speech
{
    public sealed class SpeechSegmentEventArgs : EventArgs
    {
        public SpeechSegmentEventArgs(SpeechSegment segment)
        {
            Segment = segment;
        }

        public SpeechSegment Segment { get; }
    }
}
