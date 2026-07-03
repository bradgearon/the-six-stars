using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// WebSocket-based TTS client that maintains a persistent connection to the TTS server.
    /// Sends synthesis requests and receives audio chunks as they are generated.
    /// Supports cancellation and real-time streaming.
    /// </summary>
    public sealed class WebSocketTtsVoiceGenerator : IStreamingVoiceGenerator
    {
        private readonly WebSocketTtsOptions _options;
        private readonly JsonSerializerOptions _jsonOptions;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _connectionCts;
        private CancellationTokenSource? _generationCts;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private Task? _receiveTask;
        private bool _disposed;
        private int _currentRequestId;

        public event EventHandler<AudioChunkEventArgs>? AudioChunkReady;
        public event EventHandler<GenerationCompleteEventArgs>? GenerationComplete;
        public event EventHandler<GenerationErrorEventArgs>? GenerationError;

        public bool IsConnected => _webSocket?.State == WebSocketState.Open;

        public WebSocketTtsVoiceGenerator(WebSocketTtsOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (IsConnected)
            {
                Debug.WriteLine("[WebSocketTtsVoiceGenerator] Already connected");
                return;
            }

            if (_webSocket != null)
            {
                _webSocket.Dispose();
            }

            _webSocket = new ClientWebSocket();
            _connectionCts = new CancellationTokenSource();

            Uri wsUri = new Uri(_options.WebSocketUrl);
            Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Connecting to {wsUri}...");

            try
            {
                await _webSocket.ConnectAsync(wsUri, cancellationToken).ConfigureAwait(false);
                Debug.WriteLine("[WebSocketTtsVoiceGenerator] Connected successfully");

                // Start receiving messages
                _receiveTask = Task.Run(() => ReceiveLoopAsync(_connectionCts.Token), _connectionCts.Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Connection failed: {ex.Message}");
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                return;
            }

            Debug.WriteLine("[WebSocketTtsVoiceGenerator] Disconnecting...");
            _connectionCts?.Cancel();

            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Disconnect error: {ex.Message}");
            }

            if (_receiveTask != null)
            {
                await _receiveTask.ConfigureAwait(false);
            }

            Debug.WriteLine("[WebSocketTtsVoiceGenerator] Disconnected");
        }

        public async Task GenerateStreamingAsync(VoiceSynthesisRequest request, CancellationToken cancellationToken)
        {
            if (!IsConnected)
            {
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
            }

            _generationCts?.Cancel();
            _generationCts = new CancellationTokenSource();

            int requestId = Interlocked.Increment(ref _currentRequestId);
            string language = VoiceLanguageNormalizer.Normalize(request.VoiceSettings?.Language, _options.DefaultLanguage ?? "English");

            // Build the request payload. Language is sanitized here as a final guard because
            // local LLMs sometimes put delivery style text in voiceDirective.language.
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "type", "generate" },
                { "requestId", requestId },
                { "text", request.Text ?? string.Empty },
                { "starId", request.StarId ?? string.Empty },
                { "voiceId", request.VoiceSettings?.VoiceId ?? request.StarId ?? string.Empty },
                { "model", _options.Model ?? "Qwen/Qwen3-TTS-12Hz-1.7B-Base" },
                { "language", language },
                { "responseFormat", _options.ResponseFormat ?? "wav" },
                { "taskType", _options.TaskType ?? "VoiceClone" },
                { "speed", request.VoiceSettings?.Speed ?? 1.0 }
            };

            bool hasExplicitTextParts = request.TextParts != null && request.TextParts.Count > 0;
            if (hasExplicitTextParts)
            {
                payload["textParts"] = request.TextParts.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
            }

            if (!string.IsNullOrWhiteSpace(request.VoiceSettings?.VoiceCloneAudioFile))
            {
                payload["voiceCloneAudio"] = request.VoiceSettings.VoiceCloneAudioFile;
            }

            if (!string.IsNullOrWhiteSpace(request.VoiceSettings?.VoiceCloneRefText))
            {
                payload["voiceCloneRefText"] = request.VoiceSettings.VoiceCloneRefText;
            }

            if (request.VoiceSettings?.XVectorOnly == true)
            {
                payload["xVectorOnly"] = true;
            }

            if (!string.IsNullOrWhiteSpace(request.VoiceSettings?.VoiceDesignPrompt))
            {
                payload["voiceDesignPrompt"] = request.VoiceSettings.VoiceDesignPrompt;
            }

            if (!string.IsNullOrWhiteSpace(request.VoiceSettings?.DeliveryInstructions))
            {
                payload["deliveryInstructions"] = request.VoiceSettings.DeliveryInstructions;
            }

            if (!string.IsNullOrWhiteSpace(request.VoiceSettings?.PrimaryEmotion))
            {
                payload["primaryEmotion"] = request.VoiceSettings.PrimaryEmotion;
            }

            if (!string.IsNullOrWhiteSpace(request.VoiceSettings?.Pace))
            {
                payload["pace"] = request.VoiceSettings.Pace;
            }

            if (!string.IsNullOrWhiteSpace(request.VoiceSettings?.PitchDescription))
            {
                payload["pitchDescription"] = request.VoiceSettings.PitchDescription;
            }

            if (!string.IsNullOrWhiteSpace(request.VoiceSettings?.Volume))
            {
                payload["volume"] = request.VoiceSettings.Volume;
            }

            if (!string.IsNullOrWhiteSpace(request.VoiceSettings?.Prosody))
            {
                payload["prosody"] = request.VoiceSettings.Prosody;
            }

            // The LLM now provides the real TTS/breath chunks in textParts. When those are present,
            // do not send maxWordsPerChunk/maxSentencesPerChunk; they are fallback-only knobs for
            // legacy callers that send one plain text string.
            if (!hasExplicitTextParts)
            {
                payload["maxWordsPerChunk"] = _options.MaxWordsPerChunk;
                payload["maxSentencesPerChunk"] = _options.MaxSentencesPerChunk;
            }
            payload["chunkGenerationMode"] = _options.ChunkGenerationMode ?? "first_then_prefetch";
            payload["maxParallelChunks"] = _options.MaxParallelChunks;

            string json = JsonSerializer.Serialize(payload, _jsonOptions);
            Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Sending generation request {requestId}: {(request.Text == null ? "" : request.Text.Substring(0, Math.Min(50, request.Text.Length)))}...");

            await SendMessageAsync(json, cancellationToken).ConfigureAwait(false);
        }

        public void CancelGeneration()
        {
            if (!IsConnected)
            {
                return;
            }

            Debug.WriteLine("[WebSocketTtsVoiceGenerator] Cancelling current generation");
            _generationCts?.Cancel();

            // Send cancel message to server
            Dictionary<string, object> cancelPayload = new Dictionary<string, object>
            {
                { "type", "cancel" }
            };

            string json = JsonSerializer.Serialize(cancelPayload, _jsonOptions);
            Task.Run(async () =>
            {
                try
                {
                    await SendMessageAsync(json, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Cancel send error: {ex.Message}");
                }
            });
        }

        private async Task SendMessageAsync(string message, CancellationToken cancellationToken)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WebSocket is not connected");
            }

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(message);
                await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[1024 * 1024]; // 1MB buffer for audio chunks
            List<byte> messageBuffer = new List<byte>();

            while (!cancellationToken.IsCancellationRequested && _webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                try
                {
                    WebSocketReceiveResult result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Debug.WriteLine("[WebSocketTtsVoiceGenerator] Server closed connection");
                        break;
                    }

                    // Accumulate message bytes
                    messageBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));

                    // Process complete message
                    if (result.EndOfMessage)
                    {
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                            ProcessTextMessage(json);
                        }
                        else if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            byte[] audioData = messageBuffer.ToArray();
                            ProcessBinaryMessage(audioData);
                        }

                        messageBuffer.Clear();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Receive error: {ex.Message}");
                    GenerationError?.Invoke(this, new GenerationErrorEventArgs
                    {
                        ErrorMessage = ex.Message,
                        Exception = ex
                    });
                    break;
                }
            }

            Debug.WriteLine("[WebSocketTtsVoiceGenerator] Receive loop ended");
        }

        private void ProcessTextMessage(string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    string? messageType = root.GetProperty("type").GetString();

                    Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Received text message: {messageType}");

                    switch (messageType)
                    {
                        case "chunk_info":
                            ProcessChunkInfo(root);
                            break;

                        case "complete":
                            ProcessComplete(root);
                            break;

                        case "error":
                            ProcessError(root);
                            break;

                        case "status":
                            string? status = root.GetProperty("message").GetString();
                            Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Status: {status}");
                            break;

                        default:
                            Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Unknown message type: {messageType}");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Text message parse error: {ex.Message}");
            }
        }

        private void ProcessChunkInfo(JsonElement root)
        {
            // Chunk info is sent before binary audio data
            // We'll track this for the next binary message
            Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Chunk info received");
        }

        private void ProcessComplete(JsonElement root)
        {
            int totalChunks = root.GetProperty("totalChunks").GetInt32();
            double totalDurationMs = root.GetProperty("totalDurationMs").GetDouble();
            string? starId = root.GetProperty("starId").GetString();
            bool wasCancelled = root.TryGetProperty("cancelled", out JsonElement cancelledElem) && cancelledElem.GetBoolean();

            Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Generation complete: {totalChunks} chunks, {totalDurationMs}ms, cancelled={wasCancelled}");

            GenerationComplete?.Invoke(this, new GenerationCompleteEventArgs
            {
                TotalChunks = totalChunks,
                TotalDurationMs = totalDurationMs,
                StarId = starId ?? string.Empty,
                WasCancelled = wasCancelled
            });
        }

        private void ProcessError(JsonElement root)
        {
            string? errorMessage = root.GetProperty("message").GetString();
            string? starId = root.TryGetProperty("starId", out JsonElement starIdElem) ? starIdElem.GetString() : null;

            Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Error: {errorMessage}");

            GenerationError?.Invoke(this, new GenerationErrorEventArgs
            {
                ErrorMessage = errorMessage ?? "Unknown error",
                StarId = starId ?? string.Empty
            });
        }

        private void ProcessBinaryMessage(byte[] audioData)
        {
            // Binary messages are audio chunks
            // First 4 bytes = chunk index (int32)
            // Next 4 bytes = total chunks (int32)
            // Next 4 bytes = text length (int32)
            // Next N bytes = text (UTF8)
            // Next 4 bytes = starId length (int32)
            // Next N bytes = starId (UTF8)
            // Next 4 bytes = voiceId length (int32)
            // Next N bytes = voiceId (UTF8)
            // Remaining bytes = audio data

            try
            {
                int offset = 0;
                int chunkIndex = BitConverter.ToInt32(audioData, offset);
                offset += 4;

                int totalChunks = BitConverter.ToInt32(audioData, offset);
                offset += 4;

                int textLength = BitConverter.ToInt32(audioData, offset);
                offset += 4;

                string text = Encoding.UTF8.GetString(audioData, offset, textLength);
                offset += textLength;

                int starIdLength = BitConverter.ToInt32(audioData, offset);
                offset += 4;

                string starId = Encoding.UTF8.GetString(audioData, offset, starIdLength);
                offset += starIdLength;

                int voiceIdLength = BitConverter.ToInt32(audioData, offset);
                offset += 4;

                string voiceId = Encoding.UTF8.GetString(audioData, offset, voiceIdLength);
                offset += voiceIdLength;

                byte[] audio = new byte[audioData.Length - offset];
                Array.Copy(audioData, offset, audio, 0, audio.Length);

                Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Audio chunk {chunkIndex + 1}/{totalChunks}: {audio.Length} bytes, text='{text.Substring(0, Math.Min(30, text.Length))}...'");

                AudioChunkReady?.Invoke(this, new AudioChunkEventArgs
                {
                    ChunkIndex = chunkIndex,
                    TotalChunks = totalChunks,
                    AudioData = audio,
                    MimeType = "audio/wav",
                    Text = text,
                    StarId = starId,
                    VoiceId = voiceId
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebSocketTtsVoiceGenerator] Binary message parse error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connectionCts?.Cancel();
            _generationCts?.Cancel();

            try
            {
                DisconnectAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore dispose errors
            }

            _webSocket?.Dispose();
            _connectionCts?.Dispose();
            _generationCts?.Dispose();
            _sendLock?.Dispose();
        }
    }
}
