using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// Hosted Qwen3-TTS VC adapter for Alibaba Cloud Model Studio. The application already streams
    /// speech in intentional text chunks; this adapter synthesizes each chunk through the hosted
    /// HTTP API and emits it through the same IStreamingVoiceGenerator events used by local Qwen.
    /// Existing per-Star local clone audio is automatically enrolled and the returned hosted voice
    /// ID is cached for subsequent runs.
    /// </summary>
    public sealed class AlibabaQwenTtsStreamingVoiceGenerator : IStreamingVoiceGenerator
    {
        private readonly AlibabaQwenTtsOptions _options;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        private readonly SemaphoreSlim _voiceGate = new SemaphoreSlim(1, 1);
        private readonly object _generationGate = new object();
        private Dictionary<string, string>? _voiceCache;
        private CancellationTokenSource? _generationCts;
        private bool _disposed;

        public AlibabaQwenTtsStreamingVoiceGenerator(AlibabaQwenTtsOptions options)
            : this(options, null)
        {
        }

        public AlibabaQwenTtsStreamingVoiceGenerator(AlibabaQwenTtsOptions options, HttpClient? httpClient)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(_options.ApiKey)) throw new ArgumentException("Alibaba Model Studio API key is required.", nameof(options));
            _httpClient = httpClient ?? new HttpClient();
            _ownsClient = httpClient == null;
            _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds));
        }

        public event EventHandler<AudioChunkEventArgs>? AudioChunkReady;
        public event EventHandler<GenerationCompleteEventArgs>? GenerationComplete;
        public event EventHandler<GenerationErrorEventArgs>? GenerationError;

        public bool IsConnected => !_disposed && !string.IsNullOrWhiteSpace(_options.ApiKey);

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(AlibabaQwenTtsStreamingVoiceGenerator));
            if (string.IsNullOrWhiteSpace(_options.ApiKey)) throw new InvalidOperationException("Hosted Alibaba Qwen TTS is enabled but no API key is configured.");
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            CancelGeneration();
            return Task.CompletedTask;
        }

        public async Task GenerateStreamingAsync(VoiceSynthesisRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            await ConnectAsync(cancellationToken).ConfigureAwait(false);

            CancellationTokenSource ownedCts = new CancellationTokenSource();
            lock (_generationGate)
            {
                _generationCts?.Cancel();
                _generationCts?.Dispose();
                _generationCts = ownedCts;
            }

            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownedCts.Token))
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    List<string> parts = (request.TextParts ?? new List<string>())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim())
                        .ToList();
                    if (parts.Count == 0 && !string.IsNullOrWhiteSpace(request.Text)) parts.Add(request.Text.Trim());
                    if (parts.Count == 0)
                    {
                        GenerationComplete?.Invoke(this, new GenerationCompleteEventArgs { TotalChunks = 0, StarId = request.StarId });
                        return;
                    }

                    string voiceId = await ResolveVoiceIdAsync(request, linked.Token).ConfigureAwait(false);
                    string language = ResolveLanguage(request.VoiceSettings?.Language);
                    Debug.WriteLine($"[Hosted Qwen TTS] Star={request.StarId}, voice={voiceId}, parts={parts.Count}, model={_options.Model}");

                    for (int i = 0; i < parts.Count; i++)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        HostedAudio audio = await SynthesizeAsync(parts[i], voiceId, language, linked.Token).ConfigureAwait(false);
                        AudioChunkReady?.Invoke(this, new AudioChunkEventArgs
                        {
                            ChunkIndex = i,
                            TotalChunks = parts.Count,
                            AudioData = audio.Bytes,
                            MimeType = audio.MimeType,
                            Text = parts[i],
                            StarId = request.StarId ?? string.Empty,
                            VoiceId = voiceId
                        });
                    }

                    stopwatch.Stop();
                    GenerationComplete?.Invoke(this, new GenerationCompleteEventArgs
                    {
                        TotalChunks = parts.Count,
                        TotalDurationMs = stopwatch.Elapsed.TotalMilliseconds,
                        StarId = request.StarId ?? string.Empty,
                        WasCancelled = false
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Hosted Qwen TTS] Generation failed: " + ex);
                    GenerationError?.Invoke(this, new GenerationErrorEventArgs
                    {
                        ErrorMessage = ex.Message,
                        Exception = ex,
                        StarId = request.StarId ?? string.Empty
                    });
                }
                finally
                {
                    lock (_generationGate)
                    {
                        if (ReferenceEquals(_generationCts, ownedCts)) _generationCts = null;
                    }
                }
            }
        }

        public void CancelGeneration()
        {
            lock (_generationGate)
            {
                try { _generationCts?.Cancel(); }
                catch { }
            }
        }

        private async Task<string> ResolveVoiceIdAsync(VoiceSynthesisRequest request, CancellationToken cancellationToken)
        {
            string starId = string.IsNullOrWhiteSpace(request.StarId) ? request.VoiceSettings?.StarId ?? string.Empty : request.StarId;
            string? configured = FindVoice(_options.VoiceIds, starId);
            if (!string.IsNullOrWhiteSpace(configured)) return configured;

            await _voiceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Dictionary<string, string> cache = LoadVoiceCache();
                string cacheKey = BuildVoiceCacheKey(starId);
                if (cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached)) return cached;

                if (!_options.AutoCloneVoices)
                {
                    throw new InvalidOperationException("No hosted Qwen voice ID is configured for " + starId + ". Configure HostedServices:TTS:VoiceIds or enable AutoCloneVoices.");
                }

                string cloneFile = request.VoiceSettings?.VoiceCloneAudioFile ?? string.Empty;
                if (string.IsNullOrWhiteSpace(cloneFile) || !File.Exists(cloneFile))
                {
                    throw new FileNotFoundException("Hosted Qwen TTS needs the Star's existing VoiceCloneAudioFile on first use so it can create the hosted clone voice.", cloneFile);
                }

                string voice = await EnrollVoiceAsync(starId, cloneFile, cancellationToken).ConfigureAwait(false);
                cache[cacheKey] = voice;
                SaveVoiceCache(cache);
                return voice;
            }
            finally
            {
                _voiceGate.Release();
            }
        }

        private async Task<string> EnrollVoiceAsync(string starId, string audioFile, CancellationToken cancellationToken)
        {
            byte[] audioBytes = File.ReadAllBytes(audioFile);
            string dataUri = "data:" + GetAudioMimeType(audioFile) + ";base64," + Convert.ToBase64String(audioBytes);
            string preferredName = SanitizePreferredName(starId);
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "model", string.IsNullOrWhiteSpace(_options.VoiceEnrollmentModel) ? "qwen-voice-enrollment" : _options.VoiceEnrollmentModel },
                { "input", new Dictionary<string, object>
                    {
                        { "action", "create" },
                        { "target_model", _options.Model },
                        { "preferred_name", preferredName },
                        { "audio", new Dictionary<string, string> { { "data", dataUri } } }
                    }
                }
            };

            Debug.WriteLine($"[Hosted Qwen TTS] Enrolling voice for {starId} from {audioFile} using {_options.Model}.");
            string body = await PostJsonAsync(_options.VoiceEnrollmentEndpointUrl, payload, cancellationToken).ConfigureAwait(false);
            using (JsonDocument doc = JsonDocument.Parse(body))
            {
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("output", out JsonElement output)
                    && output.ValueKind == JsonValueKind.Object
                    && output.TryGetProperty("voice", out JsonElement voice)
                    && voice.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(voice.GetString()))
                {
                    return voice.GetString()!;
                }
            }

            throw new InvalidOperationException("Alibaba voice enrollment succeeded but response.output.voice was missing. Response: " + body);
        }

        private async Task<HostedAudio> SynthesizeAsync(string text, string voiceId, string language, CancellationToken cancellationToken)
        {
            Dictionary<string, object> input = new Dictionary<string, object>
            {
                { "text", text },
                { "voice", voiceId }
            };
            if (!string.IsNullOrWhiteSpace(language)) input["language_type"] = language;

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "model", _options.Model },
                { "input", input }
            };

            string body = await PostJsonAsync(_options.EndpointUrl, payload, cancellationToken).ConfigureAwait(false);
            using (JsonDocument doc = JsonDocument.Parse(body))
            {
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("output", out JsonElement output) && output.ValueKind == JsonValueKind.Object
                    && output.TryGetProperty("audio", out JsonElement audio) && audio.ValueKind == JsonValueKind.Object)
                {
                    if (audio.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(data.GetString()))
                    {
                        return new HostedAudio(Convert.FromBase64String(data.GetString()!), "audio/wav");
                    }

                    if (audio.TryGetProperty("url", out JsonElement url) && url.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(url.GetString()))
                    {
                        return await DownloadAudioAsync(url.GetString()!, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            throw new InvalidOperationException("Alibaba Qwen TTS response did not contain output.audio.url or output.audio.data. Response: " + body);
        }

        private async Task<string> PostJsonAsync(string endpoint, object payload, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("Alibaba Qwen TTS endpoint is empty.");
            string json = JsonSerializer.Serialize(payload);
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("Alibaba Qwen TTS request failed (" + (int)response.StatusCode + "): " + body);
                    }
                    return body;
                }
            }
        }

        private async Task<HostedAudio> DownloadAudioAsync(string url, CancellationToken cancellationToken)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
            using (HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                string mime = response.Content.Headers.ContentType?.MediaType ?? "audio/wav";
                return new HostedAudio(bytes, mime);
            }
        }

        private Dictionary<string, string> LoadVoiceCache()
        {
            if (_voiceCache != null) return _voiceCache;
            string path = ResolveCachePath();
            try
            {
                if (File.Exists(path))
                {
                    Dictionary<string, string>? loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                    _voiceCache = loaded ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    _voiceCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                _voiceCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            return _voiceCache;
        }

        private void SaveVoiceCache(Dictionary<string, string> cache)
        {
            string path = ResolveCachePath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(cache, _jsonOptions), new UTF8Encoding(false));
        }

        private string ResolveCachePath()
        {
            string configured = string.IsNullOrWhiteSpace(_options.VoiceCachePath) ? "state/hosted-tts-voices.json" : _options.VoiceCachePath.Trim();
            return Path.IsPathRooted(configured) ? Path.GetFullPath(configured) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
        }

        private string BuildVoiceCacheKey(string starId)
        {
            return (_options.Model ?? string.Empty).Trim() + "|" + (starId ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string? FindVoice(Dictionary<string, string>? voices, string starId)
        {
            if (voices == null) return null;
            foreach (KeyValuePair<string, string> pair in voices)
            {
                if (string.Equals(pair.Key, starId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value)) return pair.Value.Trim();
            }
            return null;
        }

        private string ResolveLanguage(string? requested)
        {
            string value = string.IsNullOrWhiteSpace(requested) || string.Equals(requested, "Auto", StringComparison.OrdinalIgnoreCase)
                ? _options.DefaultLanguage
                : requested!;
            if (string.IsNullOrWhiteSpace(value)) return "English";
            if (string.Equals(value, "en", StringComparison.OrdinalIgnoreCase)) return "English";
            return value.Trim();
        }

        private static string GetAudioMimeType(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".mp3") return "audio/mpeg";
            if (ext == ".m4a" || ext == ".mp4") return "audio/mp4";
            return "audio/wav";
        }

        private static string SanitizePreferredName(string value)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in value ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(char.ToLowerInvariant(c));
            }
            string result = sb.ToString();
            if (result.Length == 0) result = "starvoice";
            if (result.Length > 16) result = result.Substring(0, 16);
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelGeneration();
            lock (_generationGate)
            {
                _generationCts?.Dispose();
                _generationCts = null;
            }
            _voiceGate.Dispose();
            if (_ownsClient) _httpClient.Dispose();
        }

        private sealed class HostedAudio
        {
            public HostedAudio(byte[] bytes, string mimeType) { Bytes = bytes; MimeType = string.IsNullOrWhiteSpace(mimeType) ? "audio/wav" : mimeType; }
            public byte[] Bytes { get; }
            public string MimeType { get; }
        }
    }
}
