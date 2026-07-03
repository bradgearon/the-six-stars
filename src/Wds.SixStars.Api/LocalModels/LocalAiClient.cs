using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Ai;

namespace Wds.SixStars.Api.LocalModels
{
    /// <summary>
    /// Thin reusable client for the local model service layer. It speaks the same OpenAI-compatible
    /// chat contract used by LM Studio, but defaults to the Six Stars local LLM sidecar.
    /// </summary>
    public sealed class LocalAiClient : ILocalChatModelClient, IDisposable
    {
        private readonly OpenAiCompatibleChatModelClient _chatClient;

        public LocalAiClient(LocalModelServiceOptions serviceOptions)
        {
            ServiceOptions = serviceOptions ?? throw new ArgumentNullException(nameof(serviceOptions));
            LocalModelPaths.ApplyEnvironment(ServiceOptions);
            _chatClient = new OpenAiCompatibleChatModelClient(ServiceOptions.Chat);
            _chatClient.ExchangeLogged += (_, args) => ExchangeLogged?.Invoke(this, args);
        }

        public LocalModelServiceOptions ServiceOptions { get; }

        public event EventHandler<LocalChatModelExchangeEventArgs>? ExchangeLogged;

        public Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            return _chatClient.CompleteJsonAsync(messages, cancellationToken);
        }

        public Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, object? responseFormat, int? maxTokens, string debugLabel, CancellationToken cancellationToken)
        {
            return _chatClient.CompleteJsonAsync(messages, responseFormat, maxTokens, debugLabel, cancellationToken);
        }

        public void Dispose()
        {
            _chatClient.Dispose();
        }
    }
}
