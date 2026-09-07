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
        private readonly LlmExchangeFileLogger? _exchangeFileLogger;
        private readonly bool _ownsClient;
        private readonly SemaphoreSlim _requestGate = new SemaphoreSlim(1, 1);
        private long _exchangeSequence;

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
            _exchangeFileLogger = options.LogExchangesToDisk ? new LlmExchangeFileLogger(options) : null;
        }

        public Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            return CompleteJsonAsync(messages, StarResponseJsonSchema.CreateResponseFormat(), null, "full-star-response", cancellationToken);
        }

        public async Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, object? responseFormat, int? maxTokens, string debugLabel, CancellationToken cancellationToken)
        {
            // LM Studio/llama.cpp is a single local model service. Letting a cancelled background
            // request overlap a foreground request can leave the server finishing the abandoned
            // generation while new calls pile up behind it. Keep one HTTP generation in flight per
            // client. The background dispatcher already prevents new background work while a
            // foreground lease is active, so this gate mainly protects the handoff edge.
            System.Diagnostics.Stopwatch requestGateStopwatch = System.Diagnostics.Stopwatch.StartNew();
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            requestGateStopwatch.Stop();
            LogRequestGateWait(debugLabel, requestGateStopwatch.ElapsedMilliseconds);
            try
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
                    string exchangeId = LlmExchangeFileLogger.CreateExchangeId(debugLabel);
                    try
                    {
                        string body = await SendOnceWithOptionalReasoningControlsAsync(wireMessages, responseFormatMode, responseFormat, effectiveMaxTokens, debugLabel, exchangeId, cancellationToken).ConfigureAwait(false);
                        string content = ExtractContent(body);
                        LogExchange("LLM extracted content before Star parsing", FormatExtractedContentForDebug(responseFormatMode + " / " + debugLabel, content), "extracted_content", exchangeId, debugLabel, responseFormatMode);
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
                    catch (OperationCanceledException)
                    {
                        // Cancellation and HttpClient timeout are not response-format failures.
                        // Retrying the same request without response_format used to create a second
                        // abandoned generation and could wedge the local server.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        lastErrorBody = ex.Message;
                        System.Diagnostics.Debug.WriteLine($"[OpenAiCompatibleChatModelClient] Attempt with response_format mode '{responseFormatMode}' failed: {ex.Message}");

                        if (!LooksLikeResponseFormatProblem(ex.Message))
                        {
                            throw;
                        }
                    }
                }

                throw new InvalidOperationException("LLM endpoint failed after response_format fallbacks. Last error: " + (lastErrorBody ?? lastException?.Message ?? "unknown"), lastException);
            }
            finally
            {
                _requestGate.Release();
            }
        }

        public async Task<string> CompleteTextStreamingAsync(IReadOnlyList<ChatMessage> messages, int? maxTokens, string debugLabel, Action<string>? onTextDelta, CancellationToken cancellationToken)
        {
            System.Diagnostics.Stopwatch requestGateStopwatch = System.Diagnostics.Stopwatch.StartNew();
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            requestGateStopwatch.Stop();
            LogRequestGateWait(debugLabel, requestGateStopwatch.ElapsedMilliseconds);
            try
            {
                System.Diagnostics.Stopwatch streamStopwatch = System.Diagnostics.Stopwatch.StartNew();
                long firstDeltaMs = -1;
                int streamedDeltaCount = 0;
                List<Dictionary<string, string>> wireMessages = messages
                    .Select(x => new Dictionary<string, string>
                    {
                        { "role", x.Role },
                        { "content", x.Content }
                    })
                    .ToList();

                int configuredMaxTokens = maxTokens.HasValue ? maxTokens.Value : _options.MaxTokens;
                int? effectiveMaxTokens = configuredMaxTokens > 0 ? configuredMaxTokens : (int?)null;
                string exchangeId = LlmExchangeFileLogger.CreateExchangeId(debugLabel);

                Dictionary<string, object> payload = new Dictionary<string, object>
                {
                    { "model", _options.Model },
                    { "messages", wireMessages },
                    { "temperature", _options.Temperature },
                    { "stream", true }
                };

                if (ShouldSendReasoningControlFields())
                {
                    payload["reasoning"] = "off";
                    payload["reasoning_budget"] = Math.Max(0, _options.ReasoningBudget);
                    payload["reasoning_format"] = string.IsNullOrWhiteSpace(_options.ReasoningFormat) ? "none" : _options.ReasoningFormat;
                }

                if (effectiveMaxTokens.HasValue)
                {
                    payload["max_tokens"] = effectiveMaxTokens.Value;
                }

                string json = JsonSerializer.Serialize(payload, _jsonOptions);
                LogExchange(
                    "LLM streaming text request before send",
                    FormatRequestForDebug("stream_text / " + debugLabel, payload, null, effectiveMaxTokens),
                    "request",
                    exchangeId,
                    debugLabel,
                    "stream_text");

                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl))
                {
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                    }

                    using (HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
#if NET5_0_OR_GREATER
                            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#else
                            string errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif
                            string preview = errorBody.Substring(0, Math.Min(500, errorBody.Length));
                            throw new InvalidOperationException($"LLM streaming endpoint returned {(int)response.StatusCode} {response.StatusCode}: {preview}");
                        }

                        StringBuilder content = new StringBuilder();
                        StringBuilder nonSseBody = new StringBuilder();
                        bool sawSseData = false;

#if NET5_0_OR_GREATER
                        Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
                        Stream responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
                        using (responseStream)
                        using (StreamReader reader = new StreamReader(responseStream, Encoding.UTF8, true, 8192, leaveOpen: false))
                        {
                            while (!reader.EndOfStream)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                                if (line == null) break;

                                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith(":", StringComparison.Ordinal))
                                    {
                                        if (nonSseBody.Length > 0) nonSseBody.AppendLine();
                                        nonSseBody.Append(line);
                                    }
                                    continue;
                                }

                                sawSseData = true;
                                string data = line.Substring(5).TrimStart();
                                if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase)) break;
                                if (string.IsNullOrWhiteSpace(data)) continue;

                                string delta = ExtractStreamingContentDelta(data);
                                if (string.IsNullOrEmpty(delta)) continue;

                                if (firstDeltaMs < 0) firstDeltaMs = streamStopwatch.ElapsedMilliseconds;
                                streamedDeltaCount++;
                                content.Append(delta);
                                if (onTextDelta != null)
                                {
                                    try
                                    {
                                        onTextDelta(delta);
                                    }
                                    catch (Exception callbackError)
                                    {
                                        System.Diagnostics.Debug.WriteLine("[OpenAiCompatibleChatModelClient] Streaming text callback failed: " + callbackError.Message);
                                    }
                                }
                            }
                        }

                        if (!sawSseData && content.Length == 0 && nonSseBody.Length > 0)
                        {
                            string fallbackContent = ExtractContent(nonSseBody.ToString());
                            content.Append(fallbackContent);
                            if (!string.IsNullOrEmpty(fallbackContent) && onTextDelta != null)
                            {
                                try { onTextDelta(fallbackContent); }
                                catch (Exception callbackError)
                                {
                                    System.Diagnostics.Debug.WriteLine("[OpenAiCompatibleChatModelClient] Non-SSE streaming callback failed: " + callbackError.Message);
                                }
                            }
                        }

                        string finalContent = content.ToString();
                        streamStopwatch.Stop();
                        LogExchange(
                            "LLM streaming timing",
                            "FirstDeltaMs: " + firstDeltaMs + "\nTotalStreamMs: " + streamStopwatch.ElapsedMilliseconds + "\nDeltaCount: " + streamedDeltaCount + "\nFinalChars: " + finalContent.Length,
                            "stream_timing",
                            exchangeId,
                            debugLabel,
                            "stream_text");
                        LogExchange(
                            "LLM streamed extracted content before Star parsing",
                            FormatExtractedContentForDebug("stream_text / " + debugLabel, finalContent),
                            "extracted_content",
                            exchangeId,
                            debugLabel,
                            "stream_text");

                        if (string.IsNullOrWhiteSpace(finalContent))
                        {
                            throw new InvalidOperationException("Streaming LLM returned no text content.");
                        }

                        return finalContent;
                    }
                }
            }
            finally
            {
                _requestGate.Release();
            }
        }

        private static string ExtractStreamingContentDelta(string data)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(data))
                {
                    JsonElement root = doc.RootElement;
                    if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    {
                        return string.Empty;
                    }

                    JsonElement choice = choices[0];
                    if (choice.TryGetProperty("delta", out JsonElement delta)
                        && delta.ValueKind == JsonValueKind.Object
                        && delta.TryGetProperty("content", out JsonElement deltaContent)
                        && deltaContent.ValueKind == JsonValueKind.String)
                    {
                        return deltaContent.GetString() ?? string.Empty;
                    }

                    if (choice.TryGetProperty("message", out JsonElement message)
                        && message.ValueKind == JsonValueKind.Object
                        && message.TryGetProperty("content", out JsonElement messageContent)
                        && messageContent.ValueKind == JsonValueKind.String)
                    {
                        return messageContent.GetString() ?? string.Empty;
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore malformed/non-content streaming frames; later frames can still complete the response.
            }

            return string.Empty;
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

        private async Task<string> SendOnceWithOptionalReasoningControlsAsync(List<Dictionary<string, string>> wireMessages, string responseFormatMode, object? responseFormat, int? maxTokens, string debugLabel, string exchangeId, CancellationToken cancellationToken)
        {
            bool includeReasoningControls = ShouldSendReasoningControlFields();
            try
            {
                return await SendOnceAsync(wireMessages, responseFormatMode, responseFormat, maxTokens, debugLabel, includeReasoningControls, exchangeId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (includeReasoningControls && LooksLikeReasoningParameterProblem(ex.Message))
            {
                System.Diagnostics.Debug.WriteLine("[OpenAiCompatibleChatModelClient] Reasoning-control request fields were rejected; retrying same request without reasoning fields: " + ex.Message);
                LogExchange("LLM reasoning-control fallback", "The local endpoint rejected request-level reasoning fields, so the client retried without them. The launcher still disables reasoning through llama-server CLI args when it starts the server.\n" + ex.Message, "fallback", exchangeId, debugLabel, responseFormatMode);
                return await SendOnceAsync(wireMessages, responseFormatMode, responseFormat, maxTokens, debugLabel, false, exchangeId, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<string> SendOnceAsync(List<Dictionary<string, string>> wireMessages, string responseFormatMode, object? responseFormat, int? maxTokens, string debugLabel, bool includeReasoningControls, string exchangeId, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "model", _options.Model },
                { "messages", wireMessages },
                { "temperature", _options.Temperature }
            };

            if (includeReasoningControls)
            {
                // llama.cpp/llama-server can expose intermediate thinking as reasoning_content.
                // For the real-time foreground voice path, request final content only so the
                // model does not spend the whole max_tokens budget in reasoning_content.
                payload["reasoning"] = "off";
                payload["reasoning_budget"] = Math.Max(0, _options.ReasoningBudget);
                payload["reasoning_format"] = string.IsNullOrWhiteSpace(_options.ReasoningFormat) ? "none" : _options.ReasoningFormat;
            }

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
            LogExchange("LLM request before send", FormatRequestForDebug(responseFormatMode + " / " + debugLabel, payload, responseFormat, maxTokens), "request", exchangeId, debugLabel, responseFormatMode);

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
                    LogExchange("LLM raw HTTP response before Star parsing", FormatResponseForDebug(responseFormatMode + " / " + debugLabel, response, body), "raw_response", exchangeId, debugLabel, responseFormatMode);

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


        private void LogExchange(string title, string formattedText, string kind = "debug", string? requestId = null, string? debugLabel = null, string? responseFormatMode = null)
        {
            try
            {
                long sequence = System.Threading.Interlocked.Increment(ref _exchangeSequence);
                LocalChatModelExchangeEventArgs args = new LocalChatModelExchangeEventArgs(
                    title,
                    formattedText,
                    kind,
                    string.IsNullOrWhiteSpace(requestId) ? LlmExchangeFileLogger.CreateExchangeId(debugLabel ?? "llm") : requestId!,
                    debugLabel ?? string.Empty,
                    responseFormatMode ?? string.Empty,
                    sequence);
                _exchangeFileLogger?.Log(args);
                ExchangeLogged?.Invoke(this, args);
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

        private void LogRequestGateWait(string debugLabel, long waitMs)
        {
            if (waitMs < 25) return;
            string exchangeId = LlmExchangeFileLogger.CreateExchangeId((debugLabel ?? "llm") + "-gate-wait");
            LogExchange(
                "LLM request waited for local model gate",
                "WaitMs: " + waitMs + Environment.NewLine
                    + "DebugLabel: " + (debugLabel ?? string.Empty) + Environment.NewLine
                    + "Meaning: another local-model generation already owned the single request gate before this request could be logged/sent.",
                "gate_wait",
                exchangeId,
                debugLabel ?? string.Empty,
                "gate_wait");
        }

        private string FormatRequestForDebug(string responseFormatMode, Dictionary<string, object> payload, object? requestedResponseFormat, int? effectiveMaxTokens)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("================ LLM REQUEST ================");
            sb.AppendLine("Time: " + DateTimeOffset.Now.ToString("O"));
            sb.AppendLine("Endpoint: " + _options.EndpointUrl);
            sb.AppendLine("Model: " + _options.Model);
            sb.AppendLine("ResponseFormatMode: " + responseFormatMode);
            sb.AppendLine("ResponseFormatSent:");
            sb.AppendLine(payload.TryGetValue("response_format", out object? sentResponseFormat)
                ? PrettyJson(sentResponseFormat)
                : "not sent");
            sb.AppendLine("RequestedResponseFormatDefinition:");
            sb.AppendLine(requestedResponseFormat != null ? PrettyJson(requestedResponseFormat) : "none");
            sb.AppendLine("Temperature: " + _options.Temperature);
            sb.AppendLine("MaxTokens: " + DescribeMaxTokens(effectiveMaxTokens));
            sb.AppendLine("ReasoningControls: " + (ShouldSendReasoningControlFields() ? ("off; budget=" + Math.Max(0, _options.ReasoningBudget).ToString(System.Globalization.CultureInfo.InvariantCulture) + "; format=" + (string.IsNullOrWhiteSpace(_options.ReasoningFormat) ? "none" : _options.ReasoningFormat)) : "not sent"));
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

            sb.AppendLine();
            sb.AppendLine("Full request payload JSON:");
            sb.AppendLine(PrettyJson(payload));
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

        private bool ShouldSendReasoningControlFields()
        {
            if (!_options.DisableReasoning || !_options.SendReasoningControlFields)
            {
                return false;
            }

            // These request-level fields are intended for local OpenAI-compatible servers.
            // Avoid surprising hosted/public OpenAI-style endpoints unless explicitly enabled by local config.
            if (string.IsNullOrWhiteSpace(_options.EndpointUrl))
            {
                return false;
            }

            return _options.EndpointUrl.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0
                || _options.EndpointUrl.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeReasoningParameterProblem(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.IndexOf("reasoning", StringComparison.OrdinalIgnoreCase) >= 0
                && (message.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("unsupported", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("unrecognized", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("bad request", StringComparison.OrdinalIgnoreCase) >= 0);
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
            _requestGate.Dispose();
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

                        string? reasoningContent = ExtractReasoningContent(message);
                        if (!string.IsNullOrWhiteSpace(reasoningContent))
                        {
                            throw new InvalidOperationException(
                                "Local model returned reasoning_content but empty message.content. " +
                                "Disable reasoning for llama-server using --reasoning off --reasoning-budget 0 --reasoning-format none. " +
                                "Per-request max_tokens is intentionally optional/disabled by default in this build; do not fix this by lowering caps. " +
                                "Reasoning preview: " + Preview(reasoningContent, 360));
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

        private static string? ExtractReasoningContent(JsonElement message)
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (message.TryGetProperty("reasoning_content", out JsonElement reasoningContent))
            {
                return ElementToString(reasoningContent);
            }

            if (message.TryGetProperty("reasoning", out JsonElement reasoning))
            {
                return ElementToString(reasoning);
            }

            return null;
        }

        private static string Preview(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "...";
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
