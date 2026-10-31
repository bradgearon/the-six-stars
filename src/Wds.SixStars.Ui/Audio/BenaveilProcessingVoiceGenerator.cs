using System.IO;
using System.Threading.Channels;
using NAudio.Wave;
using Wds.SixStars.Api.Voice;

namespace Wds.SixStars.Ui.Audio;

/// <summary>Transforms generated WAV windows before any desktop or network consumer sees them.</summary>
internal sealed class BenaveilProcessingVoiceGenerator : IStreamingVoiceGenerator
{
    private readonly IStreamingVoiceGenerator _inner;
    private readonly IBenaveilVoiceProcessor _processor;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _active;
    public BenaveilProcessingVoiceGenerator(IStreamingVoiceGenerator inner, IBenaveilVoiceProcessor processor)
    { _inner = inner; _processor = processor; }
    public event EventHandler<AudioChunkEventArgs>? AudioChunkReady;
    public event EventHandler<GenerationCompleteEventArgs>? GenerationComplete;
    public event EventHandler<GenerationErrorEventArgs>? GenerationError;
    public bool IsConnected => _inner.IsConnected;
    public Task ConnectAsync(CancellationToken token) => _inner.ConnectAsync(token);
    public Task DisconnectAsync() => _inner.DisconnectAsync();

    public async Task GenerateStreamingAsync(VoiceSynthesisRequest request, CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate) _active = stop;
        var queue = Channel.CreateUnbounded<AudioChunkEventArgs>(new UnboundedChannelOptions { SingleReader = true });
        bool process = _processor.Enabled && string.Equals(request.StarId?.Trim(), "Benaveil", StringComparison.OrdinalIgnoreCase);
        int emitted = 0;
        double duration = 0;
        GenerationErrorEventArgs? sourceError = null;
        bool sourceCancelled = false;
        void Chunk(object? sender, AudioChunkEventArgs args)
        {
            if (stop.IsCancellationRequested) return;
            queue.Writer.TryWrite(new AudioChunkEventArgs
            {
                ChunkIndex = args.ChunkIndex, TotalChunks = args.TotalChunks, AudioData = (byte[])args.AudioData.Clone(),
                MimeType = args.MimeType, Text = args.Text, StarId = args.StarId, VoiceId = args.VoiceId,
                DurationMs = args.DurationMs, IsStreamingPcmWindow = args.IsStreamingPcmWindow
            });
        }
        void Error(object? sender, GenerationErrorEventArgs args) => sourceError = args;
        void Complete(object? sender, GenerationCompleteEventArgs args) => sourceCancelled = args.WasCancelled;
        _inner.AudioChunkReady += Chunk;
        _inner.GenerationError += Error;
        _inner.GenerationComplete += Complete;

        async Task ConsumeAsync()
        {
            IBenaveilVoiceSession? lease = null;
            AudioChunkEventArgs? last = null;
            var pending = new SortedDictionary<int, AudioChunkEventArgs>();
            int next = 0;
            try
            {
                await foreach (var incoming in queue.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    if (incoming.ChunkIndex < next || !pending.TryAdd(incoming.ChunkIndex, incoming))
                        throw new InvalidDataException("Duplicate generated audio chunk index.");
                    while (pending.Remove(next, out var chunk))
                    {
                        next++;
                        last = chunk;
                        if (!process)
                        {
                            emitted++;
                            duration += chunk.DurationMs;
                            AudioChunkReady?.Invoke(this, chunk);
                            continue;
                        }
                        lease ??= await _processor.BeginAsync(stop.Token).ConfigureAwait(false);
                        byte[] wav = await lease.ProcessAsync(chunk.AudioData, stop.Token).ConfigureAwait(false);
                        Emit(wav, chunk);
                    }
                }
                if (pending.Count != 0) throw new InvalidDataException("Generated audio ended with a missing chunk.");
                if (lease != null && last != null)
                    Emit(await lease.EndAsync(stop.Token).ConfigureAwait(false), last);
            }
            finally { lease?.Dispose(); }
        }
        void Emit(byte[] wav, AudioChunkEventArgs original)
        {
            if (wav.Length == 0) return; // plug-in latency may consume all of an early input window
            stop.Token.ThrowIfCancellationRequested();
            using var reader = new WaveFileReader(new MemoryStream(wav, false));
            double milliseconds = reader.TotalTime.TotalMilliseconds;
            int index = emitted++;
            duration += milliseconds;
            AudioChunkReady?.Invoke(this, new AudioChunkEventArgs
            {
                AudioData = wav, ChunkIndex = index, TotalChunks = emitted, MimeType = "audio/wav",
                Text = original.Text, StarId = original.StarId, VoiceId = original.VoiceId,
                DurationMs = milliseconds, IsStreamingPcmWindow = true
            });
        }

        Task consumer = Task.Run(ConsumeAsync, CancellationToken.None);
        Task? producer = null;
        try
        {
            producer = _inner.GenerateStreamingAsync(request, stop.Token);
            Task first = await Task.WhenAny(producer, consumer).ConfigureAwait(false);
            if (first == consumer) await consumer.ConfigureAwait(false); // surface VST errors immediately
            await producer.ConfigureAwait(false);
            if (sourceError != null) throw sourceError.Exception ?? new InvalidOperationException(sourceError.ErrorMessage);
            if (sourceCancelled) throw new OperationCanceledException(stop.Token);
            queue.Writer.TryComplete();
            await consumer.ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            GenerationComplete?.Invoke(this, new GenerationCompleteEventArgs
            { StarId = request.StarId ?? string.Empty, TotalChunks = emitted, TotalDurationMs = duration });
        }
        catch (Exception ex)
        {
            stop.Cancel();
            _inner.CancelGeneration();
            queue.Writer.TryComplete(ex);
            if (ex is OperationCanceledException)
                GenerationComplete?.Invoke(this, new GenerationCompleteEventArgs { StarId = request.StarId ?? string.Empty, TotalChunks = emitted, WasCancelled = true });
            else
                GenerationError?.Invoke(this, new GenerationErrorEventArgs { StarId = request.StarId ?? string.Empty, ErrorMessage = ex.Message, Exception = ex });
            throw;
        }
        finally
        {
            stop.Cancel();
            queue.Writer.TryComplete();
            try { if (producer != null) await producer.ConfigureAwait(false); } catch { }
            try { await consumer.ConfigureAwait(false); } catch { }
            _inner.AudioChunkReady -= Chunk;
            _inner.GenerationError -= Error;
            _inner.GenerationComplete -= Complete;
            lock (_gate) _active = null;
            _requests.Release();
        }
    }
    public void CancelGeneration()
    {
        lock (_gate) _active?.Cancel();
        _inner.CancelGeneration();
    }
    public void Dispose() { CancelGeneration(); _inner.Dispose(); }
}

internal sealed class BenaveilProcessingStandardVoiceGenerator : IStarVoiceGenerator, IDisposable
{
    private readonly IStarVoiceGenerator _inner;
    private readonly IBenaveilVoiceProcessor _processor;
    public BenaveilProcessingStandardVoiceGenerator(IStarVoiceGenerator inner, IBenaveilVoiceProcessor processor)
    { _inner = inner; _processor = processor; }
    public async Task<VoiceSynthesisResult> GenerateAsync(VoiceSynthesisRequest request, CancellationToken token)
    {
        VoiceSynthesisResult result = await _inner.GenerateAsync(request, token).ConfigureAwait(false);
        if (!_processor.Enabled || !string.Equals(request.StarId?.Trim(), "Benaveil", StringComparison.OrdinalIgnoreCase)) return result;
        // Reuse the one-shot processing path with a memory sink; no playback occurs here.
        var capture = new CaptureEndpoint();
        await new BenaveilProcessingPlaybackEndpoint(capture, _processor).PlayWavAsync(result, token).ConfigureAwait(false);
        return capture.Result ?? throw new InvalidDataException("Benaveil processing produced no audio.");
    }
    public void Dispose() { if (_inner is IDisposable disposable) disposable.Dispose(); }
    private sealed class CaptureEndpoint : IWavePlaybackEndpoint
    {
        public VoiceSynthesisResult? Result;
        public Task PlayWavAsync(VoiceSynthesisResult result, CancellationToken token) { Result = result; return Task.CompletedTask; }
    }
}
