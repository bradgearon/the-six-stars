using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Ai
{
    public interface ILocalChatModelClient
    {
        Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);
        Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, object? responseFormat, int? maxTokens, string debugLabel, CancellationToken cancellationToken);
        Task<string> CompleteTextStreamingAsync(IReadOnlyList<ChatMessage> messages, int? maxTokens, string debugLabel, System.Action<string>? onTextDelta, CancellationToken cancellationToken);
    }
}
