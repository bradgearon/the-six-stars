using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.Mobile;
using Wds.SixStars.Ui.Networking;

namespace Wds.SixStars.Ui.Services;

/// <summary>Owns HTTP/WebSocket and TCP audio host lifetimes as one desktop service.</summary>
internal sealed class MobileBridgeHostService : IDisposable
{
    private MobileBridgeHttpServer? _server;
    private MobileBridgeAudioSocketHost? _audioHost;

    public bool IsRunning => _server?.IsRunning == true;
    public int AudioClientCount => _audioHost?.ClientCount ?? 0;
    public int WebSocketClientCount => _server?.WebSocketClientCount ?? 0;
    public MobileBridgeAudioSocketHost? AudioHost => _audioHost;
    public event EventHandler<string>? StatusChanged;

    public void Start(
        int port,
        int audioPort,
        Func<MobileBridgeRequest, CancellationToken, Task<MobileBridgeResponse>> messageHandler,
        Func<MobileBridgeRequest, CancellationToken, Task<MobileBridgeResponse>> audioHandler,
        Func<CancellationToken, Task<object>> testAudioHandler,
        Func<string, string, CancellationToken, Task<byte[]>> standaloneLocalTtsHandler,
        Func<object> healthFactory,
        Func<object> starsFactory,
        Func<string, CancellationToken, Task<object>> stateFactory,
        MobileBridgeDispatcher dispatcher,
        Func<string, int, CancellationToken, Task<object>> conversationArchiveFactory,
        string conversationArchiveRoot,
        string conversationArchivePrefix,
        bool conversationArchiveEnabled,
        SixStarsRuntime runtime,
        string worldEventApiKey)
    {
        Stop();
        _audioHost = new MobileBridgeAudioSocketHost();
        _audioHost.StatusChanged += OnStatusChanged;
        _audioHost.Start(audioPort);

        _server = new MobileBridgeHttpServer(
            port,
            messageHandler,
            audioHandler,
            testAudioHandler,
            standaloneLocalTtsHandler,
            healthFactory,
            starsFactory,
            stateFactory,
            dispatcher,
            conversationArchiveFactory,
            conversationArchiveRoot,
            conversationArchivePrefix,
            conversationArchiveEnabled,
            runtime,
            worldEventApiKey);
        _server.StatusChanged += OnStatusChanged;
        _server.Start();
    }

    public void Stop()
    {
        if (_server != null) _server.StatusChanged -= OnStatusChanged;
        if (_audioHost != null) _audioHost.StatusChanged -= OnStatusChanged;
        try { _server?.Dispose(); } catch { }
        try { _audioHost?.Dispose(); } catch { }
        _server = null;
        _audioHost = null;
    }

    private void OnStatusChanged(object? sender, string message) => StatusChanged?.Invoke(this, message);
    public void Dispose() => Stop();
}
