using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Wds.SixStars.Api.Voice;

namespace Wds.SixStars.Ui.Audio;

/// <summary>
/// Standard desktop WAV playback endpoint. Unity will provide its own endpoint later, but WPF uses NAudio.
/// Streaming Star speech uses the smart DirectSound rolling-PCM session so one DirectSound device
/// stays open for the whole response with adaptive prebuffer/refill behavior. One-shot playback
/// still honors SIX_STARS_AUDIO_BACKEND for compatibility.
/// </summary>
public sealed class NaudioWavePlaybackEndpoint : IWavePlaybackEndpoint, IAudioOutputKeepAliveEndpoint, IStreamingWavePlaybackEndpoint
{
    private readonly string? _configuredBackend;
    private readonly DesktopSmartPcmAdaptiveProfile _smartPcmProfile = new DesktopSmartPcmAdaptiveProfile();
    private readonly object _keepAliveGate = new object();
    private KeepAlivePlayback? _keepAlivePlayback;
    private int _keepAliveLeaseCount;

    public NaudioWavePlaybackEndpoint(string? configuredBackend = null)
    {
        _configuredBackend = configuredBackend;
    }

    public IStreamingWavePlaybackSession CreateStreamingSession(string reason)
    {
        // Deliberately DirectSound-only: this mirrors Android SmartPcmPlaybackWriter semantics
        // rather than reopening/alternating Windows audio backends while a response is playing.
        return new SmartDirectSoundPcmPlaybackSession(_smartPcmProfile, reason);
    }

    public IDisposable StartOutputKeepAlive(string reason, CancellationToken cancellationToken)
    {
        if (IsKeepAliveDisabled())
        {
            System.Diagnostics.Debug.WriteLine("[NaudioPlayback] Audio keep-alive disabled by SIX_STARS_AUDIO_KEEP_ALIVE=false.");
            return NoopDisposable.Instance;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return NoopDisposable.Instance;
        }

        lock (_keepAliveGate)
        {
            _keepAliveLeaseCount++;

            if (_keepAlivePlayback == null)
            {
                string backend = NormalizeBackend(
                    Environment.GetEnvironmentVariable("SIX_STARS_AUDIO_KEEPALIVE_BACKEND")
                    ?? Environment.GetEnvironmentVariable("SIX_STARS_AUDIO_KEEP_ALIVE_BACKEND")
                    ?? _configuredBackend
                    ?? Environment.GetEnvironmentVariable("SIX_STARS_AUDIO_BACKEND")
                    ?? "directsound");

                _keepAlivePlayback = TryStartKeepAlivePlayback(backend, reason);
                if (_keepAlivePlayback == null)
                {
                    System.Diagnostics.Debug.WriteLine("[NaudioPlayback] Silent audio keep-alive could not be started. Normal speech playback will continue.");
                }
            }

            return new KeepAliveLease(this);
        }
    }

    public async Task PlayWavAsync(VoiceSynthesisResult voiceAudio, CancellationToken cancellationToken)
    {
        byte[] wav = voiceAudio.WavAudio ?? Array.Empty<byte>();
        if (wav.Length == 0)
        {
            System.Diagnostics.Debug.WriteLine("[NaudioPlayback] No audio bytes to play.");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] Playing {wav.Length} bytes ({voiceAudio.MimeType ?? "audio/wav"}) for {voiceAudio.StarId}/{voiceAudio.VoiceId}.");

        try
        {
            using MemoryStream ms = new MemoryStream(wav, writable: false);
            using WaveFileReader testReader = new WaveFileReader(ms);
            System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] WAV Format: {testReader.WaveFormat.SampleRate}Hz, {testReader.WaveFormat.BitsPerSample}bit, {testReader.WaveFormat.Channels}ch, Duration: {testReader.TotalTime.TotalSeconds:0.00}s");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] WARNING: Could not read WAV header: {ex.Message}");
        }

        string backend = NormalizeBackend(_configuredBackend ?? Environment.GetEnvironmentVariable("SIX_STARS_AUDIO_BACKEND") ?? "directsound");
        System.Diagnostics.Debug.WriteLine("[NaudioPlayback] Requested backend: " + backend);

        if (backend == "wasapi")
        {
            await PlayWithFallbacksAsync(wav, voiceAudio.MimeType, cancellationToken, "wasapi", "waveout", "directsound").ConfigureAwait(false);
            return;
        }

        if (backend == "waveout")
        {
            await PlayWithFallbacksAsync(wav, voiceAudio.MimeType, cancellationToken, "waveout", "directsound").ConfigureAwait(false);
            return;
        }

        if (backend == "directsound" || backend == "auto")
        {
            // Use the exact same adaptive single-stream DirectSound path for complete WAVs and for
            // rolling qwentts PCM. This avoids maintaining two different DirectSound timing models.
            using IStreamingWavePlaybackSession smart = CreateStreamingSession("one-shot desktop voice");
            await smart.EnqueueAsync(voiceAudio, cancellationToken).ConfigureAwait(false);
            await smart.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await PlayWithFallbacksAsync(wav, voiceAudio.MimeType, cancellationToken, "directsound", "waveout").ConfigureAwait(false);
    }

    private static bool IsKeepAliveDisabled()
    {
        string value = Environment.GetEnvironmentVariable("SIX_STARS_AUDIO_KEEP_ALIVE")
            ?? Environment.GetEnvironmentVariable("SIX_STARS_AUDIO_KEEPALIVE")
            ?? "true";

        return value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("0", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("no", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBackend(string? backend)
    {
        string value = (backend ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "wasapi" => "wasapi",
            "windows wasapi" => "wasapi",
            "directsound" => "directsound",
            "direct sound" => "directsound",
            "ds" => "directsound",
            "waveout" => "waveout",
            "wave out" => "waveout",
            "waveoutevent" => "waveout",
            "auto" => "auto",
            _ => "directsound"
        };
    }

    private static async Task PlayWithFallbacksAsync(byte[] wav, string? mimeType, CancellationToken cancellationToken, params string[] backends)
    {
        Exception? lastException = null;

        foreach (string backend in backends)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] Attempting {backend} playback...");

                switch (backend)
                {
                    case "wasapi":
                        await PlayWithWasapiAsync(wav, mimeType, cancellationToken).ConfigureAwait(false);
                        break;
                    case "waveout":
                        await PlayWithWaveOutAsync(wav, mimeType, cancellationToken).ConfigureAwait(false);
                        break;
                    case "directsound":
                        await PlayWithDirectSoundAsync(wav, mimeType, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        continue;
                }

                System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] {backend} playback completed successfully.");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] {backend} playback failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        throw new InvalidOperationException("All configured NAudio playback backends failed. See debug log for backend-specific errors.", lastException);
    }

    private static async Task PlayWithWasapiAsync(byte[] wav, string? mimeType, CancellationToken cancellationToken)
    {
        using MemoryStream memoryStream = new MemoryStream(wav, writable: false);
        using WaveStream reader = CreateReader(memoryStream, mimeType);
        using MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
        using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using WasapiOut output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        await PlayInitializedOutputAsync(output, reader, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PlayWithDirectSoundAsync(byte[] wav, string? mimeType, CancellationToken cancellationToken)
    {
        // DirectSoundOut can throw "Object contains references" on some .NET 8/driver combinations
        // when it is fed a WaveFileReader directly. Decode the WAV to a stable PCM byte buffer first,
        // then feed DirectSoundOut through BufferedWaveProvider. This keeps the requested DirectSound
        // output path while avoiding the fragile reader-as-provider path.
        using MemoryStream memoryStream = new MemoryStream(wav, writable: false);
        using WaveStream reader = CreateReader(memoryStream, mimeType);
        byte[] pcm = ReadAllPcmBytes(reader);
        WaveFormat directSoundFormat = DirectSoundWaveFormatHelper.Canonicalize(reader.WaveFormat);
        BufferedWaveProvider provider = new BufferedWaveProvider(directSoundFormat, reader.TotalTime + TimeSpan.FromSeconds(1))
        {
            DiscardOnBufferOverflow = false,
            ReadFully = false
        };
        provider.AddSamples(pcm, 0, pcm.Length);

        using DirectSoundOut output = new DirectSoundOut(100);
        await PlayInitializedOutputAsync(output, provider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PlayWithWaveOutAsync(byte[] wav, string? mimeType, CancellationToken cancellationToken)
    {
        using MemoryStream memoryStream = new MemoryStream(wav, writable: false);
        using WaveStream reader = CreateReader(memoryStream, mimeType);
        using WaveOut output = new WaveOut
        {
            DeviceNumber = -1,
            BufferMilliseconds = 34,
            NumberOfBuffers = 3
        };
        await PlayInitializedOutputAsync(output, reader, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PlayInitializedOutputAsync(IWavePlayer output, IWaveProvider provider, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        output.PlaybackStopped += (_, args) =>
        {
            System.Diagnostics.Debug.WriteLine("[NaudioPlayback] PlaybackStopped event fired");
            if (args.Exception != null)
            {
                System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] PlaybackStopped with exception: {args.Exception.Message}");
                completed.TrySetException(args.Exception);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[NaudioPlayback] PlaybackStopped normally");
                completed.TrySetResult(true);
            }
        };

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[NaudioPlayback] Cancellation requested, stopping output");
                output.Stop();
            }
            catch
            {
                // Ignore cancellation stop races.
            }

            completed.TrySetCanceled(cancellationToken);
        });

        cancellationToken.ThrowIfCancellationRequested();

        System.Diagnostics.Debug.WriteLine("[NaudioPlayback] Initializing output with audio provider");
        output.Init(provider);

        System.Diagnostics.Debug.WriteLine("[NaudioPlayback] Starting playback...");
        output.Play();

        System.Diagnostics.Debug.WriteLine("[NaudioPlayback] Waiting for playback to complete...");
        await completed.Task.ConfigureAwait(false);

        System.Diagnostics.Debug.WriteLine("[NaudioPlayback] Playback task completed successfully");
    }

    private static byte[] ReadAllPcmBytes(WaveStream reader)
    {
        using MemoryStream pcm = new MemoryStream();
        byte[] buffer = new byte[Math.Max(reader.WaveFormat.AverageBytesPerSecond / 4, 4096)];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            pcm.Write(buffer, 0, read);
        }

        return pcm.ToArray();
    }

    private static WaveStream CreateReader(Stream stream, string? mimeType)
    {
        if (!string.IsNullOrWhiteSpace(mimeType) && mimeType.IndexOf("mp3", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return new Mp3FileReader(stream);
        }

        return new WaveFileReader(stream);
    }

    private static KeepAlivePlayback? TryStartKeepAlivePlayback(string backend, string reason)
    {
        string[] candidates = backend == "auto"
            ? new[] { "directsound", "wasapi", "waveout" }
            : new[] { backend };

        foreach (string candidate in candidates)
        {
            OutputPlayerBundle? bundle = null;
            try
            {
                System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] Starting silent audio keep-alive using {candidate}. Reason: {reason}");
                bundle = CreateOutputPlayerBundle(candidate, latencyMs: 100);
                SilentPcmWaveProvider silence = new SilentPcmWaveProvider(sampleRate: 48000, channels: 2);
                bundle.Player.Init(silence);
                bundle.Player.Play();
                System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] Silent audio keep-alive started with {candidate} at {silence.WaveFormat.SampleRate}Hz, {silence.WaveFormat.BitsPerSample}bit, {silence.WaveFormat.Channels}ch.");
                OutputPlayerBundle startedBundle = bundle;
                bundle = null;
                return new KeepAlivePlayback(startedBundle, candidate);
            }
            catch (Exception ex)
            {
                bundle?.Dispose();
                System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] Silent audio keep-alive failed for {candidate}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return null;
    }

    private static OutputPlayerBundle CreateOutputPlayerBundle(string backend, int latencyMs)
    {
        switch (backend)
        {
            case "directsound":
                return new OutputPlayerBundle(new DirectSoundOut(latencyMs));

            case "waveout":
                return new OutputPlayerBundle(new WaveOut
                {
                    DeviceNumber = -1,
                    BufferMilliseconds = Math.Max(1, (latencyMs + 2) / 3),
                    NumberOfBuffers = 3
                });

            case "wasapi":
                MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                try
                {
                    MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    try
                    {
                        return new OutputPlayerBundle(
                            new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs),
                            enumerator,
                            device);
                    }
                    catch
                    {
                        device.Dispose();
                        throw;
                    }
                }
                catch
                {
                    enumerator.Dispose();
                    throw;
                }

            default:
                throw new InvalidOperationException("Unsupported audio keep-alive backend: " + backend);
        }
    }

    private void ReleaseKeepAliveLease()
    {
        lock (_keepAliveGate)
        {
            if (_keepAliveLeaseCount > 0)
            {
                _keepAliveLeaseCount--;
            }

            if (_keepAliveLeaseCount == 0 && _keepAlivePlayback != null)
            {
                System.Diagnostics.Debug.WriteLine("[NaudioPlayback] Stopping silent audio keep-alive.");
                _keepAlivePlayback.Dispose();
                _keepAlivePlayback = null;
            }
        }
    }

    private sealed class KeepAliveLease : IDisposable
    {
        private NaudioWavePlaybackEndpoint? _owner;

        public KeepAliveLease(NaudioWavePlaybackEndpoint owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            NaudioWavePlaybackEndpoint? owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseKeepAliveLease();
        }
    }

    private sealed class KeepAlivePlayback : IDisposable
    {
        private readonly OutputPlayerBundle _bundle;
        private readonly string _backend;
        private int _disposed;

        public KeepAlivePlayback(OutputPlayerBundle bundle, string backend)
        {
            _bundle = bundle;
            _backend = backend;
            _bundle.Player.PlaybackStopped += OnPlaybackStopped;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _bundle.Player.PlaybackStopped -= OnPlaybackStopped;
            }
            catch
            {
                // Ignore event detachment races during shutdown.
            }

            try
            {
                _bundle.Player.Stop();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] Silent audio keep-alive stop ignored for {_backend}: {ex.Message}");
            }

            _bundle.Dispose();
            System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] Silent audio keep-alive disposed for {_backend}.");
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception != null)
            {
                System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] Silent audio keep-alive stopped with exception on {_backend}: {args.Exception.Message}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] Silent audio keep-alive stopped normally on {_backend}.");
            }
        }
    }

    private sealed class OutputPlayerBundle : IDisposable
    {
        public OutputPlayerBundle(IWavePlayer player, MMDeviceEnumerator? enumerator = null, MMDevice? device = null)
        {
            Player = player;
            Enumerator = enumerator;
            Device = device;
        }

        public IWavePlayer Player { get; }
        private MMDeviceEnumerator? Enumerator { get; }
        private MMDevice? Device { get; }

        public void Dispose()
        {
            Player.Dispose();
            Device?.Dispose();
            Enumerator?.Dispose();
        }
    }

    private sealed class SilentPcmWaveProvider : IWaveProvider
    {
        public SilentPcmWaveProvider(int sampleRate, int channels)
        {
            WaveFormat = new WaveFormat(sampleRate, 16, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<byte> buffer)
        {
            buffer.Clear();
            return buffer.Length;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new NoopDisposable();

        public void Dispose()
        {
        }
    }
}
