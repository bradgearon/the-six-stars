using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// HTTP adapter for Qwen3-TTS or a Qwen3-TTS sidecar/server.
    /// Defaults to an OpenAI-compatible speech endpoint because several local servers expose /v1/audio/speech.
    /// If your sidecar uses another shape, set PayloadShape = "QwenSimple" or adapt this one class.
    /// </summary>
    public sealed class Qwen3TtsVoiceGenerator : IStarVoiceGenerator, IDisposable
    {
        private readonly Qwen3TtsOptions _options;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;
        private readonly JsonSerializerOptions _jsonOptions;

        public Qwen3TtsVoiceGenerator(Qwen3TtsOptions options)
            : this(options, null)
        {
        }

        public Qwen3TtsVoiceGenerator(Qwen3TtsOptions options, HttpClient? httpClient)
        {
            _options = options;
            _httpClient = httpClient ?? new HttpClient();
            _ownsClient = httpClient == null;
            _httpClient.Timeout = options.Timeout;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
        }

        public async Task<VoiceSynthesisResult> GenerateAsync(VoiceSynthesisRequest request, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = CreatePayload(request);
            string json = JsonSerializer.Serialize(payload, _jsonOptions);

            using (HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl))
            {
                httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
                if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                {
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                }

                using (HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false))
                {
                    byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        string text = Encoding.UTF8.GetString(body);
                        throw new InvalidOperationException("Qwen3-TTS returned " + (int)response.StatusCode + ": " + text);
                    }

                    string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    byte[] wavBytes = ExtractAudioBytes(body, contentType, out string mimeType);
                    return new VoiceSynthesisResult
                    {
                        StarId = request.StarId,
                        VoiceId = request.VoiceSettings?.VoiceId ?? request.StarId,
                        Instructions = Qwen3TtsInstructionBuilder.Build(request, _options),
                        WavAudio = wavBytes,
                        MimeType = string.IsNullOrWhiteSpace(mimeType) ? "audio/wav" : mimeType,
                        DurationMs = 0,
                        Source = "qwen3-tts"
                    };
                }
            }
        }

        public void Dispose()
        {
            if (_ownsClient)
            {
                _httpClient.Dispose();
            }
        }

        private Dictionary<string, object> CreatePayload(VoiceSynthesisRequest request)
        {
            string? voiceId = request.VoiceSettings?.VoiceId;
            if (string.IsNullOrWhiteSpace(voiceId)) voiceId = request.StarId;

            string instructions = Qwen3TtsInstructionBuilder.Build(request, _options);
            string language = VoiceLanguageNormalizer.Normalize(request.VoiceSettings?.Language, _options.DefaultLanguage);

            if (string.Equals(_options.PayloadShape, "QwenSimple", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> simple = new Dictionary<string, object>
                {
                    { "inputs", request.Text ?? string.Empty },
                    { "voice", voiceId },
                    { "instructions", instructions },
                    { "language", language }
                };
                if (!string.IsNullOrWhiteSpace(_options.Model)) simple["model"] = _options.Model;
                if (!string.IsNullOrWhiteSpace(_options.TaskType)) simple["task_type"] = _options.TaskType!;
                if (!string.IsNullOrWhiteSpace(request.VoiceSettings?.VoiceCloneAudioFile)) simple["voice_clone_audio"] = request.VoiceSettings!.VoiceCloneAudioFile;
                if (!string.IsNullOrWhiteSpace(request.VoiceSettings?.VoiceCloneRefText)) simple["voice_clone_ref_text"] = request.VoiceSettings!.VoiceCloneRefText;
                if (request.VoiceSettings?.XVectorOnly == true) simple["x_vector_only"] = true;
                return simple;
            }

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "model", _options.Model },
                { "input", request.Text ?? string.Empty },
                { "voice", voiceId },
                { "instructions", instructions },
                { "response_format", _options.ResponseFormat },
                { "speed", request.VoiceSettings?.Speed ?? 1.0 }
            };

            if (!string.IsNullOrWhiteSpace(language) && !string.Equals(language, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                payload["language"] = language;
            }

            if (!string.IsNullOrWhiteSpace(_options.TaskType))
            {
                payload["task_type"] = _options.TaskType!;
            }

            if (!string.IsNullOrWhiteSpace(request.VoiceSettings?.VoiceCloneAudioFile))
            {
                payload["voice_clone_audio"] = request.VoiceSettings!.VoiceCloneAudioFile;
            }

            if (!string.IsNullOrWhiteSpace(request.VoiceSettings?.VoiceCloneRefText))
            {
                payload["voice_clone_ref_text"] = request.VoiceSettings!.VoiceCloneRefText;
            }

            if (request.VoiceSettings?.XVectorOnly == true)
            {
                payload["x_vector_only"] = true;
            }

            return payload;
        }

        private static byte[] ExtractAudioBytes(byte[] body, string contentType, out string mimeType)
        {
            mimeType = contentType;
            if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || LooksLikeWav(body))
            {
                if (string.IsNullOrWhiteSpace(mimeType)) mimeType = "audio/wav";
                return body;
            }

            string text = Encoding.UTF8.GetString(body).Trim();
            if (text.StartsWith("{", StringComparison.Ordinal))
            {
                using (JsonDocument doc = JsonDocument.Parse(text))
                {
                    JsonElement root = doc.RootElement;
                    string? base64 = TryGetString(root, "audio")
                        ?? TryGetString(root, "audio_base64")
                        ?? TryGetString(root, "wav_audio")
                        ?? TryGetNestedString(root, "data", "audio")
                        ?? TryGetNestedString(root, "data", "audio_base64")
                        ?? TryGetNestedString(root, "data", "wav_audio");

                    string? jsonMime = TryGetString(root, "mime_type") ?? TryGetNestedString(root, "data", "mime_type");
                    if (!string.IsNullOrWhiteSpace(jsonMime)) mimeType = jsonMime!;

                    if (!string.IsNullOrWhiteSpace(base64))
                    {
                        return Convert.FromBase64String(base64!);
                    }
                }
            }

            throw new InvalidOperationException("Qwen3-TTS response was not audio bytes and did not contain recognized base64 audio JSON.");
        }

        private static bool LooksLikeWav(byte[] bytes)
        {
            return bytes.Length >= 12
                && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'A' && bytes[10] == (byte)'V' && bytes[11] == (byte)'E';
        }

        private static string? TryGetString(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            return null;
        }

        private static string? TryGetNestedString(JsonElement element, string parentName, string propertyName)
        {
            if (element.TryGetProperty(parentName, out JsonElement parent) && parent.ValueKind == JsonValueKind.Object)
            {
                return TryGetString(parent, propertyName);
            }

            return null;
        }
    }
}
