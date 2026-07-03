using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Ai;

namespace Wds.SixStars.Api.Screen
{
    /// <summary>
    /// OpenAI-compatible vision adapter for local VLMs. Many local servers expose /v1/chat/completions
    /// with image_url data URLs. If your server needs another shape, adapt CreatePayload in this one class.
    /// </summary>
    public sealed class OpenAiCompatibleVisionScreenSummarizer : IScreenSummarizer, IDisposable
    {
        private readonly LocalChatModelOptions _options;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public OpenAiCompatibleVisionScreenSummarizer(LocalChatModelOptions options)
            : this(options, null)
        {
        }

        public OpenAiCompatibleVisionScreenSummarizer(LocalChatModelOptions options, HttpClient? httpClient)
        {
            _options = options;
            _httpClient = httpClient ?? new HttpClient();
            _ownsClient = httpClient == null;
            _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds));
        }

        public async Task<ScreenCaptureSummary> SummarizeAsync(ScreenSummarizationRequest request, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = CreatePayload(request);
            string json = JsonSerializer.Serialize(payload, _jsonOptions);
            using HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("Screen summarizer returned " + (int)response.StatusCode + ": " + body);
            }

            string content = ExtractContent(body);
            ScreenCaptureSummary? summary = TryParseSummary(content);
            if (summary == null)
            {
                summary = new ScreenCaptureSummary
                {
                    CapturedAt = request.Capture.CapturedAt,
                    ActiveApp = request.Capture.ActiveApp,
                    ActivityType = "unknown",
                    LikelyActivity = "The screen model returned unparseable output.",
                    CompanionSafeSummary = content.Length > 800 ? content.Substring(0, 800) : content,
                    Sensitivity = "normal",
                    Confidence = 0.35
                };
            }

            summary.CapturedAt = request.Capture.CapturedAt;
            if (string.IsNullOrWhiteSpace(summary.ActiveApp)) summary.ActiveApp = request.Capture.ActiveApp;
            if (string.IsNullOrWhiteSpace(summary.ImageReference)) summary.ImageReference = request.Capture.ImageReference;
            if (request.IncludeBase64InSummary && string.IsNullOrWhiteSpace(summary.ImageBase64Jpeg))
            {
                summary.ImageBase64Jpeg = Convert.ToBase64String(request.Capture.ImageBytes ?? Array.Empty<byte>());
            }
            return summary;
        }

        private Dictionary<string, object> CreatePayload(ScreenSummarizationRequest request)
        {
            string prompt = "You are a screen summarizer for a desktop AI companion app. Return compact JSON only with properties: activeApp, activityType, likelyActivity, companionSafeSummary, visibleTextHighlights, visibleProblems, sensitivity, confidence. Do not include passwords, API keys, tokens, full emails, private messages, addresses, or medical/bank details. If the screen looks sensitive, set sensitivity to sensitive or highly_sensitive and keep the summary vague. Do not invent details. Active window title: " + (request.Capture.ActiveWindowTitle ?? string.Empty) + ". Active process: " + (request.Capture.ActiveProcessName ?? string.Empty) + ".";

            var content = new object[]
            {
                new Dictionary<string, object> { { "type", "text" }, { "text", prompt } },
                new Dictionary<string, object>
                {
                    { "type", "image_url" },
                    { "image_url", new Dictionary<string, object> { { "url", request.Capture.ToDataUrl() } } }
                }
            };

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "model", _options.Model },
                { "temperature", 0.1 },
                { "messages", new object[] { new Dictionary<string, object> { { "role", "user" }, { "content", content } } } }
            };

            if (_options.UseJsonSchemaResponseFormat)
            {
                payload["response_format"] = new Dictionary<string, object>
                {
                    { "type", "json_object" }
                };
            }

            return payload;
        }

        private static string ExtractContent(string responseBody)
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                JsonElement first = choices[0];
                if (first.TryGetProperty("message", out JsonElement message) && message.TryGetProperty("content", out JsonElement content))
                {
                    if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
                    return content.GetRawText();
                }
            }
            return responseBody;
        }

        private ScreenCaptureSummary? TryParseSummary(string content)
        {
            string json = ExtractJsonObject(content);
            try
            {
                return JsonSerializer.Deserialize<ScreenCaptureSummary>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractJsonObject(string text)
        {
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start >= 0 && end > start) return text.Substring(start, end - start + 1);
            return text;
        }

        public void Dispose()
        {
            if (_ownsClient) _httpClient.Dispose();
        }
    }
}
