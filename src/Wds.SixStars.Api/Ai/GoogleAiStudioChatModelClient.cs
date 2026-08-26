using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Hosted Gemma client for Google AI Studio / Gemini API. It adapts the application's existing
    /// OpenAI-shaped chat abstraction to generateContent/streamGenerateContent so the rest of the
    /// Star runtime does not need a separate hosted code path.
    /// </summary>
    public sealed class GoogleAiStudioChatModelClient : ILocalChatModelClient, IDisposable
    {
        private static readonly HashSet<string> SupportedSchemaKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "$id", "$defs", "$ref", "$anchor", "type", "format", "title", "description", "enum",
            "items", "prefixItems", "minItems", "maxItems", "minimum", "maximum", "anyOf", "oneOf",
            "properties", "additionalProperties", "required", "propertyOrdering"
        };

        private readonly GoogleAiStudioChatModelOptions _options;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly LlmExchangeFileLogger? _exchangeFileLogger;
        private readonly SemaphoreSlim _requestGate = new SemaphoreSlim(1, 1);
        private long _exchangeSequence;

        public GoogleAiStudioChatModelClient(GoogleAiStudioChatModelOptions options)
            : this(options, null)
        {
        }

        public GoogleAiStudioChatModelClient(GoogleAiStudioChatModelOptions options, HttpClient? httpClient)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(_options.ApiKey)) throw new ArgumentException("Google AI Studio API key is required.", nameof(options));
            if (string.IsNullOrWhiteSpace(_options.Model)) throw new ArgumentException("Google AI Studio model is required.", nameof(options));

            _httpClient = httpClient ?? new HttpClient();
            _ownsClient = httpClient == null;
            _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds));
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };

            if (_options.LogExchangesToDisk)
            {
                _exchangeFileLogger = new LlmExchangeFileLogger(new LocalChatModelOptions
                {
                    LogExchangesToDisk = true,
                    ExchangeLogDirectory = _options.ExchangeLogDirectory,
                    ExchangeLogMaxTextChars = _options.ExchangeLogMaxTextChars
                });
            }
        }

        public event EventHandler<LocalChatModelExchangeEventArgs>? ExchangeLogged;

        public Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            return CompleteJsonAsync(messages, StarResponseJsonSchema.CreateResponseFormat(), null, "full-star-response", cancellationToken);
        }

        public async Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, object? responseFormat, int? maxTokens, string debugLabel, CancellationToken cancellationToken)
        {
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string exchangeId = LlmExchangeFileLogger.CreateExchangeId(string.IsNullOrWhiteSpace(debugLabel) ? "google-json" : debugLabel);
                JsonNode? requestedSchema = TryExtractOpenAiJsonSchema(responseFormat);

                // Prefer a real JSON schema. Gemma deployments can differ in which structured-output
                // features are enabled, so if Google rejects the schema we retry the same prompt in
                // application/json mode rather than falling back to local AI or mock data.
                if (requestedSchema != null)
                {
                    try
                    {
                        return await SendGenerateContentAsync(messages, maxTokens, debugLabel, exchangeId, true, SanitizeSchema(requestedSchema), "json_schema", cancellationToken).ConfigureAwait(false);
                    }
                    catch (GoogleAiStudioRequestException ex) when (ex.StatusCode == 400)
                    {
                        LogExchange(
                            "Google AI Studio structured-output fallback",
                            "The hosted endpoint rejected the requested JSON schema. Retrying the same hosted request with application/json and no schema.\n" + ex.Message,
                            "fallback",
                            exchangeId,
                            debugLabel,
                            "json_object");
                    }
                }

                try
                {
                    return await SendGenerateContentAsync(messages, maxTokens, debugLabel, exchangeId, true, null, "json_object", cancellationToken).ConfigureAwait(false);
                }
                catch (GoogleAiStudioRequestException ex) when (ex.StatusCode == 400)
                {
                    // Gemma 4's hosted feature surface can differ from Gemini's structured-output
                    // surface. The review prompts already explicitly require JSON, so preserve the
                    // hosted provider and retry as ordinary text if responseMimeType itself is not
                    // accepted by this deployment.
                    LogExchange(
                        "Google AI Studio JSON-mode fallback",
                        "The hosted endpoint rejected application/json response mode. Retrying the same hosted request as ordinary generateContent; the Star review prompt still requires JSON.\n" + ex.Message,
                        "fallback",
                        exchangeId,
                        debugLabel,
                        "prompt_json");
                    return await SendGenerateContentAsync(messages, maxTokens, debugLabel, exchangeId, false, null, "prompt_json", cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _requestGate.Release();
            }
        }

        public async Task<string> CompleteTextStreamingAsync(IReadOnlyList<ChatMessage> messages, int? maxTokens, string debugLabel, Action<string>? onTextDelta, CancellationToken cancellationToken)
        {
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string exchangeId = LlmExchangeFileLogger.CreateExchangeId(string.IsNullOrWhiteSpace(debugLabel) ? "google-stream" : debugLabel);
                Dictionary<string, object> payload = BuildPayload(messages, maxTokens, wantsJson: false, schema: null, allowGoogleSearch: _options.EnableGoogleSearch);
                string json = JsonSerializer.Serialize(payload, _jsonOptions);
                string endpoint = BuildEndpoint("streamGenerateContent") + "?alt=sse";
                LogExchange("Google AI Studio streaming request before send", FormatRequest(endpoint, payload), "request", exchangeId, debugLabel, "stream_text");

                Stopwatch stopwatch = Stopwatch.StartNew();
                long? firstDeltaMs = null;
                int deltaCount = 0;
                StringBuilder finalText = new StringBuilder();

                using (HttpRequestMessage request = CreateRequest(endpoint, json))
                using (HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new GoogleAiStudioRequestException((int)response.StatusCode, "Google AI Studio streaming request failed: " + error);
                    }

                    using (System.IO.Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (System.IO.StreamReader reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                    {
                        StringBuilder eventData = new StringBuilder();
                        while (!reader.EndOfStream)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                            if (line == null) break;

                            if (line.Length == 0)
                            {
                                ProcessSseEvent(eventData, finalText, onTextDelta, stopwatch, ref firstDeltaMs, ref deltaCount);
                                eventData.Clear();
                                continue;
                            }

                            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            {
                                if (eventData.Length > 0) eventData.Append('\n');
                                eventData.Append(line.Substring(5).TrimStart());
                            }
                        }

                        if (eventData.Length > 0)
                        {
                            ProcessSseEvent(eventData, finalText, onTextDelta, stopwatch, ref firstDeltaMs, ref deltaCount);
                        }
                    }
                }

                stopwatch.Stop();
                string result = finalText.ToString();
                LogExchange(
                    "Google AI Studio streaming response",
                    "FirstDeltaMs: " + (firstDeltaMs?.ToString() ?? "none") + "\nTotalStreamMs: " + stopwatch.ElapsedMilliseconds + "\nDeltaCount: " + deltaCount + "\n\n" + result,
                    "extracted_content",
                    exchangeId,
                    debugLabel,
                    "stream_text");

                if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException("Google AI Studio streaming response contained no text.");
                return result;
            }
            finally
            {
                _requestGate.Release();
            }
        }

        private async Task<string> SendGenerateContentAsync(
            IReadOnlyList<ChatMessage> messages,
            int? maxTokens,
            string debugLabel,
            string exchangeId,
            bool wantsJson,
            JsonNode? schema,
            string responseFormatMode,
            CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = BuildPayload(messages, maxTokens, wantsJson, schema, allowGoogleSearch: !wantsJson && _options.EnableGoogleSearch);
            string json = JsonSerializer.Serialize(payload, _jsonOptions);
            string endpoint = BuildEndpoint("generateContent");
            LogExchange("Google AI Studio request before send", FormatRequest(endpoint, payload), "request", exchangeId, debugLabel, responseFormatMode);

            using (HttpRequestMessage request = CreateRequest(endpoint, json))
            using (HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                LogExchange("Google AI Studio raw HTTP response", "Status: " + (int)response.StatusCode + " " + response.ReasonPhrase + "\n\n" + body, "raw_response", exchangeId, debugLabel, responseFormatMode);
                if (!response.IsSuccessStatusCode)
                {
                    throw new GoogleAiStudioRequestException((int)response.StatusCode, "Google AI Studio request failed: " + body);
                }

                string content = ExtractCandidateText(body);
                LogExchange("Google AI Studio extracted content before Star parsing", content, "extracted_content", exchangeId, debugLabel, responseFormatMode);
                if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("Google AI Studio returned no text content.");
                return content;
            }
        }

        private Dictionary<string, object> BuildPayload(IReadOnlyList<ChatMessage> messages, int? maxTokens, bool wantsJson, JsonNode? schema, bool allowGoogleSearch)
        {
            string systemInstruction = string.Join("\n\n", (messages ?? Array.Empty<ChatMessage>())
                .Where(x => x != null && string.Equals(x.Role, "system", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Content))
                .Select(x => x.Content));

            List<Dictionary<string, object>> contents = new List<Dictionary<string, object>>();
            foreach (ChatMessage message in messages ?? Array.Empty<ChatMessage>())
            {
                if (message == null || string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase)) continue;
                string role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(message.Role, "model", StringComparison.OrdinalIgnoreCase)
                    ? "model"
                    : "user";
                string text = message.Content ?? string.Empty;

                // Merge consecutive same-role messages. This keeps Google chat history well-formed even
                // when an internal prompt builder emits adjacent user/context messages.
                if (contents.Count > 0
                    && contents[contents.Count - 1].TryGetValue("role", out object? existingRole)
                    && string.Equals(existingRole?.ToString(), role, StringComparison.Ordinal))
                {
                    List<Dictionary<string, string>> existingParts = (List<Dictionary<string, string>>)contents[contents.Count - 1]["parts"];
                    existingParts.Add(new Dictionary<string, string> { { "text", text } });
                }
                else
                {
                    contents.Add(new Dictionary<string, object>
                    {
                        { "role", role },
                        { "parts", new List<Dictionary<string, string>> { new Dictionary<string, string> { { "text", text } } } }
                    });
                }
            }

            if (contents.Count == 0)
            {
                contents.Add(new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "parts", new List<Dictionary<string, string>> { new Dictionary<string, string> { { "text", string.Empty } } } }
                });
            }

            Dictionary<string, object> generationConfig = new Dictionary<string, object>
            {
                { "temperature", _options.Temperature },
                { "thinkingConfig", new Dictionary<string, object> { { "thinkingLevel", NormalizeThinkingLevel(_options.ThinkingLevel) } } }
            };

            int resolvedMaxTokens = maxTokens.HasValue && maxTokens.Value > 0 ? maxTokens.Value : _options.MaxTokens;
            if (resolvedMaxTokens > 0) generationConfig["maxOutputTokens"] = resolvedMaxTokens;
            if (wantsJson)
            {
                generationConfig["responseMimeType"] = "application/json";
                if (schema != null) generationConfig["responseJsonSchema"] = schema;
            }

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "contents", contents },
                { "generationConfig", generationConfig }
            };

            if (!string.IsNullOrWhiteSpace(systemInstruction))
            {
                payload["systemInstruction"] = new Dictionary<string, object>
                {
                    { "parts", new List<Dictionary<string, string>> { new Dictionary<string, string> { { "text", systemInstruction } } } }
                };
            }

            if (allowGoogleSearch)
            {
                payload["tools"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { { "googleSearch", new Dictionary<string, object>() } }
                };
            }

            return payload;
        }

        private HttpRequestMessage CreateRequest(string endpoint, string json)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("x-goog-api-key", _options.ApiKey);
            return request;
        }

        private string BuildEndpoint(string method)
        {
            string baseUrl = (_options.EndpointBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (baseUrl.Length == 0) baseUrl = "https://generativelanguage.googleapis.com/v1beta";
            return baseUrl + "/models/" + Uri.EscapeDataString(_options.Model.Trim()) + ":" + method;
        }

        private static string NormalizeThinkingLevel(string? value)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return normalized == "minimal" || normalized == "off" || normalized == "none" ? "minimal" : "high";
        }

        private static JsonNode? TryExtractOpenAiJsonSchema(object? responseFormat)
        {
            if (responseFormat == null) return null;
            try
            {
                JsonNode? root = JsonNode.Parse(JsonSerializer.Serialize(responseFormat));
                if (root is not JsonObject obj) return null;
                string type = obj["type"]?.GetValue<string>() ?? string.Empty;
                if (!string.Equals(type, "json_schema", StringComparison.OrdinalIgnoreCase)) return null;
                return obj["json_schema"]?["schema"]?.DeepClone();
            }
            catch
            {
                return null;
            }
        }

        private static JsonNode? SanitizeSchema(JsonNode? node)
        {
            if (node is not JsonObject schemaObject) return node?.DeepClone();
            JsonObject result = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> property in schemaObject)
            {
                if (!SupportedSchemaKeywords.Contains(property.Key)) continue;
                JsonNode? value = property.Value;
                if (value == null)
                {
                    result[property.Key] = null;
                    continue;
                }

                if (string.Equals(property.Key, "properties", StringComparison.Ordinal)
                    || string.Equals(property.Key, "$defs", StringComparison.Ordinal))
                {
                    JsonObject mapped = new JsonObject();
                    if (value is JsonObject definitions)
                    {
                        foreach (KeyValuePair<string, JsonNode?> definition in definitions)
                        {
                            mapped[definition.Key] = SanitizeSchema(definition.Value);
                        }
                    }
                    result[property.Key] = mapped;
                }
                else if (string.Equals(property.Key, "items", StringComparison.Ordinal))
                {
                    result[property.Key] = SanitizeSchema(value);
                }
                else if (string.Equals(property.Key, "prefixItems", StringComparison.Ordinal)
                    || string.Equals(property.Key, "anyOf", StringComparison.Ordinal)
                    || string.Equals(property.Key, "oneOf", StringComparison.Ordinal))
                {
                    JsonArray array = new JsonArray();
                    if (value is JsonArray sourceArray)
                    {
                        foreach (JsonNode? item in sourceArray) array.Add(SanitizeSchema(item));
                    }
                    result[property.Key] = array;
                }
                else if (string.Equals(property.Key, "additionalProperties", StringComparison.Ordinal) && value is JsonObject)
                {
                    result[property.Key] = SanitizeSchema(value);
                }
                else
                {
                    result[property.Key] = value.DeepClone();
                }
            }
            return result;
        }

        private void ProcessSseEvent(StringBuilder eventData, StringBuilder finalText, Action<string>? onTextDelta, Stopwatch stopwatch, ref long? firstDeltaMs, ref int deltaCount)
        {
            string data = eventData.ToString().Trim();
            if (data.Length == 0 || string.Equals(data, "[DONE]", StringComparison.Ordinal)) return;
            string delta = ExtractCandidateText(data);
            if (string.IsNullOrEmpty(delta)) return;
            if (!firstDeltaMs.HasValue) firstDeltaMs = stopwatch.ElapsedMilliseconds;
            deltaCount++;
            finalText.Append(delta);
            if (onTextDelta != null)
            {
                try { onTextDelta(delta); }
                catch (Exception ex) { Debug.WriteLine("[GoogleAiStudioChatModelClient] Streaming callback failed: " + ex.Message); }
            }
        }

        private static string ExtractCandidateText(string body)
        {
            using (JsonDocument doc = JsonDocument.Parse(body))
            {
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("candidates", out JsonElement candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0) return string.Empty;
                JsonElement candidate = candidates[0];
                if (!candidate.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Object) return string.Empty;
                if (!content.TryGetProperty("parts", out JsonElement parts) || parts.ValueKind != JsonValueKind.Array) return string.Empty;
                StringBuilder text = new StringBuilder();
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object) continue;
                    if (part.TryGetProperty("thought", out JsonElement thought) && thought.ValueKind == JsonValueKind.True) continue;
                    if (part.TryGetProperty("text", out JsonElement textElement) && textElement.ValueKind == JsonValueKind.String)
                    {
                        text.Append(textElement.GetString());
                    }
                }
                return text.ToString();
            }
        }

        private string FormatRequest(string endpoint, Dictionary<string, object> payload)
        {
            return "Provider: Google AI Studio / Gemini API\nEndpoint: " + endpoint + "\nModel: " + _options.Model + "\nThinkingLevel: " + NormalizeThinkingLevel(_options.ThinkingLevel) + "\nGoogleSearch: " + _options.EnableGoogleSearch + "\n\n" + JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        }

        private void LogExchange(string title, string text, string kind, string requestId, string debugLabel, string responseFormatMode)
        {
            long sequence = Interlocked.Increment(ref _exchangeSequence);
            LocalChatModelExchangeEventArgs args = new LocalChatModelExchangeEventArgs(title, text, kind, requestId, debugLabel, responseFormatMode, sequence);
            _exchangeFileLogger?.Log(args);
            ExchangeLogged?.Invoke(this, args);
        }

        public void Dispose()
        {
            _requestGate.Dispose();
            if (_ownsClient) _httpClient.Dispose();
        }

        private sealed class GoogleAiStudioRequestException : Exception
        {
            public GoogleAiStudioRequestException(int statusCode, string message) : base(message) { StatusCode = statusCode; }
            public int StatusCode { get; }
        }
    }
}
