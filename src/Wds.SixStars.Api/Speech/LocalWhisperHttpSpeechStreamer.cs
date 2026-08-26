using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Audio;

namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// Starter HTTP adapter for a local Whisper sidecar. Different Whisper servers expose different routes,
    /// so treat this as a reference implementation. It buffers the active segment, wraps PCM16 as WAV,
    /// posts it as multipart/form-data, and uses the response body as the transcript text.
    /// </summary>
    public sealed class LocalWhisperHttpSpeechStreamer : IWhisperSpeechStreamer, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly WhisperStreamEndpointOptions _endpointOptions;
        private readonly ConcurrentDictionary<string, MemoryStream> _segments = new ConcurrentDictionary<string, MemoryStream>();
        private readonly ConcurrentDictionary<string, PcmAudioFormat> _formats = new ConcurrentDictionary<string, PcmAudioFormat>();
        private readonly ConcurrentDictionary<string, DateTimeOffset> _startedAt = new ConcurrentDictionary<string, DateTimeOffset>();
        private bool _disposeHttpClient;

        public LocalWhisperHttpSpeechStreamer(WhisperStreamEndpointOptions endpointOptions)
            : this(new HttpClient { Timeout = TimeSpan.FromSeconds(endpointOptions.TimeoutSeconds) }, endpointOptions, true)
        {
        }

        public LocalWhisperHttpSpeechStreamer(HttpClient httpClient, WhisperStreamEndpointOptions endpointOptions, bool disposeHttpClient)
        {
            _httpClient = httpClient;
            _endpointOptions = endpointOptions;
            _disposeHttpClient = disposeHttpClient;

            if (!string.IsNullOrWhiteSpace(_endpointOptions.ApiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _endpointOptions.ApiKey);
            }
        }

        public Task BeginSegmentAsync(string segmentId, PcmAudioFormat format, WhisperTranscriptionOptions options, CancellationToken cancellationToken)
        {
            _segments[segmentId] = new MemoryStream();
            _formats[segmentId] = format;
            _startedAt[segmentId] = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }

        public Task PushAudioAsync(string segmentId, byte[] pcm16Buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_segments.TryGetValue(segmentId, out MemoryStream? stream))
            {
                stream.Write(pcm16Buffer, offset, count);
            }

            return Task.CompletedTask;
        }

        public async Task<SpeechTranscript> EndSegmentAsync(string segmentId, CancellationToken cancellationToken)
        {
            _segments.TryRemove(segmentId, out MemoryStream? stream);
            _formats.TryRemove(segmentId, out PcmAudioFormat? format);
            _startedAt.TryRemove(segmentId, out DateTimeOffset startedAt);

            if (stream == null)
            {
                return new SpeechTranscript
                {
                    SegmentId = segmentId,
                    Text = string.Empty,
                    Confidence = 0,
                    Source = "local-whisper-http-empty"
                };
            }

            format = format ?? PcmAudioFormat.Mono16Khz();
            byte[] wavBytes = WaveFileUtilities.WrapPcm16InWav(stream.ToArray(), format);
            stream.Dispose();

            using (MultipartFormDataContent form = new MultipartFormDataContent())
            using (ByteArrayContent fileContent = new ByteArrayContent(wavBytes))
            {
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(fileContent, "file", "speech.wav");
                string language = string.IsNullOrWhiteSpace(_endpointOptions.Language) ? "en" : _endpointOptions.Language;
                form.Add(new StringContent(language), "language");
                bool isGroq = string.Equals(_endpointOptions.Provider, "groq", StringComparison.OrdinalIgnoreCase);
                if (isGroq)
                {
                    string model = string.IsNullOrWhiteSpace(_endpointOptions.Model) ? "whisper-large-v3-turbo" : _endpointOptions.Model.Trim();
                    form.Add(new StringContent(model), "model");
                    form.Add(new StringContent("json"), "response_format");
                }
                else
                {
                    form.Add(new StringContent("transcribe"), "task");
                }

                using (HttpResponseMessage response = await _httpClient.PostAsync(_endpointOptions.EndpointUrl, form, cancellationToken).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    string transcriptText = ExtractTranscriptText(body);
                    return new SpeechTranscript
                    {
                        SegmentId = segmentId,
                        Text = transcriptText,
                        Confidence = string.IsNullOrWhiteSpace(transcriptText) ? 0 : 0.80,
                        Language = language,
                        StartedAt = startedAt == default(DateTimeOffset) ? DateTimeOffset.UtcNow : startedAt,
                        EndedAt = DateTimeOffset.UtcNow,
                        AudioDurationMs = (double)wavBytes.Length / Math.Max(1, format.BytesPerSecond) * 1000.0,
                        Source = string.Equals(_endpointOptions.Provider, "groq", StringComparison.OrdinalIgnoreCase) ? "groq-whisper-http" : "local-whisper-http",
                        RawJson = body
                    };
                }
            }
        }

        private static string ExtractTranscriptText(string body)
        {
            string trimmed = (body ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return trimmed.Trim('\"');
            }

            try
            {
                using (JsonDocument doc = JsonDocument.Parse(trimmed))
                {
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("text", out JsonElement text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        return text.GetString()?.Trim() ?? string.Empty;
                    }
                }
            }
            catch
            {
                // Fall through and show the raw text if a nonstandard server returns invalid JSON.
            }

            return trimmed;
        }

        public Task CancelSegmentAsync(string segmentId, CancellationToken cancellationToken)
        {
            if (_segments.TryRemove(segmentId, out MemoryStream? stream))
            {
                stream.Dispose();
            }

            _formats.TryRemove(segmentId, out PcmAudioFormat _);
            _startedAt.TryRemove(segmentId, out DateTimeOffset _);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposeHttpClient)
            {
                _httpClient.Dispose();
                _disposeHttpClient = false;
            }
        }
    }
}
