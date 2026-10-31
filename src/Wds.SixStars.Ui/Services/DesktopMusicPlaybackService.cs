using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Wds.SixStars.Ui.Audio;

namespace Wds.SixStars.Ui.Services;

/// <summary>
/// Owns desktop music output. Uses the mobile host's renderer and the desktop speech
/// buffer's rate/jitter estimator, with a separate music profile and bounded lookahead.
/// </summary>
internal sealed class DesktopMusicPlaybackService : IDisposable
{
    private readonly DesktopSmartPcmAdaptiveProfile _profile = new(500);
    private readonly SemaphoreSlim _outputGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Func<IWavePlayer>? _outputFactory;
    private CancellationTokenSource? _active;
    private bool _disposed;

    internal DesktopMusicPlaybackService(Func<IWavePlayer>? outputFactory = null)
        => _outputFactory = outputFactory;

    public Task PlayAsync(byte[] midi, long requestStartedAtMs, CancellationToken token)
        => PlayCoreAsync(cancel => MidiPianoWaveRenderer.RenderChunks(midi, cancel), requestStartedAtMs, token);

    // Internal seam also lets checks delay production while running the real buffering controller.
    internal Task PlayChunksAsync(IEnumerable<MidiPianoWaveRenderer.PcmChunk> chunks,
        long requestStartedAtMs, CancellationToken token)
        => PlayCoreAsync(_ => chunks, requestStartedAtMs, token);

    private async Task PlayCoreAsync(Func<CancellationToken, IEnumerable<MidiPianoWaveRenderer.PcmChunk>> produce,
        long requestStartedAtMs, CancellationToken token)
    {
        CancellationTokenSource operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active?.Cancel();
            _active = operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        }
        bool acquired = false;
        try
        {
            await _outputGate.WaitAsync(operation.Token).ConfigureAwait(false);
            acquired = true;
            // Native audio and synthesis both run off the WPF dispatcher. One output per song.
            await Task.Run(async () =>
            {
                using var session = new SmartDirectSoundPcmPlaybackSession(_profile, "music preview",
                    backend: "wasapi", outputFactory: _outputFactory, musicMode: true,
                    requestStartedAtMs: requestStartedAtMs);
                using var cancel = operation.Token.Register(session.Cancel);
                using var iterator = produce(operation.Token).GetEnumerator();
                while (true)
                {
                    // Target is at most 2.2s. Keep enough room for one 250ms block while
                    // allowing the consumer to start before the rest of the MIDI is rendered.
                    await session.WaitForBufferBelowAsync(2500, operation.Token).ConfigureAwait(false);
                    operation.Token.ThrowIfCancellationRequested();
                    if (!iterator.MoveNext()) break;
                    var chunk = iterator.Current;
                    if (chunk.SampleRate != 24000 || chunk.Channels != 1 || chunk.Pcm.Length > 12000)
                        throw new InvalidOperationException("Unexpected music PCM block format or size.");
                    await session.EnqueuePcmAsync(chunk.Pcm, new WaveFormat(chunk.SampleRate, 16, chunk.Channels),
                        operation.Token).ConfigureAwait(false);
                }
                await session.CompleteAsync(operation.Token).ConfigureAwait(false);
            }, operation.Token).ConfigureAwait(false);
        }
        finally
        {
            if (acquired) _outputGate.Release();
            lock (_gate) { if (ReferenceEquals(_active, operation)) _active = null; }
            operation.Dispose();
        }
    }

    public void Stop()
    {
        lock (_gate) _active?.Cancel();
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _active?.Cancel(); }
        // Active tasks still release the semaphore while winding down.
    }
}
