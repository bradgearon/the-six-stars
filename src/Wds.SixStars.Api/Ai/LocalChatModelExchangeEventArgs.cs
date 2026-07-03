using System;

namespace Wds.SixStars.Api.Ai
{
    public sealed class LocalChatModelExchangeEventArgs : EventArgs
    {
        public LocalChatModelExchangeEventArgs(string title, string formattedText)
        {
            Title = title;
            FormattedText = formattedText;
            LoggedAt = DateTimeOffset.Now;
        }

        public string Title { get; }
        public string FormattedText { get; }
        public DateTimeOffset LoggedAt { get; }
    }
}
