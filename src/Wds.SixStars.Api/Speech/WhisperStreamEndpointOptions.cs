namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// Placeholder settings for the concrete local whisper implementation you plug in later.
    /// Example targets: whisper.cpp server, Whisper.net in-process model, or an Ollama/LM Studio sidecar.
    /// </summary>
    public sealed class WhisperStreamEndpointOptions
    {
        public string Provider { get; set; } = "whispercpp";
        public string EndpointUrl { get; set; } = "http://localhost:8000/inference";
        public string ModelPath { get; set; } = string.Empty;
        /// <summary>Remote API model ID, for example whisper-large-v3-turbo on Groq.</summary>
        public string Model { get; set; } = string.Empty;
        public string? ApiKey { get; set; }
        public string Language { get; set; } = "en";
        public int TimeoutSeconds { get; set; } = 60;
    }
}
