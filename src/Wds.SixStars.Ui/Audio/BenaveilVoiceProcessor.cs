using System.IO;
using System.Windows.Threading;
using NAudio.Wave;
using Wds.SixStars.Ui.Configuration;
using Wds.SixStars.VstProtocol;

namespace Wds.SixStars.Ui.Audio;

/// <summary>Serializes all native VST calls on a dedicated STA with its own message pump.</summary>
internal sealed class BenaveilVoiceProcessor : IBenaveilVoiceProcessor, IDisposable
{
    private readonly BenaveilVoiceProcessingConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly TaskCompletionSource<Dispatcher> _dispatcher = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly EffectEngine _engine = new();
    private CancellationTokenSource _enabledLifetime = new();
    private readonly List<CancellationTokenSource> _retiredLifetimes = new();
    private bool _disabled;
    private bool _disposed;
    private bool _ready; // only accessed while the operation gate is held
    public event Action<string>? StatusChanged;

    public BenaveilVoiceProcessor(BenaveilVoiceProcessingConfig config)
    {
        _config = config;
        _disabled = config.Disabled;
        var thread = new Thread(() =>
        {
            _dispatcher.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Benaveil VocalSynth" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public bool Enabled { get { lock (_stateGate) return !_disabled && !_disposed; } }

    public void ApplyDisabled(bool disabled)
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _disabled = disabled;
            if (disabled) _enabledLifetime.Cancel();
            else if (_enabledLifetime.IsCancellationRequested)
            {
                _retiredLifetimes.Add(_enabledLifetime);
                _enabledLifetime = new CancellationTokenSource();
            }
        }
        if (disabled)
        {
            StatusChanged?.Invoke("Benaveil processing disabled; using the original clone voice.");
            _ = UnloadAsync();
        }
        else _ = WarmAsync();
    }

    private CancellationToken LifetimeToken()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_disabled) throw new OperationCanceledException("Benaveil voice processing was disabled.");
            return _enabledLifetime.Token;
        }
    }

    private async Task WarmAsync()
    {
        try
        {
            CancellationToken token = LifetimeToken();
            StatusChanged?.Invoke("Loading Benaveil VocalSynth preset...");
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try { await EnsureReadyAsync(token).ConfigureAwait(false); }
            finally { _gate.Release(); }
            token.ThrowIfCancellationRequested();
            StatusChanged?.Invoke("Benaveil VocalSynth is ready.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusChanged?.Invoke("Benaveil effect could not load: " + ex.Message + " Disable processing to use the dry voice."); }
    }

    private async Task UnloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A newer enable wins over a queued unload from an earlier checkbox change.
            if (Enabled) return;
            await OnVstThreadAsync(() => { _engine.Dispose(); return true; }, CancellationToken.None).ConfigureAwait(false);
            _ready = false;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        finally { _gate.Release(); }
    }

    public async Task<IBenaveilVoiceSession> BeginAsync(CancellationToken token)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(token, LifetimeToken());
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await EnsureReadyAsync(linked.Token).ConfigureAwait(false);
                VstReply format = await SendAsync(new VstRequest { Command = "begin" }, linked.Token).ConfigureAwait(false);
                return new Lease(this, linked, format);
            }
            catch { _gate.Release(); throw; }
        }
        catch { linked.Dispose(); throw; }
    }

    private async Task EnsureReadyAsync(CancellationToken token)
    {
        if (_ready) return;
        await SendAsync(new VstRequest
        {
            Command = "load", PluginPath = ResolvePath(_config.PluginPath), PresetPath = ResolvePath(_config.PresetPath),
            SampleRate = _config.SampleRate, BlockSize = _config.BlockSize, MaxTailSeconds = _config.MaxTailSeconds
        }, token).ConfigureAwait(false);
        _ready = true;
    }

    private static string ResolvePath(string value)
    {
        string path = Environment.ExpandEnvironmentVariables(value);
        return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
    }

    private Task<VstReply> SendAsync(VstRequest request, CancellationToken token)
        => OnVstThreadAsync(() => _engine.Handle(request), token);

    private async Task<T> OnVstThreadAsync<T>(Func<T> action, CancellationToken token)
    {
        Dispatcher dispatcher = await _dispatcher.Task.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // Await native calls to finish before releasing the gate, even if cancellation occurs
        // during Process. Disposing a plug-in concurrently with Process is unsafe.
        T result = await dispatcher.InvokeAsync(action, DispatcherPriority.Normal, token).Task.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return result;
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabledLifetime.Cancel();
        }
        _ = ShutdownAsync();
    }
    private async Task ShutdownAsync()
    {
        await UnloadAsync().ConfigureAwait(false);
        Dispatcher dispatcher = await _dispatcher.Task.ConfigureAwait(false);
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        _enabledLifetime.Dispose();
        foreach (var lifetime in _retiredLifetimes) lifetime.Dispose();
    }

    internal sealed class Lease : IBenaveilVoiceSession
    {
        private readonly BenaveilVoiceProcessor _owner;
        private readonly CancellationTokenSource _lifetime;
        private readonly VstReply _format;
        private bool _disposed;
        internal Lease(BenaveilVoiceProcessor owner, CancellationTokenSource lifetime, VstReply format)
        { _owner = owner; _lifetime = lifetime; _format = format; }

        public async Task<byte[]> ProcessAsync(byte[] wav, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            return await _owner.OnVstThreadAsync(() =>
            {
                using var input = new MemoryStream(wav, false);
                using var reader = new WaveFileReader(input);
                using var resampler = new MediaFoundationResampler(reader,
                    WaveFormat.CreateIeeeFloatWaveFormat(_format.SampleRate, _format.InputChannels)) { ResamplerQuality = 60 };
                using var pcm = new MemoryStream();
                byte[] buffer = new byte[32768];
                int read;
                while ((read = resampler.Read(buffer)) > 0)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    pcm.Write(buffer, 0, read);
                }
                if (pcm.Length == 0) return Array.Empty<byte>();
                return _owner._engine.Handle(new VstRequest { Command = "chunk", Audio = pcm.ToArray() }).Audio;
            }, linked.Token).ConfigureAwait(false);
        }
        public async Task<byte[]> EndAsync(CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            return (await _owner.SendAsync(new VstRequest { Command = "end" }, linked.Token).ConfigureAwait(false)).Audio;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Dispose();
            _owner._gate.Release();
        }
    }
}
