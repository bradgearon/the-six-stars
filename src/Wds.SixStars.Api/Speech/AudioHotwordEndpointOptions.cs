namespace Wds.SixStars.Api.Speech
{
    public sealed class AudioHotwordEndpointOptions
    {
        public string Provider { get; set; } = "local-http";
        public string EndpointUrl { get; set; } = "http://localhost:8090/detect-hotword";
        public string? ApiKey { get; set; }
        public int TimeoutSeconds { get; set; } = 10;
        public string PayloadShape { get; set; } = "MultipartWav";
    }
}
