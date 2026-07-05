using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Voice;

namespace Wds.SixStars.Ui.Networking;

/// <summary>
/// Lightweight persistent TCP socket for Android phone audio playback.
///
/// This is intentionally not the Qwen TTS WebSocket. The desktop app still connects to
/// Qwen over WebSocket and receives WAV chunks there. This host is the second hop:
/// desktop -> Android. It sends ordered WAV chunks to the phone as small framed messages.
/// </summary>
public sealed class MobileBridgeAudioSocketHost : IDisposable
{
    private const byte FrameStatus = 1;
    private const byte FrameAudioWav = 2;
    private const byte FrameError = 3;

    private readonly object _gate = new object();
    private readonly List<ClientConnection> _clients = new List<ClientConnection>();
    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private int _lastClientId;
    private bool _disposed;

    public event EventHandler<string>? StatusChanged;

    public int Port { get; private set; }
    public int ClientCount
    {
        get
        {
            lock (_gate) return _clients.Count;
        }
    }

    public void Start(int port)
    {
        if (_listener != null) return;
        Port = port;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        PublishStatus("Phone audio socket listening on port " + port + ".");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        List<ClientConnection> snapshot;
        lock (_gate)
        {
            snapshot = new List<ClientConnection>(_clients);
            _clients.Clear();
        }

        foreach (ClientConnection client in snapshot)
        {
            client.Dispose();
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        PublishStatus("Phone audio socket stopped.");
    }

    public async Task<MobileAudioSendResult> SendStatusAsync(string message, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { message }, _jsonOptions));
        return await BroadcastFrameAsync(FrameStatus, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MobileAudioSendResult> SendErrorAsync(string message, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { message }, _jsonOptions));
        return await BroadcastFrameAsync(FrameError, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MobileAudioSendResult> SendVoiceChunkAsync(VoiceSynthesisResult result, int chunkIndex, int totalChunks, CancellationToken cancellationToken)
    {
        if (result == null || result.WavAudio == null || result.WavAudio.Length == 0)
        {
            return MobileAudioSendResult.Empty(ClientCount);
        }

        string checksum = Sha256Hex(result.WavAudio);
        string header = JsonSerializer.Serialize(new
        {
            chunkIndex,
            totalChunks,
            starId = result.StarId ?? string.Empty,
            voiceId = result.VoiceId ?? string.Empty,
            mimeType = string.IsNullOrWhiteSpace(result.MimeType) ? "audio/wav" : result.MimeType,
            bytes = result.WavAudio.Length,
            sha256 = checksum,
            sha256Short = checksum.Length > 12 ? checksum.Substring(0, 12) : checksum,
            durationMs = result.DurationMs,
            source = result.Source ?? string.Empty,
            sentAtUtc = DateTimeOffset.UtcNow
        }, _jsonOptions);

        byte[] headerBytes = Encoding.UTF8.GetBytes(header);
        byte[] payload = new byte[4 + headerBytes.Length + result.WavAudio.Length];
        WriteInt32BigEndian(payload, 0, headerBytes.Length);
        Buffer.BlockCopy(headerBytes, 0, payload, 4, headerBytes.Length);
        Buffer.BlockCopy(result.WavAudio, 0, payload, 4 + headerBytes.Length, result.WavAudio.Length);

        return await BroadcastFrameAsync(FrameAudioWav, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? accepted = null;
            try
            {
                TcpListener listener = _listener ?? throw new ObjectDisposedException(nameof(MobileBridgeAudioSocketHost));
                accepted = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                accepted.NoDelay = true;
                int id = Interlocked.Increment(ref _lastClientId);
                ClientConnection connection = new ClientConnection(id, accepted);
                accepted = null;
                lock (_gate) _clients.Add(connection);
                PublishStatus("Phone audio client connected. Clients=" + ClientCount + ".");
                _ = Task.Run(() => WatchClientAsync(connection, cancellationToken), CancellationToken.None);
                await SendStatusAsync("Connected to Six Stars desktop voice queue.", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MobileAudioSocket] Accept error: " + ex);
                PublishStatus("Phone audio socket accept error: " + ex.Message);
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                accepted?.Dispose();
            }
        }
    }

    private async Task WatchClientAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        byte[] one = new byte[1];
        try
        {
            NetworkStream stream = connection.Client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(one, 0, 1, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                // Client pings are ignored. The socket is primarily desktop -> phone.
            }
        }
        catch
        {
            // Treat as disconnect.
        }
        finally
        {
            RemoveClient(connection);
        }
    }

    private async Task<MobileAudioSendResult> BroadcastFrameAsync(byte frameType, byte[] payload, CancellationToken cancellationToken)
    {
        List<ClientConnection> snapshot;
        lock (_gate) snapshot = new List<ClientConnection>(_clients);
        if (snapshot.Count == 0)
        {
            return MobileAudioSendResult.Empty(0);
        }

        int sentClients = 0;
        int failedClients = 0;
        foreach (ClientConnection client in snapshot)
        {
            try
            {
                await client.SendFrameAsync(frameType, payload, cancellationToken).ConfigureAwait(false);
                sentClients++;
            }
            catch (Exception ex)
            {
                failedClients++;
                Debug.WriteLine("[MobileAudioSocket] Removing failed client: " + ex.Message);
                RemoveClient(client);
            }
        }

        return new MobileAudioSendResult
        {
            ConnectedClients = ClientCount,
            SentClients = sentClients,
            FailedClients = failedClients,
            BytesSent = payload.Length * sentClients
        };
    }

    private void RemoveClient(ClientConnection client)
    {
        bool removed;
        lock (_gate) removed = _clients.Remove(client);
        client.Dispose();
        if (removed) PublishStatus("Phone audio client disconnected. Clients=" + ClientCount + ".");
    }

    private void PublishStatus(string status)
    {
        StatusChanged?.Invoke(this, status);
    }


    private static string Sha256Hex(byte[] bytes)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes);
        StringBuilder builder = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) builder.Append(b.ToString("x2"));
        return builder.ToString();
    }

    private static void WriteInt32BigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)((value >> 24) & 0xff);
        target[offset + 1] = (byte)((value >> 16) & 0xff);
        target[offset + 2] = (byte)((value >> 8) & 0xff);
        target[offset + 3] = (byte)(value & 0xff);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private sealed class ClientConnection : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        public int Id { get; }
        public TcpClient Client { get; }

        public ClientConnection(int id, TcpClient client)
        {
            Id = id;
            Client = client;
        }

        public async Task SendFrameAsync(byte type, byte[] payload, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                NetworkStream stream = Client.GetStream();
                byte[] header = new byte[5];
                header[0] = type;
                WriteInt32BigEndian(header, 1, payload.Length);
                await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
                if (payload.Length > 0)
                {
                    await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            try { Client.Close(); } catch { }
            try { Client.Dispose(); } catch { }
            _sendLock.Dispose();
        }
    }
}

public sealed class MobileAudioSendResult
{
    public int ConnectedClients { get; set; }
    public int SentClients { get; set; }
    public int FailedClients { get; set; }
    public int BytesSent { get; set; }

    public static MobileAudioSendResult Empty(int connectedClients) => new MobileAudioSendResult { ConnectedClients = connectedClients };
}
