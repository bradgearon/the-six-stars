using System;
using System.Collections.Generic;
using System.Linq;
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
    /// OpenAI-compatible vision adapter for local VLMs such as LM Studio.
    ///
    /// The canonical chat-completions vision payload shape is:
    /// messages: [
    ///   { role: "user", content: [
    ///       { type: "text", text: "..." },
    ///       { type: "image_url", image_url: { url: "data:image/jpeg;base64,..." } }
    ///   ] }
    /// ]
    ///
    /// Local OpenAI-compatible servers vary most often on response_format support, not on the image shape,
    /// so this class sends the image in the standard OpenAI-compatible shape and retries without response_format
    /// when a local server rejects that parameter.
    /// </summary>
    public sealed class OpenAiCompatibleVisionScreenSummarizer : IScreenSummarizer, IDisposable
    {
        private readonly LocalChatModelOptions _options;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

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
            Exception? lastException = null;
            string? lastBody = null;

            foreach (string responseFormatMode in BuildResponseFormatModes())
            {
                try
                {
                    Dictionary<string, object> payload = CreatePayload(request, responseFormatMode);
                    string body = await SendPayloadAsync(payload, responseFormatMode, request, cancellationToken).ConfigureAwait(false);
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

                    if (responseFormatMode != BuildResponseFormatModes()[0])
                    {
                        System.Diagnostics.Debug.WriteLine("[Screen LLM] Succeeded after response_format fallback mode: " + responseFormatMode);
                    }

                    return summary;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    lastBody = ex.Message;
                    System.Diagnostics.Debug.WriteLine("[Screen LLM] Attempt with response_format mode '" + responseFormatMode + "' failed: " + ex.Message);
                }
            }

            throw new InvalidOperationException("Screen LLM endpoint failed after OpenAI-compatible vision fallbacks. Last error: " + (lastBody ?? lastException?.Message ?? "unknown"), lastException);
        }

        private List<string> BuildResponseFormatModes()
        {
            if (_options.UseJsonSchemaResponseFormat)
            {
                // For vision calls, many local OpenAI-compatible servers reject response_format even when text calls accept it.
                // Try json_object first, then send the exact same OpenAI-compatible image payload without response_format.
                return new List<string> { "json_object", "none" };
            }

            return new List<string> { "none" };
        }

        private Dictionary<string, object> CreatePayload(ScreenSummarizationRequest request, string responseFormatMode)
        {
            string prompt = BuildPrompt(request);
            string imageDataUrl = request.Capture.ToDataUrl();

            object[] userContent = new object[]
            {
                new Dictionary<string, object>
                {
                    { "type", "text" },
                    { "text", prompt }
                },
                new Dictionary<string, object>
                {
                    { "type", "image_url" },
                    { "image_url", new Dictionary<string, object>
                        {
                            { "url", imageDataUrl }
                        }
                    }
                }
            };

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "model", _options.Model },
                { "temperature", 0.1 },
                { "messages", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "content", userContent }
                        }
                    }
                }
            };

            if (_options.MaxTokens > 0)
            {
                payload["max_tokens"] = _options.MaxTokens;
            }

            if (string.Equals(responseFormatMode, "json_object", StringComparison.OrdinalIgnoreCase))
            {
                payload["response_format"] = new Dictionary<string, object>
                {
                    { "type", "json_object" }
                };
            }

            return payload;
        }

        private static string BuildPrompt(ScreenSummarizationRequest request)
        {
            StringBuilder prompt = new StringBuilder();
            prompt.AppendLine("You are a screen summarizer for a desktop AI companion app.");
            prompt.AppendLine("Look at the attached screenshot image and return compact JSON only.");
            prompt.AppendLine("Use this JSON shape exactly:");
            prompt.AppendLine("{\"activeApp\":\"\",\"activityType\":\"\",\"likelyActivity\":\"\",\"companionSafeSummary\":\"\",\"visibleTextHighlights\":[],\"visibleProblems\":[],\"sensitivity\":\"normal\",\"confidence\":0.0}");
            prompt.AppendLine("Do not include passwords, API keys, tokens, full emails, private messages, addresses, or medical/bank details.");
            prompt.AppendLine("If the screen looks sensitive, set sensitivity to sensitive or highly_sensitive and keep the summary vague.");
            prompt.AppendLine("Do not invent details. If the image is unclear, say that confidence is low.");
            prompt.AppendLine("Active window title: " + (request.Capture.ActiveWindowTitle ?? string.Empty));
            prompt.AppendLine("Active process: " + (request.Capture.ActiveProcessName ?? string.Empty));
            prompt.AppendLine("Image reference: " + (request.Capture.ImageReference ?? string.Empty));
            prompt.AppendLine("Image dimensions: " + request.Capture.Width.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x" + request.Capture.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return prompt.ToString();
        }

        private async Task<string> SendPayloadAsync(Dictionary<string, object> payload, string responseFormatMode, ScreenSummarizationRequest request, CancellationToken cancellationToken)
        {
            string json = JsonSerializer.Serialize(payload, _jsonOptions);
            LogRequestForDebug(payload, responseFormatMode, request, json.Length);

            using HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            System.Diagnostics.Debug.WriteLine("[Screen LLM] Sending OpenAI-compatible vision POST to " + _options.EndpointUrl + " mode=" + responseFormatMode);
            using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
#if NET5_0_OR_GREATER
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#else
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif
            System.Diagnostics.Debug.WriteLine("[Screen LLM] Response status: " + (int)response.StatusCode + " " + response.StatusCode + "; body chars=" + body.Length);
            LogResponseForDebug(responseFormatMode, response, body);

            if (!response.IsSuccessStatusCode)
            {
                string preview = body.Substring(0, Math.Min(1200, body.Length));
                if ((int)response.StatusCode == 401 && string.IsNullOrWhiteSpace(_options.ApiKey))
                {
                    throw new InvalidOperationException("Screen LLM returned 401 Unauthorized and no API key was configured. LM Studio may require a token. Set LM_API_TOKEN, SIX_STARS_SCREEN_LLM_API_KEY, SIX_STARS_LLM_API_KEY, or disable API key requirement in LM Studio. Raw response: " + preview);
                }

                throw new InvalidOperationException("Screen summarizer returned " + (int)response.StatusCode + " " + response.StatusCode + " in mode " + responseFormatMode + ": " + preview);
            }

            return body;
        }

        private void LogRequestForDebug(Dictionary<string, object> payload, string responseFormatMode, ScreenSummarizationRequest request, int jsonLength)
        {
            try
            {
                long imageBytes = request.Capture.ImageBytes?.LongLength ?? 0;
                string dataUrlPrefix = request.Capture.ToDataUrl();
                int commaIndex = dataUrlPrefix.IndexOf(',');
                if (commaIndex >= 0) dataUrlPrefix = dataUrlPrefix.Substring(0, commaIndex + 1) + "...";

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("================ SCREEN LLM REQUEST ================");
                sb.AppendLine("Time: " + DateTimeOffset.Now.ToString("O"));
                sb.AppendLine("Endpoint: " + _options.EndpointUrl);
                sb.AppendLine("Model: " + _options.Model);
                sb.AppendLine("ResponseFormatMode: " + responseFormatMode);
                sb.AppendLine("Authorization: " + (string.IsNullOrWhiteSpace(_options.ApiKey) ? "none" : "Bearer ***"));
                sb.AppendLine("ImageMimeType: " + request.Capture.MimeType);
                sb.AppendLine("ImageBytes: " + imageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.AppendLine("ImageDataUrl: " + dataUrlPrefix);
                sb.AppendLine("ImageDimensions: " + request.Capture.Width.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x" + request.Capture.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.AppendLine("SerializedPayloadChars: " + jsonLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.AppendLine("Payload shape: messages[0].content[0]={type:text,text}, messages[0].content[1]={type:image_url,image_url:{url:data:image/jpeg;base64,...}}");
                sb.AppendLine("============== END SCREEN LLM REQUEST ==============");
                System.Diagnostics.Debug.WriteLine(sb.ToString());
            }
            catch
            {
                // Debug logging should never break screen capture.
            }
        }

        private static void LogResponseForDebug(string responseFormatMode, HttpResponseMessage response, string body)
        {
            try
            {
                string preview = body.Length > 2000 ? body.Substring(0, 2000) + "..." : body;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=============== SCREEN LLM RESPONSE ===============");
                sb.AppendLine("Time: " + DateTimeOffset.Now.ToString("O"));
                sb.AppendLine("ResponseFormatMode: " + responseFormatMode);
                sb.AppendLine("Status: " + (int)response.StatusCode + " " + response.StatusCode);
                sb.AppendLine("BodyChars: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.AppendLine(preview);
                sb.AppendLine("============= END SCREEN LLM RESPONSE =============");
                System.Diagnostics.Debug.WriteLine(sb.ToString());
            }
            catch
            {
                // Debug logging should never break screen capture.
            }
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
