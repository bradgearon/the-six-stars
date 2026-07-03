namespace Wds.SixStars.Api.Ai
{
    public sealed class LocalChatModelOptions
    {
        public string EndpointUrl { get; set; } = "http://localhost:1234/v1/chat/completions";
        public string Model { get; set; } = "local-model";
        public string? ApiKey { get; set; }
        public double Temperature { get; set; } = 0.65;
        /// <summary>0 means do not send max_tokens; let the backend/model limit apply.</summary>
        public int MaxTokens { get; set; } = 0;
        public bool UseJsonSchemaResponseFormat { get; set; } = true;
        public bool IncludeJsonObjectFallbackInstruction { get; set; } = true;
        public int TimeoutSeconds { get; set; } = 120;
    }
}
