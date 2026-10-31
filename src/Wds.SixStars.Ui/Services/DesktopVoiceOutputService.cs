using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Voice;

namespace Wds.SixStars.Ui.Services;

internal sealed class DesktopVoiceOutputService : IDisposable
{
    internal sealed record Result(bool Configured, bool Streaming, VoiceSynthesisResult? Voice);

    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private readonly bool _useStreaming;
    private readonly StreamingStarVoiceResponsePipeline? _streaming;
    private readonly StarVoiceResponsePipeline? _standard;
    private readonly IWavePlaybackEndpoint _playback;

    public DesktopVoiceOutputService(
        bool useStreaming,
        StreamingStarVoiceResponsePipeline? streaming,
        StarVoiceResponsePipeline? standard,
        IWavePlaybackEndpoint playback)
    {
        _useStreaming = useStreaming;
        _streaming = streaming;
        _standard = standard;
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public async Task<Result> SpeakAsync(string sessionId, StarInteractionResponse response, CancellationToken cancellationToken)
    {
        // A replacement request waits for the previous pipeline and generator to finish
        // cancellation/cleanup before shared Star and queue state can be reset.
        await _speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await SpeakCoreAsync(sessionId, response, cancellationToken).ConfigureAwait(false); }
        finally { _speechGate.Release(); }
    }

    private async Task<Result> SpeakCoreAsync(string sessionId, StarInteractionResponse response, CancellationToken cancellationToken)
    {
        if (_useStreaming && _streaming != null)
        {
            await _streaming.SpeakStreamingAsync(sessionId, response, cancellationToken).ConfigureAwait(false);
            return new Result(true, true, null);
        }

        if (_standard != null)
        {
            VoiceSynthesisResult voice = await _standard.SpeakAsync(sessionId, response, cancellationToken).ConfigureAwait(false);
            return new Result(true, false, voice);
        }

        return new Result(false, false, null);
    }

    public Task PlayAsync(VoiceSynthesisResult voice, CancellationToken cancellationToken)
        => _playback.PlayWavAsync(voice, cancellationToken);

    public void Pause()
    {
        if (_streaming != null) _streaming.PausePlayback();
    }

    public void Resume()
    {
        if (_streaming != null) _streaming.ResumePlayback();
    }

    public void Cancel()
    {
        if (_streaming != null) _streaming.CancelCurrentSpeech();
    }

    public void Dispose() => _streaming?.Dispose();
}
