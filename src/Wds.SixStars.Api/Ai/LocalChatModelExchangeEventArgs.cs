using System;

namespace Wds.SixStars.Api.Ai
{
    public sealed class LocalChatModelExchangeEventArgs : EventArgs
    {
        public LocalChatModelExchangeEventArgs(string title, string formattedText)
            : this(title, formattedText, "debug", LlmExchangeFileLogger.CreateExchangeId("llm"), string.Empty, string.Empty, 0)
        {
        }

        public LocalChatModelExchangeEventArgs(
            string title,
            string formattedText,
            string kind,
            string requestId,
            string debugLabel,
            string responseFormatMode,
            long sequence)
        {
            Title = title ?? string.Empty;
            FormattedText = formattedText ?? string.Empty;
            Kind = string.IsNullOrWhiteSpace(kind) ? "debug" : kind;
            RequestId = string.IsNullOrWhiteSpace(requestId) ? LlmExchangeFileLogger.CreateExchangeId("llm") : requestId;
            DebugLabel = debugLabel ?? string.Empty;
            ResponseFormatMode = responseFormatMode ?? string.Empty;
            Sequence = sequence;
            LoggedAt = DateTimeOffset.Now;
        }

        public string Title { get; }
        public string FormattedText { get; }
        public string Kind { get; }
        public string RequestId { get; }
        public string DebugLabel { get; }
        public string ResponseFormatMode { get; }
        public long Sequence { get; }
        public DateTimeOffset LoggedAt { get; }
    }
}
