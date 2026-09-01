using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Audio;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// Local streaming adapter for qwentts.cpp's tts-server.exe.
    ///
    /// The expensive model remains resident in the native qwentts.cpp process. This class keeps
    /// the Six Stars orchestration in .NET: it registers Star clone voices through
    /// POST /v1/audio/voices, sends each intentional speech part to POST /v1/audio/speech, and
    /// raises the existing IStreamingVoiceGenerator events directly. There is no intermediate
    /// WebSocket or native gateway process.
    ///
    /// qwentts.cpp streams raw 24 kHz mono PCM while response_format=pcm. Six Stars wraps small
    /// transport packets as WAV for compatibility, while Android joins every packet from one logical
    /// response into a single rolling AudioTrack with adaptive prebuffering.
    /// </summary>
    public sealed class QwenCppHttpStreamingVoiceGenerator : IStreamingVoiceGenerator
    {
        private readonly QwenCppHttpTtsOptions _options;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly Dictionary<string, string> _registeredVoiceFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _voiceRegistrationLock = new SemaphoreSlim(1, 1);
        private readonly object _generationGate = new object();
        private CancellationTokenSource? _generationCts;
        private bool _connected;
        private bool _disposed;
        private int _generationVersion;

        public QwenCppHttpStreamingVoiceGenerator(QwenCppHttpTtsOptions options)
            : this(options, null)
        {
        }

        public QwenCppHttpStreamingVoiceGenerator(QwenCppHttpTtsOptions options, HttpClient? httpClient)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _httpClient = httpClient ?? new HttpClient();
            _ownsClient = httpClient == null;
            _httpClient.Timeout = _options.Timeout;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
        }

        public event EventHandler<AudioChunkEventArgs>? AudioChunkReady;
        public event EventHandler<GenerationCompleteEventArgs>? GenerationComplete;
        public event EventHandler<GenerationErrorEventArgs>? GenerationError;

        public bool IsConnected => _connected && !_disposed;

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            string healthUrl = CombineUrl(_options.BaseUrl, "/health");
            Debug.WriteLine("[QwenCppHttpTTS] Checking " + healthUrl);

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, healthUrl))
            using (HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    string body = await SafeReadStringAsync(response).ConfigureAwait(false);
                    throw new InvalidOperationException("qwentts.cpp health check returned " + (int)response.StatusCode + ": " + body);
                }
            }

            _connected = true;
        }

        public Task DisconnectAsync()
        {
            _connected = false;
            return Task.CompletedTask;
        }

        public async Task GenerateStreamingAsync(VoiceSynthesisRequest request, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (!IsConnected)
            {
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
            }

            int generationVersion = Interlocked.Increment(ref _generationVersion);
            CancellationTokenSource generationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationTokenSource? previous;
            lock (_generationGate)
            {
                previous = _generationCts;
                _generationCts = generationCts;
            }
            try { previous?.Cancel(); } catch { }

            string starId = request.StarId ?? string.Empty;
            string voiceId = SanitizeVoiceName(request.VoiceSettings?.VoiceId ?? starId);
            List<string> parts = ResolveTextParts(request);
            if (parts.Count == 0)
            {
                lock (_generationGate)
                {
                    if (ReferenceEquals(_generationCts, generationCts)) _generationCts = null;
                }
                generationCts.Dispose();
                throw new InvalidOperationException("No text was supplied to qwentts.cpp.");
            }

            try
            {
                await EnsureVoiceRegisteredAsync(request, voiceId, force: false, generationCts.Token).ConfigureAwait(false);

                double totalDurationMs = 0;
                int emittedAudioChunks = 0;
                bool streamPcm = string.Equals(_options.ResponseFormat, "pcm", StringComparison.OrdinalIgnoreCase);
                for (int index = 0; index < parts.Count; index++)
                {
                    generationCts.Token.ThrowIfCancellationRequested();
                    string text = parts[index];

                    if (streamPcm)
                    {
                        Func<byte[], double, bool> emitPcmWindow = (wav, durationMs) =>
                        {
                            if (!IsCurrentGeneration(generationVersion) || generationCts.IsCancellationRequested) return false;

                            int chunkIndex = emittedAudioChunks++;
                            totalDurationMs += durationMs;
                            AudioChunkReady?.Invoke(this, new AudioChunkEventArgs
                            {
                                ChunkIndex = chunkIndex,
                                // The final number is not known until the HTTP PCM stream reaches EOS.
                                // Consumers already take the maximum seen and GenerationComplete publishes
                                // the authoritative final value.
                                TotalChunks = chunkIndex + 1,
                                AudioData = wav,
                                MimeType = "audio/wav",
                                Text = text,
                                StarId = starId,
                                VoiceId = voiceId,
                                DurationMs = durationMs,
                                IsStreamingPcmWindow = true
                            });
                            return true;
                        };

                        try
                        {
                            await SynthesizePcmStreamingAsync(request, voiceId, text, emitPcmWindow, generationCts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when ((ex is UnknownVoiceException || ex is EmptyPcmStreamException)
                            && !string.IsNullOrWhiteSpace(request.VoiceSettings?.VoiceCloneAudioFile))
                        {
                            // In chunked PCM mode tts-server may already have committed HTTP 200 before
                            // an unknown-voice backend error occurs, which surfaces to the client as an
                            // empty stream rather than an HTTP error. A native server restart can therefore
                            // invalidate its in-memory voice registry while this .NET process still has the
                            // old registration fingerprint cached. Force one registration and retry.
                            await EnsureVoiceRegisteredAsync(request, voiceId, force: true, generationCts.Token).ConfigureAwait(false);
                            await SynthesizePcmStreamingAsync(request, voiceId, text, emitPcmWindow, generationCts.Token).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        byte[] wav;
                        try
                        {
                            wav = await SynthesizeWavAsync(request, voiceId, text, generationCts.Token).ConfigureAwait(false);
                        }
                        catch (UnknownVoiceException) when (!string.IsNullOrWhiteSpace(request.VoiceSettings?.VoiceCloneAudioFile))
                        {
                            await EnsureVoiceRegisteredAsync(request, voiceId, force: true, generationCts.Token).ConfigureAwait(false);
                            wav = await SynthesizeWavAsync(request, voiceId, text, generationCts.Token).ConfigureAwait(false);
                        }

                        if (!IsCurrentGeneration(generationVersion)) return;
                        double durationMs = GetWavDurationMs(wav);
                        int chunkIndex = emittedAudioChunks++;
                        totalDurationMs += durationMs;
                        AudioChunkReady?.Invoke(this, new AudioChunkEventArgs
                        {
                            ChunkIndex = chunkIndex,
                            TotalChunks = parts.Count,
                            AudioData = wav,
                            MimeType = "audio/wav",
                            Text = text,
                            StarId = starId,
                            VoiceId = voiceId,
                            DurationMs = durationMs,
                            IsStreamingPcmWindow = false
                        });
                    }
                }

                if (IsCurrentGeneration(generationVersion))
                {
                    GenerationComplete?.Invoke(this, new GenerationCompleteEventArgs
                    {
                        TotalChunks = emittedAudioChunks,
                        TotalDurationMs = totalDurationMs,
                        StarId = starId,
                        WasCancelled = false
                    });
                }
            }
            catch (OperationCanceledException)
            {
                if (IsCurrentGeneration(generationVersion))
                {
                    GenerationComplete?.Invoke(this, new GenerationCompleteEventArgs
                    {
                        TotalChunks = parts.Count,
                        TotalDurationMs = 0,
                        StarId = starId,
                        WasCancelled = true
                    });
                }
                throw;
            }
            catch (Exception ex)
            {
                if (IsCurrentGeneration(generationVersion))
                {
                    _connected = false;
                    GenerationError?.Invoke(this, new GenerationErrorEventArgs
                    {
                        ErrorMessage = ex.Message,
                        Exception = ex,
                        StarId = starId
                    });
                }
                throw;
            }
            finally
            {
                lock (_generationGate)
                {
                    if (ReferenceEquals(_generationCts, generationCts))
                    {
                        _generationCts = null;
                    }
                }
                generationCts.Dispose();
            }
        }

        public void CancelGeneration()
        {
            CancellationTokenSource? cts;
            lock (_generationGate)
            {
                cts = _generationCts;
            }
            try { cts?.Cancel(); } catch { }
        }

        private async Task EnsureVoiceRegisteredAsync(VoiceSynthesisRequest request, string voiceId, bool force, CancellationToken cancellationToken)
        {
            string? cloneFile = request.VoiceSettings?.VoiceCloneAudioFile;
            if (string.IsNullOrWhiteSpace(cloneFile))
            {
                return;
            }

            string fullPath = Path.GetFullPath(cloneFile);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Star voice clone WAV does not exist.", fullPath);
            }

            string fingerprint = BuildVoiceFingerprint(fullPath);
            await _voiceRegistrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!force
                    && _registeredVoiceFingerprints.TryGetValue(voiceId, out string? existing)
                    && string.Equals(existing, fingerprint, StringComparison.Ordinal))
                {
                    return;
                }

                Debug.WriteLine($"[QwenCppHttpTTS] Registering cloned voice '{voiceId}' from {fullPath}");
                byte[] wav = File.ReadAllBytes(fullPath);
                Dictionary<string, object> payload = new Dictionary<string, object>
                {
                    { "name", voiceId },
                    { "wav_b64", Convert.ToBase64String(wav) }
                };

                if (request.VoiceSettings?.XVectorOnly != true
                    && !string.IsNullOrWhiteSpace(request.VoiceSettings?.VoiceCloneRefText))
                {
                    payload["ref_text"] = request.VoiceSettings!.VoiceCloneRefText;
                }

                string url = CombineUrl(_options.BaseUrl, "/v1/audio/voices");
                string json = JsonSerializer.Serialize(payload, _jsonOptions);
                using (HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using (HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            string body = await SafeReadStringAsync(response).ConfigureAwait(false);
                            throw new InvalidOperationException("qwentts.cpp voice registration returned " + (int)response.StatusCode + ": " + body);
                        }
                    }
                }

                _registeredVoiceFingerprints[voiceId] = fingerprint;
            }
            finally
            {
                _voiceRegistrationLock.Release();
            }
        }

        private async Task SynthesizePcmStreamingAsync(
            VoiceSynthesisRequest request,
            string voiceId,
            string text,
            Func<byte[], double, bool> onWavChunk,
            CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = BuildSpeechPayload(request, voiceId, text, "pcm");
            string url = CombineUrl(_options.BaseUrl, "/v1/audio/speech");
            string json = JsonSerializer.Serialize(payload, _jsonOptions);
            Stopwatch timer = Stopwatch.StartNew();
            Debug.WriteLine($"[QwenCppHttpTTS] PCM POST begin {url}; voice={voiceId}; chars={text.Length}");

            using (HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
            {
                httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorText = await SafeReadStringAsync(response).ConfigureAwait(false);
                        if (errorText.IndexOf("unknown voice", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            throw new UnknownVoiceException(errorText);
                        }
                        throw new InvalidOperationException("qwentts.cpp PCM synthesis returned " + (int)response.StatusCode + ": " + errorText);
                    }

                    Debug.WriteLine($"[QwenCppHttpTTS] PCM response headers after {timer.ElapsedMilliseconds} ms; contentType={response.Content.Headers.ContentType?.MediaType ?? "unknown"}");

                    Stream responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using (responseStream)
                    using (MemoryStream pending = new MemoryStream())
                    {
                        byte[] readBuffer = new byte[32 * 1024];
                        int packetTargetBytes = PcmBytesForMilliseconds(_options.PcmPacketMilliseconds);
                        bool firstPacket = true;
                        bool firstBytesLogged = false;
                        int totalPcmBytes = 0;
                        int emittedPackets = 0;

                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int read = await responseStream.ReadAsync(readBuffer, 0, readBuffer.Length, cancellationToken).ConfigureAwait(false);
                            if (read <= 0) break;

                            if (!firstBytesLogged)
                            {
                                firstBytesLogged = true;
                                Debug.WriteLine($"[QwenCppHttpTTS] First PCM bytes arrived after {timer.ElapsedMilliseconds} ms.");
                            }

                            pending.Position = pending.Length;
                            pending.Write(readBuffer, 0, read);
                            totalPcmBytes += read;

                            while (pending.Length >= packetTargetBytes)
                            {
                                byte[] pcm = TakePrefix(pending, packetTargetBytes);
                                byte[] wav = WaveFileUtilities.WrapPcm16InWav(pcm, Pcm24KhzMono());
                                double durationMs = PcmDurationMs(pcm.Length);
                                if (!onWavChunk(wav, durationMs))
                                {
                                    throw new OperationCanceledException("qwentts.cpp PCM consumer stopped the current synthesis.", cancellationToken);
                                }
                                emittedPackets++;
                                if (firstPacket)
                                {
                                    firstPacket = false;
                                    Debug.WriteLine($"[QwenCppHttpTTS] First PCM transport packet emitted after {timer.ElapsedMilliseconds} ms ({durationMs:0} ms audio).");
                                }
                            }
                        }

                        if ((pending.Length & 1) != 0)
                        {
                            throw new InvalidOperationException("qwentts.cpp PCM stream ended on an odd byte boundary; expected PCM16 audio.");
                        }
                        if (pending.Length > 0)
                        {
                            byte[] pcm = TakePrefix(pending, checked((int)pending.Length));
                            byte[] wav = WaveFileUtilities.WrapPcm16InWav(pcm, Pcm24KhzMono());
                            double durationMs = PcmDurationMs(pcm.Length);
                            if (!onWavChunk(wav, durationMs))
                            {
                                throw new OperationCanceledException("qwentts.cpp PCM consumer stopped the current synthesis.", cancellationToken);
                            }
                            emittedPackets++;
                            if (firstPacket)
                            {
                                firstPacket = false;
                                Debug.WriteLine($"[QwenCppHttpTTS] First PCM transport packet emitted at EOS after {timer.ElapsedMilliseconds} ms ({durationMs:0} ms audio).");
                            }
                        }

                        if (totalPcmBytes <= 0 || emittedPackets <= 0)
                        {
                            throw new EmptyPcmStreamException("qwentts.cpp returned an empty PCM stream; the cloned voice may need to be re-registered after a server restart.");
                        }
                        Debug.WriteLine($"[QwenCppHttpTTS] PCM stream complete after {timer.ElapsedMilliseconds} ms; audio={PcmDurationMs(totalPcmBytes):0} ms; packets={emittedPackets}.");
                    }
                }
            }
        }

        private async Task<byte[]> SynthesizeWavAsync(VoiceSynthesisRequest request, string voiceId, string text, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = BuildSpeechPayload(request, voiceId, text, "wav");
            string url = CombineUrl(_options.BaseUrl, "/v1/audio/speech");
            string json = JsonSerializer.Serialize(payload, _jsonOptions);
            Debug.WriteLine($"[QwenCppHttpTTS] WAV POST {url}; voice={voiceId}; chars={text.Length}");

            using (HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
            {
                httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorText = Encoding.UTF8.GetString(body);
                        if (errorText.IndexOf("unknown voice", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            throw new UnknownVoiceException(errorText);
                        }

                        throw new InvalidOperationException("qwentts.cpp synthesis returned " + (int)response.StatusCode + ": " + errorText);
                    }

                    if (!LooksLikeWav(body))
                    {
                        string contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
                        throw new InvalidOperationException("qwentts.cpp returned non-WAV audio for response_format=wav (Content-Type " + contentType + ").");
                    }
                    return body;
                }
            }
        }

        private Dictionary<string, object> BuildSpeechPayload(VoiceSynthesisRequest request, string voiceId, string text, string responseFormat)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "input", text },
                { "voice", voiceId },
                { "response_format", responseFormat },
                { "speed", request.VoiceSettings?.Speed ?? 1.0 }
            };

            // Base voice-clone mode uses the registered reference voice. Instructions are intended
            // for VoiceDesign/CustomVoice modes and can be invalid for a Base clone checkpoint.
            if (!string.Equals(_options.TaskType, "VoiceClone", StringComparison.OrdinalIgnoreCase))
            {
                string instructions = !string.IsNullOrWhiteSpace(request.VoiceSettings?.VoiceDesignPrompt)
                    ? request.VoiceSettings!.VoiceDesignPrompt
                    : request.VoiceSettings?.DeliveryInstructions ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(instructions))
                {
                    payload["instructions"] = instructions;
                }
            }
            return payload;
        }

        private static PcmAudioFormat Pcm24KhzMono()
        {
            return new PcmAudioFormat
            {
                SampleRateHz = 24000,
                Channels = 1,
                BitsPerSample = 16,
                Encoding = "pcm_s16le"
            };
        }

        private static int PcmBytesForMilliseconds(int milliseconds)
        {
            int ms = Math.Max(80, milliseconds);
            long bytes = 24000L * 2L * ms / 1000L;
            bytes &= ~1L;
            return checked((int)Math.Max(2L, bytes));
        }

        private static double PcmDurationMs(int pcmBytes)
        {
            return pcmBytes <= 0 ? 0 : pcmBytes * 1000.0 / (24000.0 * 2.0);
        }

        private static byte[] TakePrefix(MemoryStream pending, int count)
        {
            byte[] all = pending.ToArray();
            if (count <= 0 || count > all.Length) throw new ArgumentOutOfRangeException(nameof(count));
            byte[] prefix = new byte[count];
            Buffer.BlockCopy(all, 0, prefix, 0, count);
            pending.SetLength(0);
            int remainder = all.Length - count;
            if (remainder > 0)
            {
                pending.Write(all, count, remainder);
            }
            pending.Position = pending.Length;
            return prefix;
        }

        private List<string> ResolveTextParts(VoiceSynthesisRequest request)
        {
            if (request.TextParts != null)
            {
                List<string> explicitParts = request.TextParts
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .ToList();
                if (explicitParts.Count > 0)
                {
                    return explicitParts;
                }
            }

            return SplitText(request.Text ?? string.Empty, _options.MaxWordsPerChunk, _options.MaxSentencesPerChunk);
        }

        private static List<string> SplitText(string text, int maxWords, int maxSentences)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            maxWords = Math.Max(1, maxWords);
            maxSentences = Math.Max(1, maxSentences);
            List<string> sentences = new List<string>();
            StringBuilder current = new StringBuilder();
            foreach (char c in text)
            {
                current.Append(c);
                if (c == '.' || c == '!' || c == '?' || c == '\n')
                {
                    string sentence = current.ToString().Trim();
                    if (sentence.Length > 0) sentences.Add(sentence);
                    current.Clear();
                }
            }
            string tail = current.ToString().Trim();
            if (tail.Length > 0) sentences.Add(tail);
            if (sentences.Count == 0) sentences.Add(text.Trim());

            StringBuilder chunk = new StringBuilder();
            int chunkWords = 0;
            int chunkSentences = 0;
            foreach (string sentence in sentences)
            {
                int sentenceWords = CountWords(sentence);
                bool wouldOverflow = chunk.Length > 0
                    && (chunkWords + sentenceWords > maxWords || chunkSentences >= maxSentences);
                if (wouldOverflow)
                {
                    result.Add(chunk.ToString().Trim());
                    chunk.Clear();
                    chunkWords = 0;
                    chunkSentences = 0;
                }

                if (chunk.Length > 0) chunk.Append(' ');
                chunk.Append(sentence);
                chunkWords += sentenceWords;
                chunkSentences++;
            }

            if (chunk.Length > 0) result.Add(chunk.ToString().Trim());
            return result;
        }

        private static int CountWords(string text)
        {
            return text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static string SanitizeVoiceName(string? value)
        {
            string source = string.IsNullOrWhiteSpace(value) ? "six-stars-voice" : value.Trim();
            StringBuilder result = new StringBuilder(source.Length);
            foreach (char c in source)
            {
                result.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            }
            return result.ToString();
        }

        private static string BuildVoiceFingerprint(string path)
        {
            FileInfo info = new FileInfo(path);
            return info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            return (baseUrl ?? string.Empty).TrimEnd('/') + "/" + path.TrimStart('/');
        }

        private static async Task<string> SafeReadStringAsync(HttpResponseMessage response)
        {
            try { return await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
            catch { return string.Empty; }
        }

        private static bool LooksLikeWav(byte[] bytes)
        {
            return bytes.Length >= 12
                && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'A' && bytes[10] == (byte)'V' && bytes[11] == (byte)'E';
        }

        private static double GetWavDurationMs(byte[] wav)
        {
            try
            {
                int offset = 12;
                int byteRate = 0;
                int dataSize = 0;
                while (offset + 8 <= wav.Length)
                {
                    string chunkId = Encoding.ASCII.GetString(wav, offset, 4);
                    int chunkSize = BitConverter.ToInt32(wav, offset + 4);
                    int dataOffset = offset + 8;
                    if (chunkSize < 0 || dataOffset + chunkSize > wav.Length) break;

                    if (chunkId == "fmt " && chunkSize >= 12)
                    {
                        byteRate = BitConverter.ToInt32(wav, dataOffset + 8);
                    }
                    else if (chunkId == "data")
                    {
                        dataSize = chunkSize;
                        break;
                    }

                    offset = dataOffset + chunkSize + (chunkSize & 1);
                }

                if (byteRate > 0 && dataSize >= 0)
                {
                    return dataSize * 1000.0 / byteRate;
                }
            }
            catch
            {
                // Duration is diagnostic only; playback uses the WAV header itself.
            }
            return 0;
        }


        private bool IsCurrentGeneration(int version)
        {
            return Volatile.Read(ref _generationVersion) == version;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(QwenCppHttpStreamingVoiceGenerator));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelGeneration();
            _voiceRegistrationLock.Dispose();
            if (_ownsClient) _httpClient.Dispose();
        }

        private sealed class UnknownVoiceException : Exception
        {
            public UnknownVoiceException(string message) : base(message) { }
        }

        private sealed class EmptyPcmStreamException : Exception
        {
            public EmptyPcmStreamException(string message) : base(message) { }
        }
    }
}
