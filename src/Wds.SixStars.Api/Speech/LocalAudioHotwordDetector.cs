using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Audio;

namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// HTTP adapter for a lightweight local audio hotword/name detector. It sends a short WAV probe window
    /// plus the configured aliases and expects either JSON shaped like HotwordDetectionResult or plain text
    /// containing a matched Star id/alias.
    /// </summary>
    public sealed class LocalAudioHotwordDetector : IAudioHotwordDetector, IDisposable
    {
        private readonly AudioHotwordEndpointOptions _options;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public LocalAudioHotwordDetector(AudioHotwordEndpointOptions options)
            : this(options, null)
        {
        }

        public LocalAudioHotwordDetector(AudioHotwordEndpointOptions options, HttpClient? httpClient)
        {
            _options = options;
            _httpClient = httpClient ?? new HttpClient();
            _ownsClient = httpClient == null;
            _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(2, options.TimeoutSeconds));
        }

        public async Task<HotwordDetectionResult> DetectAsync(AudioHotwordDetectionRequest request, CancellationToken cancellationToken)
        {
            byte[] wav = WaveFileUtilities.WrapPcm16InWav(request.ProbeSegment.Pcm16Audio, request.ProbeSegment.Format);
            using MultipartFormDataContent form = new MultipartFormDataContent();
            using ByteArrayContent fileContent = new ByteArrayContent(wav);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(fileContent, "file", "hotword-probe.wav");
            form.Add(new StringContent(request.PreferredStarId ?? string.Empty, Encoding.UTF8), "preferredStarId");
            form.Add(new StringContent(JsonSerializer.Serialize(request.WakeAliases, _jsonOptions), Encoding.UTF8, "application/json"), "wakeAliasesJson");
            form.Add(new StringContent(request.ProbeIndex.ToString(), Encoding.UTF8), "probeIndex");
            form.Add(new StringContent(request.ProbeWindowEndMs.ToString("0"), Encoding.UTF8), "probeWindowEndMs");

            using HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl);
            httpRequest.Content = form;
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new HotwordDetectionResult
                {
                    Detected = false,
                    Confidence = 0,
                    Source = "local-audio-hotword-http-error",
                    DebugText = "HTTP " + (int)response.StatusCode + ": " + body
                };
            }

            HotwordDetectionResult result = TryParseJson(body) ?? TryParsePlainText(body, request);
            result.Source = string.IsNullOrWhiteSpace(result.Source) ? "local-audio-hotword-http" : result.Source;
            return result;
        }

        private static HotwordDetectionResult? TryParseJson(string body)
        {
            try
            {
                string json = ExtractJsonObject(body);
                HotwordDetectionResult? result = JsonSerializer.Deserialize<HotwordDetectionResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static HotwordDetectionResult TryParsePlainText(string body, AudioHotwordDetectionRequest request)
        {
            string text = (body ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text) || text.Equals("none", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return HotwordDetectionResult.None("local-audio-hotword-http-text");
            }

            foreach (var pair in request.WakeAliases.WakeAliases)
            {
                if (text.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) || pair.Value.Any(alias => text.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                {
                    return new HotwordDetectionResult
                    {
                        Detected = true,
                        StarId = pair.Key,
                        MatchedAlias = text,
                        Confidence = 0.75,
                        Source = "local-audio-hotword-http-text"
                    };
                }
            }

            return new HotwordDetectionResult
            {
                Detected = true,
                StarId = text,
                MatchedAlias = text,
                Confidence = 0.65,
                Source = "local-audio-hotword-http-text"
            };
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
