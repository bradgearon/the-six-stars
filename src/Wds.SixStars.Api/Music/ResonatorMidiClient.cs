using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Music
{
    public sealed class ResonatorMidiOptions
    {
        public bool Enabled { get; set; } = true;
        public string EndpointUrl { get; set; } = "http://127.0.0.1:5000/midi";
        // Resonator v0.1 POST /midi accepts a JSON notation property.
        public string BodyMode { get; set; } = "JsonProperty"; // Text, JsonString, JsonProperty
        public string NotationProperty { get; set; } = "notation";
        public int TimeoutSeconds { get; set; } = 120;
    }
    public sealed class MusicRenderRequest : MusicRenderSpecification
    {
        public string Notation { get; set; } = "";
    }
    public class MusicRenderSpecification
    {
        public int Bars { get; set; } = 8;
        public int Tempo { get; set; } = 96;
        public string Meter { get; set; } = "4/4";
    }
    public sealed class MidiRenderNotConfiguredException : Exception
    {
        public MidiRenderNotConfiguredException() : base("MIDI rendering is not configured. Set Enabled, EndpointUrl, and BodyMode after confirming the renderer contract.") { }
    }
    public sealed class ResonatorMidiClient
    {
        private readonly HttpClient _http;
        private readonly ResonatorMidiOptions _options;
        public ResonatorMidiClient(HttpClient http, ResonatorMidiOptions options) { _http = http; _options = options; }
        public bool IsConfigured => _options.Enabled
            && Uri.TryCreate(_options.EndpointUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == "http" || uri.Scheme == "https")
            && (_options.BodyMode == "Text" || _options.BodyMode == "JsonString"
                || (_options.BodyMode == "JsonProperty" && !string.IsNullOrWhiteSpace(_options.NotationProperty)))
            && _options.TimeoutSeconds >= 1 && _options.TimeoutSeconds <= 600;
        public async Task<byte[]> RenderAsync(MusicRenderRequest request, CancellationToken token)
        {
            if (!IsConfigured) throw new MidiRenderNotConfiguredException();
            var spec = new MusicCompositionRequest { Description = "Render supplied score", Bars = request.Bars, Tempo = request.Tempo, Meter = request.Meter };
            MusicCompositionService.ValidateRequest(spec);
            var errors = ResonatorNotationValidator.Validate(request.Notation, spec);
            if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
            string body = ResonatorNotationValidator.CompleteFinalMeasure(request.Notation, spec, out _);
            if (_options.BodyMode == "JsonString") body = JsonSerializer.Serialize(body);
            else if (_options.BodyMode == "JsonProperty") body = JsonSerializer.Serialize(new Dictionary<string, string> { [_options.NotationProperty] = body });
            using var message = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl) {
                Content = new StringContent(body, Encoding.UTF8, _options.BodyMode == "Text" ? "text/plain" : "application/json")
            };
            message.Headers.Accept.ParseAdd("audio/midi");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) != 0) {
                if (output.Length + count > 16 * 1024 * 1024) throw new InvalidDataException("Renderer response exceeds 16 MiB.");
                output.Write(buffer, 0, count);
            }
            byte[] midi = output.ToArray();
            if (!response.IsSuccessStatusCode) {
                string detail = "HTTP " + (int)response.StatusCode;
                try {
                    using var json = JsonDocument.Parse(midi);
                    if (json.RootElement.TryGetProperty("error", out var error)) detail += ": " + error.GetString();
                } catch (JsonException) { }
                throw new InvalidDataException("Resonator rejected the score: " + detail.Substring(0, Math.Min(2000, detail.Length)));
            }
            if (midi.Length < 14 || Encoding.ASCII.GetString(midi, 0, 4) != "MThd")
                throw new InvalidDataException("Renderer must return binary Standard MIDI data, not JSON or a download URL.");
            return midi;
        }
    }
}
