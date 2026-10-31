using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Wds.SixStars.Api.Voice;

namespace Wds.SixStars.Ui.Audio;

/// <summary>Per-operation routing; AsyncLocal follows the streaming pipeline's playback task
/// without accidentally capturing unrelated conversation or mobile speech.</summary>
public sealed class SpeakAsStarPlaybackEndpoint : IWavePlaybackEndpoint, IStreamingWavePlaybackEndpoint, IAudioOutputKeepAliveEndpoint
{
    private readonly IWavePlaybackEndpoint _inner;
    private readonly AsyncLocal<OutputScope?> _scope = new();

    public SpeakAsStarPlaybackEndpoint(IWavePlaybackEndpoint inner) => _inner = inner;

    public OutputScope Begin(string star, string text, bool microphone, bool authoring,
        string deviceId, CancellationToken token)
    {
        var scope = new OutputScope(this, _scope.Value, star, text, microphone, authoring, deviceId, token);
        _scope.Value = scope;
        return scope;
    }

    public async Task PlayWavAsync(VoiceSynthesisResult audio, CancellationToken token)
    {
        OutputScope? scope = _scope.Value;
        try
        {
            scope?.Accept(audio, token);
            await _inner.PlayWavAsync(audio, token).ConfigureAwait(false);
            if (scope != null) await scope.DrainAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { scope?.Fail(ex); throw; }
    }

    public IStreamingWavePlaybackSession CreateStreamingSession(string reason)
        => new Session(((IStreamingWavePlaybackEndpoint)_inner).CreateStreamingSession(reason), _scope.Value);

    public IDisposable StartOutputKeepAlive(string reason, CancellationToken token)
        => (_inner as IAudioOutputKeepAliveEndpoint)?.StartOutputKeepAlive(reason, token) ?? new EmptyLease();

    private sealed class EmptyLease : IDisposable { public void Dispose() { } }

    private sealed class Session : IStreamingWavePlaybackSession
    {
        private readonly IStreamingWavePlaybackSession _inner;
        private readonly OutputScope? _scope;
        public Session(IStreamingWavePlaybackSession inner, OutputScope? scope) { _inner = inner; _scope = scope; }
        public async Task EnqueueAsync(VoiceSynthesisResult audio, CancellationToken token)
        {
            try
            {
                _scope?.Accept(audio, token);
                await _inner.EnqueueAsync(audio, token).ConfigureAwait(false);
            }
            catch (Exception ex) { _scope?.Fail(ex); throw; }
        }
        public async Task CompleteAsync(CancellationToken token)
        {
            try
            {
                await _inner.CompleteAsync(token).ConfigureAwait(false);
                if (_scope != null) await _scope.DrainAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { _scope?.Fail(ex); throw; }
        }
        public void Cancel() { _scope?.Cancel(); _inner.Cancel(); }
        public void Dispose() => _inner.Dispose();
    }

    public sealed class OutputScope : IDisposable
    {
        private readonly SpeakAsStarPlaybackEndpoint _owner;
        private readonly OutputScope? _previous;
        private readonly CancellationTokenSource _stop;
        private readonly string? _renderId;
        private readonly string? _directory;
        private readonly string _stem;
        private readonly object _gate = new();
        private Task _mirror = Task.CompletedTask;
        private WaveFileWriter? _writer;
        private string? _temporary;
        private bool _disposed;
        private Exception? _failure;

        internal OutputScope(SpeakAsStarPlaybackEndpoint owner, OutputScope? previous,
            string star, string text, bool microphone, bool authoring, string deviceId, CancellationToken token)
        {
            _owner = owner;
            _previous = previous;
            // Resolve before allocating resources so unsupported hardware fails clearly.
            _renderId = microphone ? ResolveVirtualRenderEndpoint(deviceId) : null;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _directory = authoring ? Path.Combine(AppContext.BaseDirectory, "Authoring", SafeName(star, "UnknownStar", 48)) : null;
            _stem = SafeName(text, "speech", 100);
        }

        internal void Accept(VoiceSynthesisResult audio, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (audio.WavAudio == null || audio.WavAudio.Length == 0) return;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _stop.Token.ThrowIfCancellationRequested();
                if (_directory != null)
                {
                    Directory.CreateDirectory(_directory);
                    using var memory = new MemoryStream(audio.WavAudio, writable: false);
                    using var reader = new WaveFileReader(memory);
                    if (_writer == null)
                    {
                        _temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".partial");
                        // Retain the effected voice's sample rate and stereo image in authored takes.
                        WaveFormat format = new WaveFormat(reader.WaveFormat.SampleRate, 16, reader.WaveFormat.Channels);
                        _writer = new WaveFileWriter(_temporary, format);
                    }
                    // Decode each chunk, then concatenate PCM under ONE WAV header.
                    using var converted = new MediaFoundationResampler(reader, _writer.WaveFormat) { ResamplerQuality = 60 };
                    byte[] buffer = new byte[16384];
                    int read;
                    while ((read = converted.Read(buffer)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        _writer.Write(buffer, 0, read);
                    }
                }
                if (_renderId != null)
                {
                    // Keep chunk order on the virtual device while normal speaker playback continues.
                    Task previous = _mirror;
                    byte[] wav = (byte[])audio.WavAudio.Clone();
                    CancellationToken stop = _stop.Token;
                    _mirror = Task.Run(async () =>
                    {
                        await previous.ConfigureAwait(false);
                        stop.ThrowIfCancellationRequested();
                        await PlayVirtualAsync(_renderId, wav, stop, token).ConfigureAwait(false);
                    }, stop);
                }
            }
        }

        internal void Fail(Exception error)
        {
            Interlocked.CompareExchange(ref _failure, error, null);
            Cancel();
        }

        internal async Task DrainAsync()
        {
            if (_failure != null) throw new InvalidOperationException("Speak as Star output failed.", _failure);
            await _mirror.ConfigureAwait(false);
        }

        public async Task<string?> FinishAsync(CancellationToken token)
        {
            await DrainAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _stop.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_writer == null || _temporary == null || _directory == null) return null;
                _writer.Dispose();
                _writer = null;
                // Retakes never overwrite an earlier performance.
                for (int take = 1; ; take++)
                {
                    string path = Path.Combine(_directory, _stem + (take == 1 ? "" : "_" + take) + ".wav");
                    try { File.Move(_temporary, path); _temporary = null; return path; }
                    catch (IOException) when (File.Exists(path)) { }
                }
            }
        }

        internal void Cancel() => _stop.Cancel();

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _stop.Cancel();
                _writer?.Dispose();
                _writer = null;
                if (_temporary != null) File.Delete(_temporary);
                _owner._scope.Value = _previous;
                // Observe failures even when normal playback threw before FinishAsync.
                _ = _mirror.ContinueWith(task =>
                {
                    if (task.IsFaulted) System.Diagnostics.Debug.WriteLine(task.Exception);
                    _stop.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        private static string SafeName(string value, string fallback, int max)
        {
            string name = Regex.Replace(value, @"[^\p{L}\p{N}]+", "_").Trim('_');
            if (name.Length == 0) name = fallback;
            name = name.Substring(0, Math.Min(max, name.Length)).TrimEnd('_');
            if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase)) name = "_" + name;
            return name;
        }

        private static string ResolveVirtualRenderEndpoint(string configuredId)
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrWhiteSpace(configuredId))
            {
                using var explicitDevice = enumerator.GetDevice(configuredId);
                if (explicitDevice.DataFlow != DataFlow.Render || explicitDevice.State != DeviceState.Active)
                    throw new InvalidOperationException("MicrophoneOutputDeviceId must identify an active virtual cable/mixer playback endpoint.");
                return explicitDevice.ID;
            }
            using var capture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            // Only recognize VB-CABLE's explicit paired endpoint names. Never guess that a
            // physical sound card's speakers feed its microphone just because they share a device.
            string target = Regex.Replace(capture.FriendlyName, @"^CABLE(?<suffix>(?:-[A-D])?) Output", "CABLE${suffix} Input", RegexOptions.IgnoreCase);
            if (!string.Equals(target, capture.FriendlyName, StringComparison.Ordinal))
            {
                string? found = null;
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    using (device)
                    {
                        if (!string.Equals(device.FriendlyName, target, StringComparison.OrdinalIgnoreCase)) continue;
                        if (found != null) throw new InvalidOperationException("Multiple virtual cable outputs match. Set VoiceOutput.MicrophoneOutputDeviceId explicitly.");
                        found = device.ID;
                    }
                }
                if (found != null) return found;
            }
            throw new InvalidOperationException("Cannot write audio to the default microphone '" + capture.FriendlyName
                + "'. Windows microphones are capture-only. Use a virtual cable as the default recording device, or set VoiceOutput.MicrophoneOutputDeviceId to your virtual mixer's playback endpoint ID.");
        }

        private static async Task PlayVirtualAsync(string id, byte[] wav, CancellationToken stop, CancellationToken chunkToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, chunkToken);
            CancellationToken token = linked.Token;
            token.ThrowIfCancellationRequested();
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(id);
            using var memory = new MemoryStream(wav, writable: false);
            using var reader = new WaveFileReader(memory);
            using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, args) =>
            {
                if (args.Exception != null) done.TrySetException(args.Exception);
                else done.TrySetResult(true);
            };
            output.Init(reader);
            output.Play();
            using var registration = token.Register(() => { output.Stop(); done.TrySetCanceled(token); });
            await done.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
    }
}
