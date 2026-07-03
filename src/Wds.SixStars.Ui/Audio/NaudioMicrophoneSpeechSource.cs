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
/// </summary>
public sealed class NaudioMicrophoneSpeechSource : IDisposable
{
    private readonly WhisperSpeechInputPipeline _speechPipeline;
    private readonly PcmAudioFormat _format = PcmAudioFormat.Mono16Khz();
    private WaveInEvent? _waveIn;
    private Channel<byte[]>? _channel;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;

    public NaudioMicrophoneSpeechSource(WhisperSpeechInputPipeline speechPipeline)
    {
        _speechPipeline = speechPipeline;
    }

    public bool IsRunning => _waveIn != null;
    public event EventHandler<string>? StatusChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_waveIn != null)
        {
            StatusChanged?.Invoke(this, "Microphone is already running.");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(_format.SampleRateHz, _format.BitsPerSample, _format.Channels),
            BufferMilliseconds = 100,
            NumberOfBuffers = 3
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _pumpTask = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
        _waveIn.StartRecording();
        StatusChanged?.Invoke(this, "Microphone capture started: 16 kHz mono PCM16, 100 ms buffers.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        WaveInEvent? wave = _waveIn;
        if (wave == null)
        {
            StatusChanged?.Invoke(this, "Microphone is not running.");
            return;
        }

        try
        {
            wave.StopRecording();
        }
        catch
        {
            // Continue cleanup.
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
        }

        CleanupWaveIn();
        StatusChanged?.Invoke(this, "Microphone capture stopped.");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (args.BytesRecorded <= 0 || _channel == null)
        {
            return;
        }

        byte[] copy = new byte[args.BytesRecorded];
        Array.Copy(args.Buffer, copy, args.BytesRecorded);
        _channel.Writer.TryWrite(copy);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception != null)
        {
            StatusChanged?.Invoke(this, "Microphone stopped with error: " + args.Exception.Message);
        }
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
            await _speechPipeline.PushPcm16Async(buffer, buffer.Length, _format, "wpf-naudio-microphone", cancellationToken).ConfigureAwait(false);
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
    }

    public void Dispose()
    {
        try
        {
            _waveIn?.StopRecording();
        }
        catch
        {
        }

        _channel?.Writer.TryComplete();
        _cts?.Cancel();
        CleanupWaveIn();
    }
}
