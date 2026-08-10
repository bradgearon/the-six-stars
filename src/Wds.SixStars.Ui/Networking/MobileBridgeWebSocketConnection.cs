using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Ui.Networking;

/// <summary>
/// RFC 6455 connection for the local mobile bridge. The desktop bridge already owns the TCP
/// listener, so the HTTP upgrade stays on the same port and the platform WebSocket implementation
/// handles frame masking, ping/pong, fragmentation, and protocol validation.
/// </summary>
internal sealed class MobileBridgeWebSocketConnection : IDisposable
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;
    private const int ReceiveBufferBytes = 16 * 1024;
    private readonly TcpClient _client;
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private int _disposed;

    public MobileBridgeWebSocketConnection(int id, TcpClient client, int bridgePort)
    {
        Id = id;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        NetworkInfo = MobileBridgeNetworkAddressDiscovery.Discover(_client, bridgePort);
        _socket = WebSocket.CreateFromStream(
            _client.GetStream(),
            isServer: true,
            subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(20));
    }

    public int Id { get; }
    public MobileBridgeConnectionNetworkInfo NetworkInfo { get; }
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _socket.State == WebSocketState.Open;

    public Task SendJsonAsync(object value, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(value, options);
        return SendTextAsync(json, cancellationToken);
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(MobileBridgeWebSocketConnection));
        byte[] payload = Encoding.UTF8.GetBytes(text ?? string.Empty);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                throw new WebSocketException("The mobile bridge WebSocket is not open.");
            }
            await _socket.SendAsync(
                new ArraySegment<byte>(payload),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<string?> ReadTextMessageAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[ReceiveBufferBytes];
        using MemoryStream message = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await _socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (_socket.State == WebSocketState.CloseReceived)
                {
                    try
                    {
                        await _socket.CloseOutputAsync(
                            result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            result.CloseStatusDescription ?? string.Empty,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                try
                {
                    await _socket.CloseOutputAsync(
                        WebSocketCloseStatus.InvalidMessageType,
                        "Text messages only",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
                return null;
            }

            if (message.Length + result.Count > MaxMessageBytes)
            {
                try
                {
                    await _socket.CloseOutputAsync(
                        WebSocketCloseStatus.MessageTooBig,
                        "Message too large",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
                return null;
            }

            if (result.Count > 0) message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
        }

        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _socket.Dispose(); } catch { }
        try { _client.Close(); } catch { }
        // A response task may still be unwinding through SendTextAsync after the socket is closed.
        // Leaving the lightweight semaphore for GC avoids racing a pending Release().
    }
}
