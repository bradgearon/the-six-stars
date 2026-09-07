namespace Wds.SixStars.Api.Ai
{
    public sealed class LocalChatModelOptions
    {
        public string EndpointUrl { get; set; } = "http://localhost:8080/v1/chat/completions";
        public string Model { get; set; } = "six-stars-gemma4";
        public string? ApiKey { get; set; }
        public double Temperature { get; set; } = 0.65;
        /// <summary>Default max_tokens to send to the backend. 0 means do not send max_tokens; let the backend stop on EOS/context.</summary>
        public int MaxTokens { get; set; } = 0;
        public bool UseJsonSchemaResponseFormat { get; set; } = false;
        public bool IncludeJsonObjectFallbackInstruction { get; set; } = true;

        /// <summary>
        /// Local reasoning models such as Gemma 4 can spend their generation budget in
        /// reasoning_content, leaving message.content empty. For fast companion speech, keep
        /// reasoning disabled unless a specific backend/config intentionally enables it.
        /// </summary>
        public bool DisableReasoning { get; set; } = true;

        /// <summary>When DisableReasoning=true, send this budget with local OpenAI-compatible requests.</summary>
        public int ReasoningBudget { get; set; } = 0;

        /// <summary>When DisableReasoning=true, request no extracted reasoning channel from llama.cpp-compatible servers.</summary>
        public string ReasoningFormat { get; set; } = "none";

        /// <summary>
        /// Send request-level reasoning controls. The launcher also disables reasoning through
        /// llama-server accepts these reasoning-control fields and the launcher disables reasoning explicitly for the local model.
        /// </summary>
        public bool SendReasoningControlFields { get; set; } = false;

        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>When true, every local chat-completions request/response exchange is persisted for prompt analysis.</summary>
        public bool LogExchangesToDisk { get; set; } = true;

        /// <summary>Directory for LLM JSONL/readable logs. Relative paths resolve under the app base directory.</summary>
        public string ExchangeLogDirectory { get; set; } = "logs/llm-exchanges";

        /// <summary>Safety cap for each logged request or response text block. Increase when auditing very large prompts.</summary>
        public int ExchangeLogMaxTextChars { get; set; } = 2000000;
    }
}
