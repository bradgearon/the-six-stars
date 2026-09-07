using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;
using Wds.SixStars.Api.Audio;
using Wds.SixStars.Api.Speech;

namespace Wds.SixStars.Ui.Audio;

/// <summary>
/// Desktop microphone adapter. It records 16 kHz mono PCM16 in ~100 ms chunks and feeds
/// the reusable WhisperSpeechInputPipeline. Unity does not use this class; Unity pushes audio
/// through UnityMicrophoneSpeechAdapter instead.
///
/// Windows can invalidate a WaveIn handle while the computer sleeps. The old WaveInEvent may
/// remain non-null after resume while never producing another DataAvailable event. This adapter
/// remembers whether capture was requested, watches the audio-buffer heartbeat, and rebuilds the
/// complete NAudio capture graph after resume or an unexpected device stop.
/// </summary>
public sealed class NaudioMicrophoneSpeechSource : IDisposable
{
    private const int RecoveryAttempts = 5;
    private static readonly TimeSpan ResumeDeviceSettleDelay = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan UnexpectedStopSettleDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MissingBufferThreshold = TimeSpan.FromSeconds(15);

    private readonly WhisperSpeechInputPipeline _speechPipeline;
    private readonly PcmAudioFormat _format = PcmAudioFormat.Mono16Khz();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _recoverySync = new();
    private WaveInEvent? _waveIn;
    private Channel<byte[]>? _channel;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private Timer? _captureHealthTimer;
    private Task<bool>? _activeRecoveryTask;
    private DateTimeOffset _lastDataAvailableAt = DateTimeOffset.MinValue;
    private volatile bool _desiredRunning;
    private volatile bool _disposed;
    private int _intentionalStop;

    public NaudioMicrophoneSpeechSource(WhisperSpeechInputPipeline speechPipeline)
    {
        _speechPipeline = speechPipeline;
    }

    public bool IsRunning => _waveIn != null;
    public bool WantsToRun => _desiredRunning;
    public event EventHandler<string>? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _desiredRunning = true;

            if (_waveIn != null)
            {
                StatusChanged?.Invoke(this, "Microphone is already running.");
                return;
            }

            StartCaptureCore(cancellationToken);
        }
        catch
        {
            _desiredRunning = false;
            AbortCaptureCore();
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _desiredRunning = false;
            if (_waveIn == null && _pumpTask == null)
            {
                StatusChanged?.Invoke(this, "Microphone is not running.");
                return;
            }

            await StopCaptureCoreAsync().ConfigureAwait(false);
            StatusChanged?.Invoke(this, "Microphone capture stopped.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Recreates the WaveIn device after Windows resumes. A stale WaveInEvent often remains
    /// non-null after sleep, so merely calling StartAsync would incorrectly report that capture
    /// is already running.
    /// </summary>
    public Task<bool> RecoverAfterSystemResumeAsync(CancellationToken cancellationToken)
    {
        return GetOrStartRecoveryTask(
            "Windows resumed. Reopening the microphone device...",
            "Microphone capture recovered after system resume.",
            ResumeDeviceSettleDelay,
            cancellationToken);
    }

    private Task<bool> GetOrStartRecoveryTask(
        string openingStatus,
        string successStatus,
        TimeSpan settleDelay,
        CancellationToken cancellationToken)
    {
        lock (_recoverySync)
        {
            if (_activeRecoveryTask != null && !_activeRecoveryTask.IsCompleted)
            {
                return _activeRecoveryTask;
            }

            _activeRecoveryTask = RecoverCaptureAsync(openingStatus, successStatus, settleDelay, cancellationToken);
            return _activeRecoveryTask;
        }
    }

    private async Task<bool> RecoverCaptureAsync(
        string openingStatus,
        string successStatus,
        TimeSpan settleDelay,
        CancellationToken cancellationToken)
    {
        if (!_desiredRunning || _disposed)
        {
            return false;
        }

        StatusChanged?.Invoke(this, openingStatus);
        await Task.Delay(settleDelay, cancellationToken).ConfigureAwait(false);

        Exception? lastError = null;
        for (int attempt = 1; attempt <= RecoveryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_desiredRunning || _disposed)
                {
                    return false;
                }

                await StopCaptureCoreAsync().ConfigureAwait(false);

                try
                {
                    // Recovery belongs to the application's listening state, not to a foreground
                    // request token that may have been cancelled while Windows was asleep.
                    StartCaptureCore(CancellationToken.None);
                    await _speechPipeline
                        .ResetAfterAudioDeviceRecoveryAsync(successStatus, CancellationToken.None)
                        .ConfigureAwait(false);
                    StatusChanged?.Invoke(this, successStatus);
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    AbortCaptureCore();
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }

            if (attempt < RecoveryAttempts)
            {
                int retryDelayMs = 350 * attempt;
                StatusChanged?.Invoke(this, "Microphone is not ready. Retrying in " + retryDelayMs + " ms...");
                await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        StatusChanged?.Invoke(this, "Microphone recovery failed: " + (lastError?.Message ?? "unknown audio-device error"));
        return false;
    }

    private void StartCaptureCore(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        WaveInEvent wave = new WaveInEvent
        {
            DeviceNumber = -1,
            WaveFormat = new WaveFormat(_format.SampleRateHz, _format.BitsPerSample, _format.Channels),
            BufferMilliseconds = 100,
            NumberOfBuffers = 3
        };

        wave.DataAvailable += OnDataAvailable;
        wave.RecordingStopped += OnRecordingStopped;
        _waveIn = wave;
        _pumpTask = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
        _lastDataAvailableAt = DateTimeOffset.UtcNow;

        try
        {
            wave.StartRecording();
            _captureHealthTimer = new Timer(
                CheckCaptureHealth,
                null,
                HealthCheckInterval,
                HealthCheckInterval);
        }
        catch
        {
            AbortCaptureCore();
            throw;
        }

        StatusChanged?.Invoke(this, "Microphone capture started: 16 kHz mono PCM16, 100 ms buffers.");
    }

    private async Task StopCaptureCoreAsync()
    {
        WaveInEvent? wave = _waveIn;
        Interlocked.Exchange(ref _intentionalStop, 1);
        try
        {
            _captureHealthTimer?.Dispose();
            _captureHealthTimer = null;

            if (wave != null)
            {
                try
                {
                    wave.StopRecording();
                }
                catch
                {
                    // The device may already have disappeared during sleep. Continue cleanup.
                }
            }

            _channel?.Writer.TryComplete();
            _cts?.Cancel();
            if (_pumpTask != null)
            {
                try
                {
                    await _pumpTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke(this, "Microphone audio pump stopped with error: " + ex.Message);
                }
            }

            CleanupWaveIn();
        }
        finally
        {
            Interlocked.Exchange(ref _intentionalStop, 0);
        }
    }

    private void AbortCaptureCore()
    {
        _captureHealthTimer?.Dispose();
        _captureHealthTimer = null;
        _channel?.Writer.TryComplete();
        _cts?.Cancel();
        CleanupWaveIn();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (!ReferenceEquals(sender, _waveIn) || args.BytesRecorded <= 0 || _channel == null)
        {
            return;
        }

        _lastDataAvailableAt = DateTimeOffset.UtcNow;
        byte[] copy = new byte[args.BytesRecorded];
        Array.Copy(args.Buffer, copy, args.BytesRecorded);
        _channel.Writer.TryWrite(copy);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (!ReferenceEquals(sender, _waveIn))
        {
            return;
        }

        if (args.Exception != null)
        {
            StatusChanged?.Invoke(this, "Microphone stopped with error: " + args.Exception.Message);
        }

        if (!_desiredRunning || _disposed || Volatile.Read(ref _intentionalStop) != 0)
        {
            return;
        }

        ScheduleRecovery(
            "Microphone stopped unexpectedly. Reopening the audio device...",
            "Microphone capture recovered after an unexpected device stop.",
            UnexpectedStopSettleDelay);
    }

    private void CheckCaptureHealth(object? state)
    {
        if (!_desiredRunning || _disposed || _waveIn == null || Volatile.Read(ref _intentionalStop) != 0)
        {
            return;
        }

        DateTimeOffset lastBuffer = _lastDataAvailableAt;
        if (lastBuffer == DateTimeOffset.MinValue
            || DateTimeOffset.UtcNow - lastBuffer <= MissingBufferThreshold)
        {
            return;
        }

        ScheduleRecovery(
            "Microphone stopped delivering audio buffers. Reopening the device...",
            "Microphone capture recovered after the audio-buffer heartbeat stopped.",
            UnexpectedStopSettleDelay);
    }

    private void ScheduleRecovery(string openingStatus, string successStatus, TimeSpan settleDelay)
    {
        Task<bool> recovery = GetOrStartRecoveryTask(openingStatus, successStatus, settleDelay, CancellationToken.None);
        _ = recovery.ContinueWith(
            task => StatusChanged?.Invoke(this, "Automatic microphone recovery failed: " + task.Exception?.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        Channel<byte[]>? channel = _channel;
        if (channel == null)
        {
            return;
        }

        await foreach (byte[] buffer in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await _speechPipeline.PushPcm16Async(
                buffer,
                buffer.Length,
                _format,
                "wpf-naudio-microphone",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void CleanupWaveIn()
    {
        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }

        _cts?.Dispose();
        _cts = null;
        _channel = null;
        _pumpTask = null;
        _lastDataAvailableAt = DateTimeOffset.MinValue;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NaudioMicrophoneSpeechSource));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _desiredRunning = false;
        Interlocked.Exchange(ref _intentionalStop, 1);
        try
        {
            try
            {
                _waveIn?.StopRecording();
            }
            catch
            {
            }

            AbortCaptureCore();
        }
        finally
        {
            // Do not dispose the lifecycle semaphore here: a resume notification can already be
            // awaiting it while the WPF window is closing. The process is exiting and the tiny
            // managed semaphore is safer to leave for collection than to race with that callback.
        }
    }
}
