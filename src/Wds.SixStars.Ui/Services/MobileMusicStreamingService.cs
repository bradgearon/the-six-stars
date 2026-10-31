using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Ai;
using Wds.SixStars.Api.LocalModels;
using Wds.SixStars.Api.Music;

namespace Wds.SixStars.Ui.Services;

/// <summary>Owns composition, MIDI conversion and progressive PCM delivery, independent of UI.</summary>
public sealed class MobileMusicStreamingService
{
    private readonly Func<LocalModelServiceOptions> _options;
    private readonly ResonatorMidiOptions _midiOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public MobileMusicStreamingService(Func<LocalModelServiceOptions> options, ResonatorMidiOptions midiOptions)
    { _options = options; _midiOptions = midiOptions; }

    private async Task<byte[]> GenerateMidiAsync(MusicCompositionRequest request, CancellationToken token)
    {
        MusicCompositionService.ValidateRequest(request);
        using var model = new LocalAiClient(_options());
        var composer = new MusicCompositionService(model, new InstructionLibrary(InstructionLibrary.FindDefaultContentRoot()));
        var song = await composer.ComposeAsync(request, token).ConfigureAwait(false);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return await new ResonatorMidiClient(http, _midiOptions).RenderAsync(new MusicRenderRequest {
            Notation = song.Notation, Bars = request.Bars, Tempo = request.Tempo, Meter = request.Meter
        }, token).ConfigureAwait(false);
    }

    public async Task<byte[]> RenderWavAsync(MusicCompositionRequest request, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) throw new InvalidOperationException("A music request is already running.");
        try {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            var midi = await GenerateMidiAsync(request, deadline.Token).ConfigureAwait(false);
            return MidiPianoWaveRenderer.Render(midi, deadline.Token);
        } finally { _gate.Release(); }
    }

    public async Task StreamAsync(MusicCompositionRequest request, Stream response, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) throw new InvalidOperationException("A music request is already running.");
        try {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            var midi = await GenerateMidiAsync(request, deadline.Token).ConfigureAwait(false);
            await MusicPcmStreamProtocol.WriteAsync(response, MidiPianoWaveRenderer.RenderChunks(midi, deadline.Token), deadline.Token).ConfigureAwait(false);
        } finally { _gate.Release(); }
    }
}

/// <summary>Versioned, ordered length-prefixed PCM. A zero-size frame is the only successful end.</summary>
public static class MusicPcmStreamProtocol
{
    public static async Task WriteAsync(Stream response, System.Collections.Generic.IEnumerable<MidiPianoWaveRenderer.PcmChunk> chunks, CancellationToken token)
    {
        bool started = false;
        try {
            int sequence = 0;
            foreach (var chunk in chunks) {
                if (!started) {
                    byte[] http = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/x-resone-pcm\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    // From this point an error closes the stream, never appends a second HTTP response.
                    started = true;
                    await response.WriteAsync(http, token);
                    using var metadata = new MemoryStream();
                    using (var writer = new BinaryWriter(metadata, Encoding.UTF8, true)) {
                        writer.Write(Encoding.ASCII.GetBytes("RSM1")); writer.Write(chunk.SampleRate);
                        writer.Write(chunk.Channels); writer.Write(chunk.TotalFrames);
                    }
                    await response.WriteAsync(metadata.ToArray(), token);
                }
                await WriteFrameAsync(response, sequence++, chunk.Pcm, token);
            }
            if (!started) throw new InvalidDataException("No music audio was produced.");
            await WriteFrameAsync(response, sequence, Array.Empty<byte>(), token);
        } catch when (started) {
            // Incomplete stream is an explicit failure at the receiver. Discard buffered audio.
            // Socket owner closes it after this method returns; no valid terminal frame is emitted.
        }
    }
    private static async Task WriteFrameAsync(Stream response, int sequence, byte[] pcm, CancellationToken token)
    {
        using var header = new MemoryStream();
        using (var writer = new BinaryWriter(header, Encoding.UTF8, true)) { writer.Write(sequence); writer.Write(pcm.Length); }
        await response.WriteAsync(header.ToArray(), token);
        if (pcm.Length > 0) await response.WriteAsync(pcm, token);
        await response.FlushAsync(token);
    }
}
