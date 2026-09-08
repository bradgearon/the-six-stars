using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Wds.SixStars.Api.Voice;

namespace Wds.SixStars.Ui.Audio;

/// <summary>
/// Desktop equivalent of the Android SmartPcmPlaybackWriter.
///
/// One logical Star response owns exactly one DirectSoundOut instance and one rolling PCM buffer.
/// Incoming qwentts.cpp PCM transport windows are decoded and appended to that buffer. Playback
/// starts only after an adaptive reservoir has accumulated, and if generation unexpectedly falls
/// behind the output is paused/refilled before the DirectSound buffer is allowed to underrun.
///
/// The adaptive profile is shared across responses so a rough response raises the next response's
/// startup lead while consistently fast/clean responses gradually reduce it.
/// </summary>
internal sealed class SmartDirectSoundPcmPlaybackSession : IStreamingWavePlaybackSession
{
    private const int MinStartupMs = 500;
    private const int MaxStartupMs = 2200;
    private const int MinIdleStartMs = 180;
    private const int EmergencyLeadMs = 180;
    private const int MinRefillMs = 550;

    // DirectSoundOut services its secondary buffer periodically. A larger device-side latency gives
    // the NAudio DirectSound worker plenty of scheduling margin under GPU/LLM/TTS load and avoids the
    // ~50-100 ms callback cadence becoming audible as a pop when Windows is briefly busy.
    private const int DirectSoundLatencyMs = 260;
    private const int FinalDeviceDrainMs = DirectSoundLatencyMs + 120;
    private const int MonitorPeriodMs = 10;
    private const double RateAlpha = 0.25;
    private const double JitterAlpha = 0.25;

    private readonly DesktopSmartPcmAdaptiveProfile _profile;
    private readonly string _reason;
    private readonly object _gate = new object();
    private readonly AdaptiveStats _stats = new AdaptiveStats();
    private readonly CancellationTokenSource _monitorCts = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    private BufferedWaveProvider? _provider;
    private DirectSoundOut? _output;
    private WaveFormat? _format;
    private Task? _monitorTask;
    private bool _started;
    private bool _buffering;
    private bool _inputEnded;
    private bool _cancelled;
    private bool _stopped;
    private bool _starvedSinceLastWrite;
    private long _createdAtMs = Environment.TickCount64;
    private long _firstInputAtMs;
    private double _firstInputAudioMs;
    private long _lastInputAtMs;
    private long _finalDrainBeganAtMs;
    private int _emergencyRefills;
    private int _disposed;

    public SmartDirectSoundPcmPlaybackSession(DesktopSmartPcmAdaptiveProfile profile, string reason)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _reason = string.IsNullOrWhiteSpace(reason) ? "desktop smart PCM speech" : reason;
    }

    public Task EnqueueAsync(VoiceSynthesisResult voiceAudio, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (voiceAudio == null) throw new ArgumentNullException(nameof(voiceAudio));

        byte[] encoded = voiceAudio.WavAudio ?? Array.Empty<byte>();
        if (encoded.Length == 0) return Task.CompletedTask;

        DecodeAudio(encoded, voiceAudio.MimeType, out WaveFormat format, out byte[] pcm);
        if (pcm.Length == 0) return Task.CompletedTask;

        double durationMs = format.AverageBytesPerSecond > 0
            ? pcm.Length * 1000.0 / format.AverageBytesPerSecond
            : Math.Max(1.0, voiceAudio.DurationMs);
        long now = Environment.TickCount64;

        lock (_gate)
        {
            ThrowIfUnavailable(cancellationToken);

            if (_provider == null)
            {
                _format = format;
                _provider = new BufferedWaveProvider(format, TimeSpan.FromMinutes(2))
                {
                    // The physical DirectSound lane must never tear itself down on a transient producer
                    // gap. The smart low-water controller pauses/refills before this normally matters;
                    // ReadFully is a final safety net that returns silence rather than EOF.
                    ReadFully = true,
                    DiscardOnBufferOverflow = false
                };
            }
            else if (!SameWaveFormat(_format!, format))
            {
                throw new InvalidOperationException(
                    "Streaming TTS changed PCM format mid-response. Expected " + DescribeWaveFormat(_format!) +
                    " but received " + DescribeWaveFormat(format) + ".");
            }

            if (_firstInputAtMs == 0)
            {
                _firstInputAtMs = now;
                _firstInputAudioMs = Math.Max(1.0, durationMs);
            }

            _lastInputAtMs = now;
            _stats.Observe(durationMs, now);

            // If the safety-net silence was ever reached, make the first few milliseconds of the
            // returning PCM ramp up from zero. This is not a per-packet fade; it only runs after a real
            // starvation event, so it cannot amplitude-modulate normal rolling PCM windows.
            if (_starvedSinceLastWrite)
            {
                ApplyRecoveryFadeInPlace(pcm, format, 4.0);
                _starvedSinceLastWrite = false;
            }

            _provider.AddSamples(pcm, 0, pcm.Length);
            EnsureMonitorStartedLocked();

            Debug.WriteLine(
                $"[SmartDirectSound] queued={durationMs:0}ms, buffered={_provider.BufferedDuration.TotalMilliseconds:0}ms, " +
                $"target={CalculateTargetMsLocked():0}ms, rate={EffectiveGenerationRateLocked():0.00}x, reason={_reason}.");
        }

        return Task.CompletedTask;
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_cancelled) throw new OperationCanceledException("Smart DirectSound playback was cancelled.", cancellationToken);
            if (_inputEnded)
            {
                // Another caller is already completing this lane; just await the same drain task below.
            }
            else
            {
                _inputEnded = true;
                EnsureMonitorStartedLocked();
            }
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(Cancel);

        await _drained.Task.ConfigureAwait(false);
    }

    public void Cancel()
    {
        DirectSoundOut? output;
        lock (_gate)
        {
            if (_cancelled) return;
            _cancelled = true;
            _provider?.ClearBuffer();
            output = _output;
            _output = null;
            _stopped = true;
        }

        try { _monitorCts.Cancel(); } catch { }
        try { output?.Stop(); } catch { }
        try { output?.Dispose(); } catch { }
        _drained.TrySetCanceled();
    }

    private void EnsureMonitorStartedLocked()
    {
        if (_monitorTask != null) return;
        _monitorTask = Task.Run(() => MonitorLoopAsync(_monitorCts.Token));
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool done = false;
                lock (_gate)
                {
                    if (_cancelled)
                    {
                        done = true;
                    }
                    else
                    {
                        TickLocked();
                        done = _stopped && _inputEnded;
                    }
                }

                if (done) break;
                await Task.Delay(MonitorPeriodMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[SmartDirectSound] monitor failed: " + ex);
            StopWithError(ex);
            return;
        }

        lock (_gate)
        {
            if (!_cancelled && _inputEnded && _stopped)
            {
                _drained.TrySetResult(true);
            }
        }
    }

    private void TickLocked()
    {
        if (_provider == null)
        {
            if (_inputEnded)
            {
                _stopped = true;
                _drained.TrySetResult(true);
            }
            return;
        }

        double bufferedMs = _provider.BufferedDuration.TotalMilliseconds;
        double targetMs = CalculateTargetMsLocked();
        long idleMs = ProducerIdleMsLocked();
        long idleReleaseMs = CalculateProducerIdleReleaseMsLocked();

        if (!_started)
        {
            bool targetReached = bufferedMs >= targetMs;
            bool shortPhraseReady = bufferedMs >= MinIdleStartMs && (_inputEnded || idleMs >= idleReleaseMs);
            if (targetReached || shortPhraseReady)
            {
                StartDirectSoundLocked(targetReached ? "adaptive-target" : (_inputEnded ? "input-ended" : "producer-idle"));
            }
            else if (_inputEnded && bufferedMs > 0)
            {
                StartDirectSoundLocked("short-complete");
            }
            else if (_inputEnded && bufferedMs <= 0)
            {
                _stopped = true;
                _drained.TrySetResult(true);
            }
            return;
        }

        if (_output == null || _stopped) return;

        double lowWaterMs = Math.Max(EmergencyLeadMs, Math.Min(650.0, targetMs * 0.30));
        double refillTargetMs = Math.Max(MinRefillMs, Math.Min(1400.0, targetMs * 0.70));

        if (!_inputEnded && !_buffering && bufferedMs < lowWaterMs && bufferedMs > 0)
        {
            _buffering = true;
            _emergencyRefills++;
            _profile.IncreaseStartup(Math.Max(_profile.StartupMs + 180.0, targetMs));
            try { _output.Pause(); } catch { }
            Debug.WriteLine(
                $"[SmartDirectSound] emergency refill #{_emergencyRefills}: paused with {bufferedMs:0}ms lead; " +
                $"target={refillTargetMs:0}ms, rate={EffectiveGenerationRateLocked():0.00}x.");
            return;
        }

        if (_buffering)
        {
            bool refillReady = bufferedMs >= refillTargetMs;
            // Once we deliberately enter a refill, do not "give up" simply because the producer
            // has been quiet for a few hundred milliseconds. Holding one clean pause is preferable
            // to letting DirectSound consume the last samples, hit silence, and pop back in later.
            // Completion is the only tail-release exception.
            if (refillReady || _inputEnded)
            {
                _buffering = false;
                try { _output.Play(); } catch { }
                Debug.WriteLine(
                    $"[SmartDirectSound] refill released with {bufferedMs:0}ms lead; target={refillTargetMs:0}ms.");
            }
        }

        if (!_inputEnded && !_buffering && bufferedMs <= 0.5)
        {
            // This should be rare because the low-water refill normally catches the stream first.
            // Mark it so the next returning PCM gets one recovery fade rather than a hard zero->sample edge.
            _starvedSinceLastWrite = true;
            _profile.IncreaseStartup(Math.Max(_profile.StartupMs + 250.0, targetMs));
        }

        if (_inputEnded)
        {
            if (_buffering)
            {
                _buffering = false;
                try { _output.Play(); } catch { }
            }

            if (bufferedMs <= 0.5)
            {
                if (_finalDrainBeganAtMs == 0)
                {
                    _finalDrainBeganAtMs = Environment.TickCount64;
                }
                else if (Environment.TickCount64 - _finalDrainBeganAtMs >= FinalDeviceDrainMs)
                {
                    FinishOutputLocked();
                }
            }
            else
            {
                _finalDrainBeganAtMs = 0;
            }
        }
    }

    private void StartDirectSoundLocked(string startReason)
    {
        if (_started || _provider == null || _format == null) return;

        DirectSoundOut output = new DirectSoundOut(DirectSoundLatencyMs);
        output.PlaybackStopped += OnPlaybackStopped;
        try
        {
            output.Init(_provider);
            output.Play();
            _output = output;
            _started = true;

            long firstWait = _firstInputAtMs > 0 ? Math.Max(0, _firstInputAtMs - _createdAtMs) : 0;
            Debug.WriteLine(
                $"[SmartDirectSound] started DirectSound only; reason={startReason}, " +
                $"buffer={_provider.BufferedDuration.TotalMilliseconds:0}ms, target={CalculateTargetMsLocked():0}ms, " +
                $"rate={EffectiveGenerationRateLocked():0.00}x, jitter={_stats.JitterMs:0}ms, " +
                $"firstChunkWait={firstWait}ms, deviceLatency={DirectSoundLatencyMs}ms, format={DescribeWaveFormat(_format)}, lane={_reason}.");
        }
        catch
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            output.Dispose();
            throw;
        }
    }

    private void FinishOutputLocked()
    {
        if (_stopped) return;
        _stopped = true;

        DirectSoundOut? output = _output;
        _output = null;
        if (output != null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            try { output.Stop(); } catch { }
            try { output.Dispose(); } catch { }
        }

        double completedRate = EffectiveGenerationRateLocked();
        if (_emergencyRefills == 0 && completedRate > 1.45)
        {
            _profile.DecreaseStartup(40.0);
        }

        Debug.WriteLine(
            $"[SmartDirectSound] completed; rate={completedRate:0.00}x, emergencyRefills={_emergencyRefills}, " +
            $"learnedStartup={_profile.StartupMs:0}ms, lane={_reason}.");
        _drained.TrySetResult(true);
        try { _monitorCts.Cancel(); } catch { }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            StopWithError(e.Exception);
            return;
        }

        lock (_gate)
        {
            // Ignore our own normal stop. A spontaneous stop before input completion is not allowed to
            // cause device reopen churn; report it as an error instead so it is visible in diagnostics.
            if (_stopped || _cancelled) return;
            if (!_inputEnded)
            {
                StopWithErrorLocked(new InvalidOperationException("DirectSound stopped before the smart PCM stream completed."));
            }
        }
    }

    private void StopWithError(Exception error)
    {
        lock (_gate)
        {
            StopWithErrorLocked(error);
        }
    }

    private void StopWithErrorLocked(Exception error)
    {
        if (_stopped) return;
        _stopped = true;
        DirectSoundOut? output = _output;
        _output = null;
        if (output != null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            try { output.Stop(); } catch { }
            try { output.Dispose(); } catch { }
        }
        _drained.TrySetException(error);
        try { _monitorCts.Cancel(); } catch { }
    }

    private double CalculateTargetMsLocked()
    {
        double rate = EffectiveGenerationRateLocked();
        if (rate <= 0.0)
        {
            return Math.Max(MinStartupMs, Math.Min(MaxStartupMs, _profile.StartupMs));
        }

        double rateReserve;
        if (rate >= 2.5) rateReserve = 0.0;
        else if (rate >= 1.8) rateReserve = 100.0;
        else if (rate >= 1.4) rateReserve = 260.0;
        else if (rate >= 1.15) rateReserve = 460.0;
        else if (rate >= 1.0) rateReserve = 720.0;
        else rateReserve = 950.0 + Math.Min(500.0, (1.0 - rate) * 1400.0);

        double jitterReserve = Math.Min(550.0,
            _stats.JitterMs * 2.2 + Math.Max(0.0, _stats.MaxGapMs - _stats.AverageGapMs) * 0.55);
        double dynamic = MinStartupMs + rateReserve + jitterReserve;
        return Math.Max(MinStartupMs, Math.Min(MaxStartupMs, Math.Max(_profile.StartupMs, dynamic)));
    }

    private double EffectiveGenerationRateLocked()
    {
        double firstRate = FirstChunkGenerationRateLocked();
        double sustainedRate = _stats.Rate;
        if (sustainedRate <= 0.0) return firstRate;
        if (firstRate <= 0.0) return sustainedRate;
        if (_stats.ObservedChunks <= 2) return sustainedRate * 0.65 + firstRate * 0.35;
        if (_stats.ObservedChunks == 3) return sustainedRate * 0.85 + firstRate * 0.15;
        return sustainedRate;
    }

    private double FirstChunkGenerationRateLocked()
    {
        if (_firstInputAtMs <= 0 || _firstInputAudioMs <= 0.0) return 0.0;
        long waitMs = Math.Max(1, _firstInputAtMs - _createdAtMs);
        return Math.Max(0.05, Math.Min(12.0, _firstInputAudioMs / waitMs));
    }

    private long CalculateProducerIdleReleaseMsLocked()
    {
        if (_stats.AverageGapMs <= 0.0) return 320;
        double adaptive = _stats.AverageGapMs * 2.2 + _stats.JitterMs * 2.0 + 80.0;
        return (long)Math.Round(Math.Max(220.0, Math.Min(650.0, adaptive)));
    }

    private long ProducerIdleMsLocked()
    {
        if (_lastInputAtMs <= 0) return 0;
        return Math.Max(0, Environment.TickCount64 - _lastInputAtMs);
    }

    private void ThrowIfUnavailable(CancellationToken cancellationToken)
    {
        if (_cancelled) throw new OperationCanceledException("Smart DirectSound playback was cancelled.", cancellationToken);
        if (_inputEnded) throw new InvalidOperationException("Cannot enqueue audio after smart DirectSound playback has completed input.");
        if (_stopped) throw new InvalidOperationException("Cannot enqueue audio after smart DirectSound playback has stopped.");
    }

    private static void DecodeAudio(byte[] encoded, string? mimeType, out WaveFormat format, out byte[] pcm)
    {
        using MemoryStream memory = new MemoryStream(encoded, writable: false);
        using WaveStream reader = !string.IsNullOrWhiteSpace(mimeType)
            && mimeType.IndexOf("mp3", StringComparison.OrdinalIgnoreCase) >= 0
            ? new Mp3FileReader(memory)
            : new WaveFileReader(memory);

        WaveFormat sourceFormat = reader.WaveFormat;
        format = DirectSoundWaveFormatHelper.Canonicalize(sourceFormat);

        Debug.WriteLine(
            $"[SmartDirectSound] decoded source format {DescribeWaveFormat(sourceFormat)} ({sourceFormat.GetType().Name}) " +
            $"as DirectSound-safe {DescribeWaveFormat(format)} ({format.GetType().Name}).");

        using MemoryStream output = new MemoryStream();
        byte[] buffer = new byte[Math.Max(4096, format.AverageBytesPerSecond / 4)];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
        }
        pcm = output.ToArray();
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

    private static void ApplyRecoveryFadeInPlace(byte[] pcm, WaveFormat format, double fadeMilliseconds)
    {
        if (pcm.Length < 4 || format.BitsPerSample != 16 || format.Encoding != WaveFormatEncoding.Pcm
            || format.SampleRate <= 0 || format.Channels <= 0)
        {
            return;
        }

        int bytesPerFrame = format.Channels * 2;
        int frames = pcm.Length / bytesPerFrame;
        int fadeFrames = Math.Min(frames, (int)Math.Round(format.SampleRate * fadeMilliseconds / 1000.0));
        if (fadeFrames <= 1) return;

        for (int frame = 0; frame < fadeFrames; frame++)
        {
            double gain = frame / (double)(fadeFrames - 1);
            int frameOffset = frame * bytesPerFrame;
            for (int channel = 0; channel < format.Channels; channel++)
            {
                int sampleOffset = frameOffset + channel * 2;
                short sample = (short)(pcm[sampleOffset] | (pcm[sampleOffset + 1] << 8));
                int scaled = (int)Math.Round(sample * gain);
                pcm[sampleOffset] = (byte)(scaled & 0xFF);
                pcm[sampleOffset + 1] = (byte)((scaled >> 8) & 0xFF);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Cancel();
        _monitorCts.Dispose();
    }

    private sealed class AdaptiveStats
    {
        public long LastArrivalMs { get; private set; }
        public int ObservedChunks { get; private set; }
        public double Rate { get; private set; }
        public double AverageGapMs { get; private set; }
        public double JitterMs { get; private set; }
        public double MaxGapMs { get; private set; }

        public void Observe(double durationMs, long nowMs)
        {
            ObservedChunks++;
            durationMs = Math.Max(1.0, durationMs);
            if (LastArrivalMs > 0)
            {
                double gap = Math.Max(1.0, nowMs - LastArrivalMs);
                double instantRate = Math.Max(0.05, Math.Min(12.0, durationMs / gap));
                Rate = Rate <= 0.0 ? instantRate : Rate * (1.0 - RateAlpha) + instantRate * RateAlpha;
                if (AverageGapMs <= 0.0) AverageGapMs = gap;
                double deviation = Math.Abs(gap - AverageGapMs);
                AverageGapMs = AverageGapMs * (1.0 - JitterAlpha) + gap * JitterAlpha;
                JitterMs = JitterMs <= 0.0 ? deviation : JitterMs * (1.0 - JitterAlpha) + deviation * JitterAlpha;
                MaxGapMs = Math.Max(MaxGapMs * 0.95, gap);
            }
            LastArrivalMs = nowMs;
        }
    }
}

/// <summary>Learned desktop startup reservoir shared between logical DirectSound speech sessions.</summary>
internal sealed class DesktopSmartPcmAdaptiveProfile
{
    private const double MinStartupMs = 500.0;
    private const double MaxStartupMs = 2200.0;
    private readonly object _gate = new object();
    private double _startupMs = 900.0;

    public double StartupMs
    {
        get { lock (_gate) return _startupMs; }
    }

    public void IncreaseStartup(double requestedMs)
    {
        lock (_gate)
        {
            _startupMs = Math.Min(MaxStartupMs, Math.Max(_startupMs, requestedMs));
        }
    }

    public void DecreaseStartup(double amountMs)
    {
        lock (_gate)
        {
            _startupMs = Math.Max(MinStartupMs, _startupMs - Math.Max(0.0, amountMs));
        }
    }
}
