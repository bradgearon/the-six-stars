namespace Wds.SixStars.Api.Ai
{
    /// <summary>Configuration for the hosted Google AI Studio / Gemini API Gemma client.</summary>
    public sealed class GoogleAiStudioChatModelOptions
    {
        public string EndpointBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
        public string Model { get; set; } = "gemma-4-26b-a4b-it";
        public string ApiKey { get; set; } = string.Empty;
        public string ThinkingLevel { get; set; } = "high";
        public bool EnableGoogleSearch { get; set; }
        public double Temperature { get; set; } = 0.65;
        public int MaxTokens { get; set; }
        public int TimeoutSeconds { get; set; } = 600;
        public bool LogExchangesToDisk { get; set; } = true;
        public string ExchangeLogDirectory { get; set; } = "logs/llm-exchanges";
        public int ExchangeLogMaxTextChars { get; set; } = 2000000;
    }
}
