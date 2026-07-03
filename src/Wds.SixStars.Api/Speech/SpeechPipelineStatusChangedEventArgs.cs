using System;

namespace Wds.SixStars.Api.Speech
{
    public sealed class SpeechPipelineStatusChangedEventArgs : EventArgs
    {
        public SpeechPipelineStatusChangedEventArgs(SpeechPipelineStatus status, string message)
        {
            Status = status;
            Message = message;
        }

        public SpeechPipelineStatus Status { get; }
        public string Message { get; }
    }
}
