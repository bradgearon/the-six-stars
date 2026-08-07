using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Speech;
using Wds.SixStars.Ui.Audio;

namespace Wds.SixStars.Ui.Services;

/// <summary>Owns microphone/pipeline ordering so the ViewModel does not coordinate two audio objects.</summary>
internal sealed class DesktopSpeechInputService : IDisposable
{
    private readonly WhisperSpeechInputPipeline _pipeline;
    private readonly NaudioMicrophoneSpeechSource _microphone;

    public DesktopSpeechInputService(WhisperSpeechInputPipeline pipeline, NaudioMicrophoneSpeechSource microphone)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _microphone = microphone ?? throw new ArgumentNullException(nameof(microphone));
    }

    public bool WantsToRun => _microphone.WantsToRun;
    public event EventHandler<string>? StatusChanged
    {
        add => _microphone.StatusChanged += value;
        remove => _microphone.StatusChanged -= value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _pipeline.StartListeningAsync(cancellationToken).ConfigureAwait(false);
        await _microphone.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _microphone.StopAsync(cancellationToken).ConfigureAwait(false);
        await _pipeline.StopListeningAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        await _microphone.StopAsync(cancellationToken).ConfigureAwait(false);
        await _pipeline.CancelListeningAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartConversationCaptureAsync(string starId, CancellationToken cancellationToken)
    {
        _pipeline.EnterConversationMode(starId);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> RecoverAfterResumeAsync(CancellationToken cancellationToken)
        => _microphone.RecoverAfterSystemResumeAsync(cancellationToken);

    public void Dispose() => _microphone.Dispose();
}
