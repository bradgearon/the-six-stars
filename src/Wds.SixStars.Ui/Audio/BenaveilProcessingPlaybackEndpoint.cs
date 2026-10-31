using System.IO;
using System.Runtime.ExceptionServices;
using NAudio.Wave;
using Wds.SixStars.Api.Voice;

namespace Wds.SixStars.Ui.Audio;

/// <summary>Effect precedes speaker playback, virtual-microphone routing and WAV archives.</summary>
internal sealed class BenaveilProcessingPlaybackEndpoint : IWavePlaybackEndpoint, IStreamingWavePlaybackEndpoint, IAudioOutputKeepAliveEndpoint
{
    private readonly IWavePlaybackEndpoint _inner;
    private readonly IBenaveilVoiceProcessor _processor;
    public BenaveilProcessingPlaybackEndpoint(IWavePlaybackEndpoint inner, IBenaveilVoiceProcessor processor)
    { _inner = inner; _processor = processor; }
    private bool ShouldProcess(VoiceSynthesisResult audio) => _processor.Enabled
        && string.Equals(audio.StarId?.Trim(), "Benaveil", StringComparison.OrdinalIgnoreCase);

    public async Task PlayWavAsync(VoiceSynthesisResult audio, CancellationToken token)
    {
        if (!ShouldProcess(audio)) { await _inner.PlayWavAsync(audio, token).ConfigureAwait(false); return; }
        using var lease = await _processor.BeginAsync(token).ConfigureAwait(false);
        byte[] body = await lease.ProcessAsync(audio.WavAudio, token).ConfigureAwait(false);
        byte[] tail = await lease.EndAsync(token).ConfigureAwait(false);
        byte[] wav = Join(body, tail);
        if (wav.Length > 0) await _inner.PlayWavAsync(WithAudio(audio, wav), token).ConfigureAwait(false);
    }
    public IStreamingWavePlaybackSession CreateStreamingSession(string reason)
        => new Session(this, ((IStreamingWavePlaybackEndpoint)_inner).CreateStreamingSession(reason));
    public IDisposable StartOutputKeepAlive(string reason, CancellationToken token)
        => ((IAudioOutputKeepAliveEndpoint)_inner).StartOutputKeepAlive(reason, token);

    private static VoiceSynthesisResult WithAudio(VoiceSynthesisResult original, byte[] wav)
    {
        using var memory = new MemoryStream(wav, false);
        using var reader = new WaveFileReader(memory);
        return new VoiceSynthesisResult
        {
            StarId = original.StarId, VoiceId = original.VoiceId, Instructions = original.Instructions,
            GeneratedAt = original.GeneratedAt, IsStreamingPcmWindow = original.IsStreamingPcmWindow,
            WavAudio = wav, MimeType = "audio/wav", DurationMs = reader.TotalTime.TotalMilliseconds,
            Source = original.Source + "+VocalSynth2"
        };
    }

    internal static byte[] Join(byte[] body, byte[] tail)
    {
        if (body.Length == 0) return tail;
        if (tail.Length == 0) return body;
        using var first = new WaveFileReader(new MemoryStream(body, false));
        using var second = new WaveFileReader(new MemoryStream(tail, false));
        using var result = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(result), first.WaveFormat))
        {
            first.CopyTo(writer);
            second.CopyTo(writer);
        }
        return result.ToArray();
    }

    private sealed class Session : IStreamingWavePlaybackSession
    {
        private readonly BenaveilProcessingPlaybackEndpoint _owner;
        private readonly IStreamingWavePlaybackSession _inner;
        private readonly CancellationTokenSource _stop = new();
        private IBenaveilVoiceSession? _lease;
        private VoiceSynthesisResult? _last;
        private ExceptionDispatchInfo? _failure;
        private bool? _processLine;
        private bool _completed;
        public Session(BenaveilProcessingPlaybackEndpoint owner, IStreamingWavePlaybackSession inner)
        { _owner = owner; _inner = inner; }
        public async Task EnqueueAsync(VoiceSynthesisResult audio, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                _failure?.Throw();
                // Snapshot bypass for the whole line so its PCM format cannot change halfway through.
                _processLine ??= _owner.ShouldProcess(audio);
                if (!_processLine.Value) { await _inner.EnqueueAsync(audio, linked.Token).ConfigureAwait(false); return; }
                if (!string.Equals(audio.StarId, "Benaveil", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Different Stars cannot share a Benaveil VST session.");
                _lease ??= await _owner._processor.BeginAsync(linked.Token).ConfigureAwait(false);
                _last = audio;
                byte[] wav = await _lease.ProcessAsync(audio.WavAudio, linked.Token).ConfigureAwait(false);
                if (wav.Length > 0) await _inner.EnqueueAsync(WithAudio(audio, wav), linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { _failure ??= ExceptionDispatchInfo.Capture(ex); throw; }
        }
        public async Task CompleteAsync(CancellationToken token)
        {
            if (_completed) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                _failure?.Throw();
                if (_lease != null && _last != null)
                {
                    byte[] tail = await _lease.EndAsync(linked.Token).ConfigureAwait(false);
                    if (tail.Length > 0) await _inner.EnqueueAsync(WithAudio(_last, tail), linked.Token).ConfigureAwait(false);
                    _lease.Dispose(); _lease = null;
                }
                await _inner.CompleteAsync(linked.Token).ConfigureAwait(false);
                _completed = true;
            }
            catch { _inner.Cancel(); throw; }
            finally { _lease?.Dispose(); _lease = null; }
        }
        public void Cancel() { _stop.Cancel(); _inner.Cancel(); }
        public void Dispose()
        {
            _stop.Cancel();
            _lease?.Dispose(); _lease = null;
            _inner.Dispose();
            _stop.Dispose();
        }
    }
}
