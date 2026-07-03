using System;

namespace Wds.SixStars.Api.Speech
{
    public sealed class SpeechSegmentIgnoredEventArgs : EventArgs
    {
        public SpeechSegmentIgnoredEventArgs(SpeechSegment segment, string reason, HotwordDetectionResult? hotwordResult)
        {
            Segment = segment;
            Reason = reason;
            HotwordResult = hotwordResult;
        }

        public SpeechSegment Segment { get; }
        public string Reason { get; }
        public HotwordDetectionResult? HotwordResult { get; }
    }
}
