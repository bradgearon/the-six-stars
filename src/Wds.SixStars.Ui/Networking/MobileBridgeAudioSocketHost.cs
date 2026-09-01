using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
/// This is intentionally separate from qwentts.cpp. The desktop .NET speech layer calls
/// qwentts.cpp over HTTP and receives WAV chunks there. This host is only the second hop:
/// desktop -> Android. It sends ordered audio packets to the phone as small framed messages. PCM
/// streaming packets remain WAV-wrapped on the wire for compatibility; Android joins them into one
/// continuous AudioTrack per logical stream.
/// </summary>
public sealed class MobileBridgeAudioSocketHost : IDisposable
{
    private const byte FrameStatus = 1;
    private const byte FrameAudioWav = 2;
    private const byte FrameError = 3;
    private const int StreamReconnectGraceMilliseconds = 8000;

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

    public MobileAudioStreamSession CreateStreamSession(string streamId, long streamOrdinal, string sessionId, string starId, bool isStory, string reason)
    {
        List<int> targetClientIds;
        lock (_gate) targetClientIds = _clients.Select(x => x.Id).ToList();
        return new MobileAudioStreamSession
        {
            StreamId = string.IsNullOrWhiteSpace(streamId) ? Guid.NewGuid().ToString("N") : streamId,
            StreamOrdinal = streamOrdinal,
            SessionId = sessionId ?? string.Empty,
            StarId = starId ?? string.Empty,
            IsStory = isStory,
            Reason = reason ?? string.Empty,
            StartedAtUtc = DateTimeOffset.UtcNow,
            TargetClientIds = targetClientIds
        };
    }

    public async Task<MobileAudioSendResult> SendStreamControlAsync(MobileAudioStreamSession session, string action, string message, CancellationToken cancellationToken)
    {
        if (session == null) return MobileAudioSendResult.Empty(ClientCount);
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            message = message ?? string.Empty,
            streamAction = action ?? string.Empty,
            streamId = session.StreamId,
            streamOrdinal = session.StreamOrdinal,
            sessionId = session.SessionId,
            starId = session.StarId,
            isStory = session.IsStory,
            targetClientCount = session.TargetClientIds.Count,
            reason = session.Reason,
            startedAtUtc = session.StartedAtUtc,
            sentAtUtc = DateTimeOffset.UtcNow
        }, _jsonOptions));
        return await BroadcastFrameAsync(FrameStatus, payload, cancellationToken, ResolveLiveStreamTargets(session)).ConfigureAwait(false);
    }

    public void Start(int port)
    {
        if (_listener != null) return;
        Port = port;
        _cts = new CancellationTokenSource();
        _listener = CreateDualStackListener(port);
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        PublishStatus("Phone audio socket listening on port " + port + ".");
    }

    private static TcpListener CreateDualStackListener(int port)
    {
        try
        {
            TcpListener listener = new TcpListener(IPAddress.IPv6Any, port);
            listener.Server.DualMode = true;
            return listener;
        }
        catch (SocketException)
        {
            return new TcpListener(IPAddress.Any, port);
        }
        catch (NotSupportedException)
        {
            return new TcpListener(IPAddress.Any, port);
        }
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

    public async Task<MobileAudioSendResult> SendVoiceChunkAsync(VoiceSynthesisResult result, int chunkIndex, int totalChunks, CancellationToken cancellationToken, MobileAudioStreamSession? streamSession = null, MobileAudioFrameContext? frameContext = null)
    {
        if (result == null || result.WavAudio == null || result.WavAudio.Length == 0)
        {
            return MobileAudioSendResult.Empty(ClientCount);
        }

        byte[] wavCopy = CopyBytes(result.WavAudio);
        string checksum = Sha256Hex(wavCopy);
        string spokenText = result.Instructions ?? string.Empty;
        string textHash = string.IsNullOrEmpty(spokenText) ? string.Empty : Sha256Hex(Encoding.UTF8.GetBytes(spokenText));
        if (textHash.Length > 12) textHash = textHash.Substring(0, 12);
        MobileSpeechActivityResult speechMarkers = MobileAudioSpeechActivityMarkers.AnalyzeWav(wavCopy);
        int audioSequence = streamSession?.NextAudioSequence() ?? chunkIndex;
        string header = JsonSerializer.Serialize(new
        {
            chunkIndex,
            totalChunks,
            audioSequence,
            streamId = streamSession?.StreamId ?? string.Empty,
            streamOrdinal = streamSession?.StreamOrdinal ?? 0L,
            sessionId = streamSession?.SessionId ?? string.Empty,
            starId = result.StarId ?? streamSession?.StarId ?? string.Empty,
            voiceId = result.VoiceId ?? string.Empty,
            mimeType = string.IsNullOrWhiteSpace(result.MimeType) ? "audio/wav" : result.MimeType,
            bytes = wavCopy.Length,
            sha256 = checksum,
            sha256Short = checksum.Length > 12 ? checksum.Substring(0, 12) : checksum,
            durationMs = result.DurationMs,
            continuousPcm = result.IsStreamingPcmWindow,
            textChars = spokenText.Length,
            textKey = textHash,
            source = result.Source ?? string.Empty,
            isStory = streamSession?.IsStory ?? frameContext?.IsStory ?? false,
            storyId = frameContext?.StoryId ?? string.Empty,
            storyTitle = frameContext?.StoryTitle ?? string.Empty,
            storyPartIndex = frameContext?.StoryPartIndex ?? 0,
            storyChunkCount = frameContext?.StoryChunkCount ?? 0,
            ttsPartIndex = frameContext?.TtsPartIndex ?? chunkIndex,
            ttsTotalParts = frameContext?.TtsTotalParts ?? totalChunks,
            speechActivityVersion = speechMarkers.Count > 0 ? 1 : 0,
            speechActivitySegmentsMs = speechMarkers.Segments,
            speechActivitySegmentCount = speechMarkers.Count,
            speechActivityFrameMs = speechMarkers.FrameMs,
            speechActivityPeak = Math.Round(speechMarkers.Peak, 4),
            speechActivityHighThreshold = Math.Round(speechMarkers.HighThreshold, 4),
            streamStartedAtUtc = streamSession?.StartedAtUtc,
            sentAtUtc = DateTimeOffset.UtcNow
        }, _jsonOptions);

        byte[] headerBytes = Encoding.UTF8.GetBytes(header);
        byte[] payload = new byte[4 + headerBytes.Length + wavCopy.Length];
        WriteInt32BigEndian(payload, 0, headerBytes.Length);
        Buffer.BlockCopy(headerBytes, 0, payload, 4, headerBytes.Length);
        Buffer.BlockCopy(wavCopy, 0, payload, 4 + headerBytes.Length, wavCopy.Length);

        IReadOnlyCollection<int>? targets = await ResolveLiveStreamTargetsWithGraceAsync(streamSession, cancellationToken).ConfigureAwait(false);
        MobileAudioSendResult sent = await BroadcastFrameAsync(FrameAudioWav, payload, cancellationToken, targets).ConfigureAwait(false);
        if (streamSession == null || sent.SentClients > 0) return sent;

        // A socket can disappear after target resolution but before the frame write. Give the
        // same phone time to reconnect, rebind the stream when it is the sole client, and retry
        // this exact WAV once so a transient route drop does not remove a sentence.
        targets = await ResolveLiveStreamTargetsWithGraceAsync(streamSession, cancellationToken).ConfigureAwait(false);
        return await BroadcastFrameAsync(FrameAudioWav, payload, cancellationToken, targets).ConfigureAwait(false);
    }

    private async Task<IReadOnlyCollection<int>?> ResolveLiveStreamTargetsWithGraceAsync(MobileAudioStreamSession? session, CancellationToken cancellationToken)
    {
        if (session == null) return null;
        long deadline = Environment.TickCount64 + StreamReconnectGraceMilliseconds;
        while (true)
        {
            IReadOnlyCollection<int> targets = ResolveLiveStreamTargets(session);
            if (targets.Count > 0 || Environment.TickCount64 >= deadline) return targets;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    private IReadOnlyCollection<int> ResolveLiveStreamTargets(MobileAudioStreamSession session)
    {
        List<int> liveTargets;
        int reboundClientId = 0;
        lock (_gate)
        {
            HashSet<int> requested = new HashSet<int>(session.TargetClientIds);
            liveTargets = _clients.Where(x => requested.Contains(x.Id)).Select(x => x.Id).ToList();
            if (liveTargets.Count == 0 && _clients.Count == 1)
            {
                reboundClientId = _clients[0].Id;
                session.TargetClientIds.Clear();
                session.TargetClientIds.Add(reboundClientId);
                liveTargets.Add(reboundClientId);
            }
        }

        if (reboundClientId != 0)
        {
            PublishStatus("Rebound Android voice stream " + session.StreamOrdinal + " to reconnected phone audio client " + reboundClientId + ".");
        }
        return liveTargets;
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

    private async Task<MobileAudioSendResult> BroadcastFrameAsync(byte frameType, byte[] payload, CancellationToken cancellationToken, IReadOnlyCollection<int>? targetClientIds = null)
    {
        HashSet<int>? targetSet = targetClientIds == null ? null : new HashSet<int>(targetClientIds);
        List<ClientConnection> snapshot;
        lock (_gate) snapshot = targetSet == null
            ? new List<ClientConnection>(_clients)
            : _clients.Where(x => targetSet.Contains(x.Id)).ToList();
        if (snapshot.Count == 0)
        {
            return MobileAudioSendResult.Empty(ClientCount);
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


    private static byte[] CopyBytes(byte[]? source)
    {
        if (source == null || source.Length == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] copy = new byte[source.Length];
        Buffer.BlockCopy(source, 0, copy, 0, source.Length);
        return copy;
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

public sealed class MobileAudioStreamSession
{
    private int _nextAudioSequence;

    public string StreamId { get; set; } = string.Empty;
    public long StreamOrdinal { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string StarId { get; set; } = string.Empty;
    public bool IsStory { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public List<int> TargetClientIds { get; set; } = new List<int>();

    public int NextAudioSequence() => Interlocked.Increment(ref _nextAudioSequence) - 1;
}

public sealed class MobileAudioFrameContext
{
    public bool IsStory { get; set; }
    public string StoryId { get; set; } = string.Empty;
    public string StoryTitle { get; set; } = string.Empty;
    public int StoryPartIndex { get; set; }
    public int StoryChunkCount { get; set; }
    public int TtsPartIndex { get; set; }
    public int TtsTotalParts { get; set; }
}

public sealed class MobileAudioSendResult
{
    public int ConnectedClients { get; set; }
    public int SentClients { get; set; }
    public int FailedClients { get; set; }
    public int BytesSent { get; set; }

    public static MobileAudioSendResult Empty(int connectedClients) => new MobileAudioSendResult { ConnectedClients = connectedClients };
}
