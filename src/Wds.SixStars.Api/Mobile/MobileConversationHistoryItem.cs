using System;

namespace Wds.SixStars.Api.Mobile;

public sealed class MobileConversationHistoryItem
{
    public DateTimeOffset CreatedAt { get; set; }
    public string RequestType { get; set; } = string.Empty;
    public string PlayerText { get; set; } = string.Empty;
    public string StarResponse { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}
