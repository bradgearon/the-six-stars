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
/// WASAPI shared mode is the desktop harness default for smooth long-running speech.
/// DirectSound and WaveOut remain available as compatibility fallbacks.
/// Set SIX_STARS_AUDIO_BACKEND=wasapi, waveout, directsound, or auto to override.
/// </summary>
public sealed class NaudioWavePlaybackEndpoint : IWavePlaybackEndpoint, IAudioOutputKeepAliveEndpoint, IStreamingWavePlaybackEndpoint
{
    private const int ContinuousStartupPrebufferMs = 420;
    private const int ContinuousOutputLatencyMs = 100;
    private const double SentenceEdgeFadeMs = 4.0;
    private readonly string? _configuredBackend;
    private readonly object _keepAliveGate = new object();
    private KeepAlivePlayback? _keepAlivePlayback;
    private int _keepAliveLeaseCount;

    public NaudioWavePlaybackEndpoint(string? configuredBackend = null)
    {
        _configuredBackend = configuredBackend;
    }

    public IStreamingWavePlaybackSession CreateStreamingSession(string reason)
    {
        return new ContinuousWavePlaybackSession(_configuredBackend, reason);
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
            await PlayWithFallbacksAsync(wav, voiceAudio.MimeType, cancellationToken, "waveout", "wasapi", "directsound").ConfigureAwait(false);
            return;
        }

        if (backend == "directsound")
        {
            // DirectSound is useful on some machines, but do not let the known NAudio/DirectSoundOut
            // GCHandle failure kill playback. Fall back to the modern Windows endpoint immediately.
            await PlayWithFallbacksAsync(wav, voiceAudio.MimeType, cancellationToken, "directsound", "wasapi", "waveout").ConfigureAwait(false);
            return;
        }

        // auto: prefer the Windows default endpoint, then the older WaveOut path, then DirectSound last.
        await PlayWithFallbacksAsync(wav, voiceAudio.MimeType, cancellationToken, "wasapi", "waveout", "directsound").ConfigureAwait(false);
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
                        await PlayWithWaveOutEventAsync(wav, mimeType, cancellationToken).ConfigureAwait(false);
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
        BufferedWaveProvider provider = new BufferedWaveProvider(reader.WaveFormat)
        {
            DiscardOnBufferOverflow = false,
            BufferDuration = reader.TotalTime + TimeSpan.FromSeconds(1),
            ReadFully = false
        };
        provider.AddSamples(pcm, 0, pcm.Length);

        using DirectSoundOut output = new DirectSoundOut(100);
        await PlayInitializedOutputAsync(output, provider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PlayWithWaveOutEventAsync(byte[] wav, string? mimeType, CancellationToken cancellationToken)
    {
        using MemoryStream memoryStream = new MemoryStream(wav, writable: false);
        using WaveStream reader = CreateReader(memoryStream, mimeType);
        using WaveOutEvent output = new WaveOutEvent
        {
            DeviceNumber = -1,
            DesiredLatency = 100,
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
                return new OutputPlayerBundle(new WaveOutEvent
                {
                    DeviceNumber = -1,
                    DesiredLatency = latencyMs,
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

    private sealed class ContinuousWavePlaybackSession : IStreamingWavePlaybackSession
    {
        private readonly string? _configuredBackend;
        private readonly string _reason;
        private readonly object _gate = new object();
        private OutputPlayerBundle? _bundle;
        private BufferedWaveProvider? _provider;
        private WaveFormat? _format;
        private bool _started;
        private bool _completed;
        private bool _cancelled;
        private int _disposed;

        public ContinuousWavePlaybackSession(string? configuredBackend, string reason)
        {
            _configuredBackend = configuredBackend;
            _reason = string.IsNullOrWhiteSpace(reason) ? "continuous streaming speech" : reason;
        }

        public Task EnqueueAsync(VoiceSynthesisResult voiceAudio, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (voiceAudio == null) throw new ArgumentNullException(nameof(voiceAudio));
            byte[] wav = voiceAudio.WavAudio ?? Array.Empty<byte>();
            if (wav.Length == 0) return Task.CompletedTask;

            byte[] pcm;
            WaveFormat waveFormat;
            using (MemoryStream memoryStream = new MemoryStream(wav, writable: false))
            using (WaveStream reader = CreateReader(memoryStream, voiceAudio.MimeType))
            {
                waveFormat = reader.WaveFormat;
                pcm = ReadAllPcmBytes(reader);
            }

            if (pcm.Length == 0) return Task.CompletedTask;

            // Full WAV chunks are intentional speech parts (normally a complete sentence). A tiny
            // edge ramp removes any non-zero start/end sample from the synthesizer so a natural
            // pause between separately synthesized sentences cannot turn into a click. Never ramp
            // the rolling PCM transport windows, because doing that to every packet would amplitude-
            // modulate the voice at the packet cadence.
            if (!voiceAudio.IsStreamingPcmWindow)
            {
                ApplyPcm16EdgeFadeInPlace(pcm, waveFormat, SentenceEdgeFadeMs);
            }

            lock (_gate)
            {
                if (_cancelled) throw new OperationCanceledException("Continuous desktop speech playback was cancelled.", cancellationToken);
                if (_completed) throw new InvalidOperationException("Cannot enqueue audio after continuous desktop playback has completed.");

                if (_provider == null)
                {
                    _format = waveFormat;
                    _provider = new BufferedWaveProvider(waveFormat)
                    {
                        // Keep the device alive if generation momentarily falls behind. qwentts.cpp normally
                        // produces packets faster than real time, so this avoids reopen/click gaps without
                        // manufacturing a separate output stream for every transport packet.
                        ReadFully = true,
                        DiscardOnBufferOverflow = false,
                        BufferDuration = TimeSpan.FromMinutes(2)
                    };
                    _provider.AddSamples(pcm, 0, pcm.Length);
                    StartOutputLocked(force: false);
                }
                else
                {
                    if (!SameWaveFormat(_format!, waveFormat))
                    {
                        throw new InvalidOperationException(
                            "Streaming TTS changed audio format mid-response. Expected " + DescribeWaveFormat(_format!) +
                            " but received " + DescribeWaveFormat(waveFormat) + ".");
                    }

                    _provider.AddSamples(pcm, 0, pcm.Length);
                    StartOutputLocked(force: false);
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[NaudioPlayback] Continuous stream queued {pcm.Length} PCM bytes; buffered={_provider.BufferedDuration.TotalMilliseconds:0} ms; reason={_reason}.");
            }

            return Task.CompletedTask;
        }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            BufferedWaveProvider? provider;
            lock (_gate)
            {
                if (_completed) return;
                _completed = true;
                provider = _provider;
            }

            if (provider == null) return;

            lock (_gate)
            {
                // Short utterances may contain less than the normal startup prebuffer. Completion means
                // no more input is coming, so start whatever is available and let it drain cleanly.
                StartOutputLocked(force: true);
            }

            // BufferedWaveProvider uses ReadFully=true so the output remains alive rather than stopping
            // at a momentary packet underrun. Completion therefore waits explicitly for queued PCM to
            // drain, then gives the device one latency window to render its final sample before stopping.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_cancelled) throw new OperationCanceledException("Continuous desktop speech playback was cancelled.", cancellationToken);

                int bufferedBytes;
                lock (_gate)
                {
                    bufferedBytes = provider.BufferedBytes;
                }

                if (bufferedBytes <= 0) break;
                await Task.Delay(8, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            StopOutput();
        }

        public void Cancel()
        {
            lock (_gate)
            {
                _cancelled = true;
                _provider?.ClearBuffer();
            }
            StopOutput();
        }

        private void StartOutputLocked(bool force)
        {
            if (_started || _provider == null) return;

            if (!force && _provider.BufferedDuration.TotalMilliseconds < ContinuousStartupPrebufferMs)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[NaudioPlayback] Waiting for desktop speech prebuffer: {_provider.BufferedDuration.TotalMilliseconds:0} / {ContinuousStartupPrebufferMs} ms.");
                return;
            }

            string backend = NormalizeBackend(_configuredBackend ?? Environment.GetEnvironmentVariable("SIX_STARS_AUDIO_BACKEND") ?? "wasapi");
            string[] candidates = backend switch
            {
                "wasapi" => new[] { "wasapi", "waveout", "directsound" },
                "waveout" => new[] { "waveout", "wasapi", "directsound" },
                // DirectSound is fine for one-shot compatibility, but its small circular buffers can
                // produce periodic crackles on some Windows drivers during long rolling speech. For
                // continuous Star speech prefer WASAPI shared mode first even when the legacy desktop
                // setting still says directsound.
                "directsound" => new[] { "wasapi", "directsound", "waveout" },
                _ => new[] { "wasapi", "waveout", "directsound" }
            };

            Exception? lastException = null;
            foreach (string candidate in candidates)
            {
                OutputPlayerBundle? candidateBundle = null;
                try
                {
                    candidateBundle = CreateOutputPlayerBundle(candidate, latencyMs: ContinuousOutputLatencyMs);
                    candidateBundle.Player.Init(_provider);
                    candidateBundle.Player.Play();
                    _bundle = candidateBundle;
                    candidateBundle = null;
                    _started = true;
                    System.Diagnostics.Debug.WriteLine(
                        $"[NaudioPlayback] Continuous streaming output started with {candidate} at {DescribeWaveFormat(_format!)}. Reason: {_reason}");
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    candidateBundle?.Dispose();
                    System.Diagnostics.Debug.WriteLine(
                        $"[NaudioPlayback] Continuous streaming output failed for {candidate}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            throw new InvalidOperationException("All configured NAudio backends failed to start continuous streaming playback.", lastException);
        }

        private void StopOutput()
        {
            OutputPlayerBundle? bundle;
            lock (_gate)
            {
                bundle = _bundle;
                _bundle = null;
                _started = false;
            }

            if (bundle == null) return;
            try
            {
                bundle.Player.Stop();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[NaudioPlayback] Continuous streaming stop ignored: " + ex.Message);
            }
            finally
            {
                bundle.Dispose();
            }
        }

        private static void ApplyPcm16EdgeFadeInPlace(byte[] pcm, WaveFormat format, double fadeMilliseconds)
        {
            if (pcm == null || pcm.Length < 4 || format == null) return;
            if (format.BitsPerSample != 16 || format.Encoding != WaveFormatEncoding.Pcm) return;
            if (format.Channels <= 0 || format.SampleRate <= 0) return;

            int bytesPerFrame = format.Channels * 2;
            int totalFrames = pcm.Length / bytesPerFrame;
            if (totalFrames < 4) return;

            int fadeFrames = (int)Math.Round(format.SampleRate * Math.Max(0.0, fadeMilliseconds) / 1000.0);
            fadeFrames = Math.Min(fadeFrames, totalFrames / 4);
            if (fadeFrames <= 1) return;

            for (int frame = 0; frame < fadeFrames; frame++)
            {
                double gainIn = frame / (double)(fadeFrames - 1);
                double gainOut = (fadeFrames - 1 - frame) / (double)(fadeFrames - 1);

                ScalePcm16Frame(pcm, frame * bytesPerFrame, format.Channels, gainIn);
                ScalePcm16Frame(pcm, (totalFrames - fadeFrames + frame) * bytesPerFrame, format.Channels, gainOut);
            }
        }

        private static void ScalePcm16Frame(byte[] pcm, int offset, int channels, double gain)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                int sampleOffset = offset + channel * 2;
                short sample = (short)(pcm[sampleOffset] | (pcm[sampleOffset + 1] << 8));
                int scaled = (int)Math.Round(sample * gain);
                scaled = Math.Max(short.MinValue, Math.Min(short.MaxValue, scaled));
                pcm[sampleOffset] = (byte)(scaled & 0xFF);
                pcm[sampleOffset + 1] = (byte)((scaled >> 8) & 0xFF);
            }
        }

        private static bool SameWaveFormat(WaveFormat left, WaveFormat right)
        {
            return left.SampleRate == right.SampleRate
                && left.BitsPerSample == right.BitsPerSample
                && left.Channels == right.Channels
                && left.Encoding == right.Encoding
                && left.BlockAlign == right.BlockAlign;
        }

        private static string DescribeWaveFormat(WaveFormat format)
        {
            return format.SampleRate + "Hz/" + format.BitsPerSample + "bit/" + format.Channels + "ch/" + format.Encoding;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Cancel();
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

        public int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
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
