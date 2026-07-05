namespace Wds.SixStars.Api.Ai
{
    public sealed class LocalChatModelOptions
    {
        public string EndpointUrl { get; set; } = "http://localhost:1234/v1/chat/completions";
        public string Model { get; set; } = "local-model";
        public string? ApiKey { get; set; }
        public double Temperature { get; set; } = 0.65;
        /// <summary>Default max_tokens to send to the backend. Use 0 only when intentionally letting the backend/model limit apply.</summary>
        public int MaxTokens { get; set; } = 768;
        public bool UseJsonSchemaResponseFormat { get; set; } = false;
        public bool IncludeJsonObjectFallbackInstruction { get; set; } = true;
        public int TimeoutSeconds { get; set; } = 120;
    }
}
