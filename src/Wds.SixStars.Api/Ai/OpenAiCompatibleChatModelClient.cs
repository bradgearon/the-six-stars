using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Ai
{
    public sealed class OpenAiCompatibleChatModelClient : ILocalChatModelClient, IDisposable
    {
        private readonly LocalChatModelOptions _options;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly bool _ownsClient;

        public event EventHandler<LocalChatModelExchangeEventArgs>? ExchangeLogged;

        public OpenAiCompatibleChatModelClient(LocalChatModelOptions options)
            : this(options, null)
        {
        }

        public OpenAiCompatibleChatModelClient(LocalChatModelOptions options, HttpClient? httpClient)
        {
            _options = options;
            _httpClient = httpClient ?? new HttpClient();
            _ownsClient = httpClient == null;
            _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds));
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
        }

        public Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            return CompleteJsonAsync(messages, StarResponseJsonSchema.CreateResponseFormat(), null, "full-star-response", cancellationToken);
        }

        public async Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, object? responseFormat, int? maxTokens, string debugLabel, CancellationToken cancellationToken)
        {
            List<Dictionary<string, string>> wireMessages = messages
                .Select(x => new Dictionary<string, string>
                {
                    { "role", x.Role },
                    { "content", x.Content }
                })
                .ToList();

            int configuredMaxTokens = maxTokens.HasValue ? maxTokens.Value : _options.MaxTokens;
            int? effectiveMaxTokens = configuredMaxTokens > 0 ? configuredMaxTokens : (int?)null;
            System.Diagnostics.Debug.WriteLine($"[OpenAiCompatibleChatModelClient] Sending POST to {_options.EndpointUrl}");
            System.Diagnostics.Debug.WriteLine($"[OpenAiCompatibleChatModelClient] Model: {_options.Model}, Messages: {wireMessages.Count}, Temp: {_options.Temperature}, MaxTokens: {DescribeMaxTokens(effectiveMaxTokens)}, DebugLabel: {debugLabel}");

            List<string> responseFormatModes = BuildResponseFormatModeFallbacks();
            Exception? lastException = null;
            string? lastErrorBody = null;

            foreach (string responseFormatMode in responseFormatModes)
            {
                try
                {
                    string body = await SendOnceAsync(wireMessages, responseFormatMode, responseFormat, effectiveMaxTokens, debugLabel, cancellationToken).ConfigureAwait(false);
                    string content = ExtractContent(body);
                    LogExchange("LLM extracted content before Star parsing", FormatExtractedContentForDebug(responseFormatMode + " / " + debugLabel, content));
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        throw new InvalidOperationException("Model returned an empty content field.");
                    }

                    if (responseFormatMode != responseFormatModes[0])
                    {
                        System.Diagnostics.Debug.WriteLine($"[OpenAiCompatibleChatModelClient] Succeeded after response_format fallback mode: {responseFormatMode}");
                    }

                    return content;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    lastErrorBody = ex.Message;
                    System.Diagnostics.Debug.WriteLine($"[OpenAiCompatibleChatModelClient] Attempt with response_format mode '{responseFormatMode}' failed: {ex.Message}");

                    if (!LooksLikeResponseFormatProblem(ex.Message) && responseFormatMode == responseFormatModes[0])
                    {
                        continue;
                    }
                }
            }

            throw new InvalidOperationException("LLM endpoint failed after response_format fallbacks. Last error: " + (lastErrorBody ?? lastException?.Message ?? "unknown"), lastException);
        }

        private List<string> BuildResponseFormatModeFallbacks()
        {
            List<string> modes = new List<string>();
            if (_options.UseJsonSchemaResponseFormat)
            {
                modes.Add("json_schema");
                modes.Add("json_object");
                modes.Add("none");
            }
            else
            {
                modes.Add("json_object");
                modes.Add("none");
            }

            return modes;
        }

        private async Task<string> SendOnceAsync(List<Dictionary<string, string>> wireMessages, string responseFormatMode, object? responseFormat, int? maxTokens, string debugLabel, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "model", _options.Model },
                { "messages", wireMessages },
                { "temperature", _options.Temperature }
            };

            if (maxTokens.HasValue && maxTokens.Value > 0)
            {
                payload["max_tokens"] = maxTokens.Value;
            }

            if (string.Equals(responseFormatMode, "json_schema", StringComparison.OrdinalIgnoreCase))
            {
                payload["response_format"] = responseFormat ?? StarResponseJsonSchema.CreateResponseFormat();
            }
            else if (string.Equals(responseFormatMode, "json_object", StringComparison.OrdinalIgnoreCase))
            {
                payload["response_format"] = new Dictionary<string, object> { { "type", "json_object" } };
            }

            string json = JsonSerializer.Serialize(payload, _jsonOptions);
            LogExchange("LLM request before send", FormatRequestForDebug(responseFormatMode + " / " + debugLabel, payload, maxTokens));

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl))
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                }

                System.Diagnostics.Debug.WriteLine($"[OpenAiCompatibleChatModelClient] POST mode={responseFormatMode}");

                using (HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
#if NET5_0_OR_GREATER
                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#else
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif
                    System.Diagnostics.Debug.WriteLine($"[OpenAiCompatibleChatModelClient] Response status: {(int)response.StatusCode} {response.StatusCode}; body chars={body.Length}");
                    LogExchange("LLM raw HTTP response before Star parsing", FormatResponseForDebug(responseFormatMode + " / " + debugLabel, response, body));

                    if (!response.IsSuccessStatusCode)
                    {
                        string preview = body.Substring(0, Math.Min(500, body.Length));
                        System.Diagnostics.Debug.WriteLine($"[OpenAiCompatibleChatModelClient] Error response: {preview}");
                        if ((int)response.StatusCode == 401 && string.IsNullOrWhiteSpace(_options.ApiKey))
                        {
                            throw new InvalidOperationException("LLM endpoint returned 401 Unauthorized and no API key was configured. LM Studio may require a token. Set LM_API_TOKEN or SIX_STARS_LLM_API_KEY, or disable API key requirement in LM Studio. Raw response: " + preview);
                        }

                        throw new InvalidOperationException($"LLM endpoint returned {(int)response.StatusCode} {response.StatusCode}: {preview}");
                    }

                    return body;
                }
            }
        }


        private void LogExchange(string title, string formattedText)
        {
            try
            {
                ExchangeLogged?.Invoke(this, new LocalChatModelExchangeEventArgs(title, formattedText));
            }
            catch
            {
                // Debug logging must never break the LLM request path.
            }
        }

        private static string DescribeMaxTokens(int? maxTokens)
        {
            return maxTokens.HasValue && maxTokens.Value > 0
                ? maxTokens.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "not sent; backend/model default";
        }

        private string FormatRequestForDebug(string responseFormatMode, Dictionary<string, object> payload, int? effectiveMaxTokens)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("================ LLM REQUEST ================");
            sb.AppendLine("Time: " + DateTimeOffset.Now.ToString("O"));
            sb.AppendLine("Endpoint: " + _options.EndpointUrl);
            sb.AppendLine("Model: " + _options.Model);
            sb.AppendLine("ResponseFormatMode: " + responseFormatMode);
            sb.AppendLine("Temperature: " + _options.Temperature);
            sb.AppendLine("MaxTokens: " + DescribeMaxTokens(effectiveMaxTokens));
            sb.AppendLine("TimeoutSeconds: " + _options.TimeoutSeconds);
            sb.AppendLine("Authorization: " + (string.IsNullOrWhiteSpace(_options.ApiKey) ? "none" : "Bearer ***"));
            sb.AppendLine();
            sb.AppendLine("Messages:");
            if (payload.TryGetValue("messages", out object? messagesObj) && messagesObj is IEnumerable<Dictionary<string, string>> messages)
            {
                int index = 0;
                foreach (Dictionary<string, string> message in messages)
                {
                    message.TryGetValue("role", out string? role);
                    message.TryGetValue("content", out string? content);
                    sb.AppendLine("---- message[" + index + "] role=" + (role ?? "unknown") + " chars=" + (content?.Length ?? 0) + " ----");
                    sb.AppendLine(content ?? string.Empty);
                    index++;
                }
            }
            else
            {
                sb.AppendLine(PrettyJson(payload));
            }

            sb.AppendLine("============== END LLM REQUEST ==============");
            return sb.ToString();
        }

        private static string FormatResponseForDebug(string responseFormatMode, HttpResponseMessage response, string body)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("============= LLM RAW RESPONSE =============");
            sb.AppendLine("Time: " + DateTimeOffset.Now.ToString("O"));
            sb.AppendLine("ResponseFormatMode: " + responseFormatMode);
            sb.AppendLine("Status: " + (int)response.StatusCode + " " + response.StatusCode);
            sb.AppendLine("BodyChars: " + (body?.Length ?? 0));
            sb.AppendLine();
            sb.AppendLine(PrettyJsonOrText(body ?? string.Empty));
            sb.AppendLine("=========== END LLM RAW RESPONSE ===========");
            return sb.ToString();
        }

        private static string FormatExtractedContentForDebug(string responseFormatMode, string content)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("========= LLM EXTRACTED STAR CONTENT ========");
            sb.AppendLine("Time: " + DateTimeOffset.Now.ToString("O"));
            sb.AppendLine("ResponseFormatMode: " + responseFormatMode);
            sb.AppendLine("ContentChars: " + (content?.Length ?? 0));
            sb.AppendLine("This is the exact text passed into StructuredStarAgent before JSON extraction/parsing.");
            sb.AppendLine();
            sb.AppendLine(PrettyJsonOrText(content ?? string.Empty));
            sb.AppendLine("======= END LLM EXTRACTED STAR CONTENT ======");
            return sb.ToString();
        }

        private static string PrettyJson(object value)
        {
            try
            {
                return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return value.ToString() ?? string.Empty;
            }
        }

        private static string PrettyJsonOrText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(value);
                return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return value;
            }
        }

        private static bool LooksLikeResponseFormatProblem(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.IndexOf("response_format", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("json_schema", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("json_object", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("schema", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("unsupported", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("Could not find model content", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("empty content", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void Dispose()
        {
            if (_ownsClient)
            {
                _httpClient.Dispose();
            }
        }

        private static string ExtractContent(string responseBody)
        {
            using (JsonDocument document = JsonDocument.Parse(responseBody))
            {
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    JsonElement first = choices[0];
                    if (first.TryGetProperty("message", out JsonElement message))
                    {
                        string? messageContent = ExtractMessageContent(message);
                        if (!string.IsNullOrWhiteSpace(messageContent))
                        {
                            return messageContent;
                        }
                    }

                    if (first.TryGetProperty("delta", out JsonElement delta))
                    {
                        string? deltaContent = ExtractMessageContent(delta);
                        if (!string.IsNullOrWhiteSpace(deltaContent))
                        {
                            return deltaContent;
                        }
                    }

                    if (first.TryGetProperty("text", out JsonElement text))
                    {
                        return ElementToString(text);
                    }
                }

                if (root.TryGetProperty("message", out JsonElement directMessage))
                {
                    string? directMessageContent = ExtractMessageContent(directMessage);
                    if (!string.IsNullOrWhiteSpace(directMessageContent))
                    {
                        return directMessageContent;
                    }
                }

                if (root.TryGetProperty("content", out JsonElement directContent))
                {
                    return ElementToString(directContent);
                }

                if (root.TryGetProperty("response", out JsonElement response))
                {
                    return ElementToString(response);
                }

                if (root.TryGetProperty("text", out JsonElement rootText))
                {
                    return ElementToString(rootText);
                }
            }

            throw new InvalidOperationException("Could not find model content in local chat response.");
        }

        private static string? ExtractMessageContent(JsonElement message)
        {
            if (message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }

            if (message.ValueKind != JsonValueKind.Object)
            {
                return ElementToString(message);
            }

            if (message.TryGetProperty("content", out JsonElement content))
            {
                return ElementToString(content);
            }

            if (message.TryGetProperty("text", out JsonElement text))
            {
                return ElementToString(text);
            }

            if (message.TryGetProperty("response", out JsonElement response))
            {
                return ElementToString(response);
            }

            return null;
        }

        private static string ElementToString(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString() ?? string.Empty;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                StringBuilder sb = new StringBuilder();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(item.GetString());
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        if (item.TryGetProperty("text", out JsonElement text))
                        {
                            sb.Append(ElementToString(text));
                        }
                        else if (item.TryGetProperty("content", out JsonElement content))
                        {
                            sb.Append(ElementToString(content));
                        }
                    }
                }

                return sb.ToString();
            }

            return element.ToString();
        }
    }
}
