using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Voice;
using Wds.SixStars.Ui.Networking;

namespace Wds.SixStars.Ui.Services;

/// <summary>
/// Stable façade over the currently running phone-audio host. The ViewModel describes a voice
/// stream; this service owns the transport lookup and gracefully handles a stopped bridge.
/// </summary>
internal sealed class MobileVoiceDeliveryService
{
    private readonly Func<MobileBridgeAudioSocketHost?> _audioHost;

    public MobileVoiceDeliveryService(Func<MobileBridgeAudioSocketHost?> audioHost)
        => _audioHost = audioHost ?? throw new ArgumentNullException(nameof(audioHost));

    public int ConnectedClients => _audioHost()?.ClientCount ?? 0;
    public bool IsConnected => ConnectedClients > 0;

    public MobileAudioStreamSession? CreateStreamSession(
        string streamId,
        long streamOrdinal,
        string sessionId,
        string starId,
        bool isStory,
        string reason)
        => _audioHost()?.CreateStreamSession(streamId, streamOrdinal, sessionId, starId, isStory, reason);

    public Task<MobileAudioSendResult> SendChunkAsync(
        VoiceSynthesisResult result,
        int chunkIndex,
        int totalChunks,
        CancellationToken cancellationToken,
        MobileAudioStreamSession? streamSession = null,
        MobileAudioFrameContext? frameContext = null)
    {
        MobileBridgeAudioSocketHost? host = _audioHost();
        if (host == null) return Task.FromResult(MobileAudioSendResult.Empty(0));
        return host.SendVoiceChunkAsync(result, chunkIndex, totalChunks, cancellationToken, streamSession, frameContext);
    }

    public Task<MobileAudioSendResult> SendControlAsync(
        MobileAudioStreamSession session,
        string action,
        string message,
        CancellationToken cancellationToken)
    {
        MobileBridgeAudioSocketHost? host = _audioHost();
        if (host == null) return Task.FromResult(MobileAudioSendResult.Empty(0));
        return host.SendStreamControlAsync(session, action, message, cancellationToken);
    }

    public Task<MobileAudioSendResult> SendStatusAsync(string message, CancellationToken cancellationToken)
    {
        MobileBridgeAudioSocketHost? host = _audioHost();
        return host == null
            ? Task.FromResult(MobileAudioSendResult.Empty(0))
            : host.SendStatusAsync(message, cancellationToken);
    }

    public Task<MobileAudioSendResult> SendErrorAsync(string message, CancellationToken cancellationToken)
    {
        MobileBridgeAudioSocketHost? host = _audioHost();
        return host == null
            ? Task.FromResult(MobileAudioSendResult.Empty(0))
            : host.SendErrorAsync(message, cancellationToken);
    }
}
