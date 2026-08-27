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
using Wds.SixStars.Api.Mobile;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.WorldEvents;
using Wds.SixStars.Api.SpeechIntents;
using Wds.SixStars.Api.PreparedSpeech;

namespace Wds.SixStars.Ui.Networking;

public sealed class MobileBridgeHttpServer : IDisposable
{
    private readonly Func<MobileBridgeRequest, CancellationToken, Task<MobileBridgeResponse>> _messageHandler;
    private readonly Func<MobileBridgeRequest, CancellationToken, Task<MobileBridgeResponse>> _audioHandler;
    private readonly Func<CancellationToken, Task<object>> _testAudioHandler;
    private readonly Func<string, string, CancellationToken, Task<byte[]>> _standaloneLocalTtsHandler;
    private readonly Func<object> _healthFactory;
    private readonly Func<object> _starsFactory;
    private readonly Func<string, CancellationToken, Task<object>> _stateFactory;
    private readonly Func<string, int, CancellationToken, Task<object>>? _conversationArchiveFactory;
    private readonly string _conversationArchiveRoot;
    private readonly string _conversationArchivePrefix;
    private readonly bool _conversationArchiveEnabled;
    private readonly MobileBridgeDispatcher _dispatcher;
    private readonly SixStarsRuntime? _runtime;
    private readonly string _worldEventApiKey;
    private readonly JsonSerializerOptions _jsonOptions;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private readonly object _webSocketGate = new object();
    private readonly SemaphoreSlim _eventBroadcastLock = new SemaphoreSlim(1, 1);
    private readonly List<MobileBridgeWebSocketConnection> _webSocketClients = new List<MobileBridgeWebSocketConnection>();
    private int _lastWebSocketClientId;
    private bool _disposed;

    public event EventHandler<string>? StatusChanged;

    public int Port { get; private set; }
    public bool IsRunning => _listener != null;
    public int WebSocketClientCount { get { lock (_webSocketGate) return _webSocketClients.Count; } }

    public MobileBridgeHttpServer(
        int port,
        Func<MobileBridgeRequest, CancellationToken, Task<MobileBridgeResponse>> messageHandler,
        Func<MobileBridgeRequest, CancellationToken, Task<MobileBridgeResponse>> audioHandler,
        Func<CancellationToken, Task<object>> testAudioHandler,
        Func<string, string, CancellationToken, Task<byte[]>> standaloneLocalTtsHandler,
        Func<object> healthFactory,
        Func<object> starsFactory,
        Func<string, CancellationToken, Task<object>> stateFactory,
        MobileBridgeDispatcher dispatcher,
        Func<string, int, CancellationToken, Task<object>>? conversationArchiveFactory = null,
        string? conversationArchiveRoot = null,
        string? conversationArchivePrefix = null,
        bool conversationArchiveEnabled = true,
        SixStarsRuntime? runtime = null,
        string? worldEventApiKey = null)
    {
        Port = port;
        _messageHandler = messageHandler;
        _audioHandler = audioHandler;
        _testAudioHandler = testAudioHandler;
        _standaloneLocalTtsHandler = standaloneLocalTtsHandler;
        _healthFactory = healthFactory;
        _starsFactory = starsFactory;
        _stateFactory = stateFactory;
        _conversationArchiveFactory = conversationArchiveFactory;
        _conversationArchiveRoot = string.IsNullOrWhiteSpace(conversationArchiveRoot)
            ? Path.Combine(AppContext.BaseDirectory, "Content", "WebArchive")
            : Path.GetFullPath(conversationArchiveRoot.Trim());
        _conversationArchivePrefix = NormalizePrefix(conversationArchivePrefix);
        _conversationArchiveEnabled = conversationArchiveEnabled;
        _runtime = runtime;
        _worldEventApiKey = worldEventApiKey ?? string.Empty;
        _dispatcher = dispatcher;
        _dispatcher.EventPublished += OnDispatcherEventPublished;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    public void Start()
    {
        if (_listener != null) return;
        _cts = new CancellationTokenSource();
        _listener = CreateDualStackListener(Port);
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        PublishStatus("Mobile HTTP/WebSocket bridge listening on port " + Port + ".");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;

        List<MobileBridgeWebSocketConnection> sockets;
        lock (_webSocketGate)
        {
            sockets = new List<MobileBridgeWebSocketConnection>(_webSocketClients);
            _webSocketClients.Clear();
        }
        foreach (MobileBridgeWebSocketConnection socket in sockets)
        {
            try { socket.Dispose(); } catch { }
        }

        _cts?.Dispose();
        _cts = null;
        PublishStatus("Mobile HTTP/WebSocket bridge stopped.");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? accepted = null;
            try
            {
                TcpListener listener = _listener ?? throw new ObjectDisposedException(nameof(MobileBridgeHttpServer));
                accepted = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                accepted.NoDelay = true;
                TcpClient clientForTask = accepted;
                accepted = null;
                _ = Task.Run(() => HandleClientAsync(clientForTask, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Debug.WriteLine("[MobileBridge] Accept error: " + ex);
                PublishStatus("Mobile bridge accept error: " + ex.Message);
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                accepted?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 180000;
                client.SendTimeout = 180000;
                NetworkStream stream = client.GetStream();
                HttpRequest request = await ReadRequestAsync(stream, serverToken).ConfigureAwait(false);

                if (request.Method == "GET" && request.Path == "/api/mobile/socket" && IsWebSocketUpgrade(request))
                {
                    await HandleWebSocketClientAsync(client, stream, request, serverToken).ConfigureAwait(false);
                    return;
                }

                if (request.Method == "GET" && request.Path == "/health")
                {
                    await WriteJsonAsync(stream, 200, CreateConnectionAwareHealthPayload(MobileBridgeNetworkAddressDiscovery.Discover(client, Port)), serverToken).ConfigureAwait(false);
                    return;
                }


                if (_conversationArchiveEnabled && request.Method == "GET" && request.Path == "/api/archive/data")
                {
                    if (_conversationArchiveFactory == null)
                    {
                        await WriteJsonAsync(stream, 500, new { ok = false, error = "Conversation archive data factory is not configured." }, serverToken).ConfigureAwait(false);
                        return;
                    }

                    string sessionId = request.Query.TryGetValue("sessionId", out string? sessionText) && !string.IsNullOrWhiteSpace(sessionText)
                        ? sessionText.Trim()
                        : "android-phone-prod";
                    int maxLogs = 160;
                    if (request.Query.TryGetValue("maxLogs", out string? maxLogsText)) int.TryParse(maxLogsText, out maxLogs);
                    object response = await _conversationArchiveFactory(sessionId, maxLogs, serverToken).ConfigureAwait(false);
                    await WriteJsonAsync(stream, 200, response, serverToken).ConfigureAwait(false);
                    return;
                }

                if (_conversationArchiveEnabled && request.Method == "GET" && IsConversationArchivePath(request.Path))
                {
                    await WriteConversationArchiveFileAsync(stream, request.Path, serverToken).ConfigureAwait(false);
                    return;
                }

                if (request.Method == "POST" && request.Path == "/api/mobile/local-tts")
                {
                    using JsonDocument body = JsonDocument.Parse(request.Body);
                    string starId = GetJsonString(body.RootElement, "starId");
                    string text = GetJsonString(body.RootElement, "text");
                    if (string.IsNullOrWhiteSpace(starId)) starId = "Elusiaa";
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        await WriteJsonAsync(stream, 400, new { ok = false, error = "text is required" }, serverToken).ConfigureAwait(false);
                        return;
                    }
                    byte[] wav = await _standaloneLocalTtsHandler(starId, text, serverToken).ConfigureAwait(false);
                    await WriteBytesAsync(stream, 200, "audio/wav", wav, serverToken).ConfigureAwait(false);
                    return;
                }

                if ((request.Method == "GET" || request.Method == "POST") && request.Path == "/api/mobile/test-audio")
                {
                    object response = await _testAudioHandler(serverToken).ConfigureAwait(false);
                    await WriteJsonAsync(stream, 200, response, serverToken).ConfigureAwait(false);
                    return;
                }

                if (request.Method == "GET" && request.Path == "/api/mobile/stars")
                {
                    await WriteJsonAsync(stream, 200, _starsFactory(), serverToken).ConfigureAwait(false);
                    return;
                }

                if (request.Method == "GET" && request.Path == "/api/mobile/state")
                {
                    string sessionId = request.Query.TryGetValue("sessionId", out string? sessionText) && !string.IsNullOrWhiteSpace(sessionText)
                        ? sessionText.Trim()
                        : "android-phone-dev";
                    object response = await _stateFactory(sessionId, serverToken).ConfigureAwait(false);
                    await WriteJsonAsync(stream, 200, response, serverToken).ConfigureAwait(false);
                    return;
                }

                if (request.Method == "GET" && request.Path == "/api/mobile/events")
                {
                    long after = 0;
                    int max = 50;
                    if (request.Query.TryGetValue("after", out string? afterText)) long.TryParse(afterText, out after);
                    if (request.Query.TryGetValue("max", out string? maxText)) int.TryParse(maxText, out max);
                    await WriteJsonAsync(stream, 200, _dispatcher.GetEventsAfter(after, max), serverToken).ConfigureAwait(false);
                    return;
                }

                if (request.Method == "POST" && request.Path == "/api/mobile/dispatch")
                {
                    MobileBridgeDispatchCommand? command = JsonSerializer.Deserialize<MobileBridgeDispatchCommand>(request.Body, _jsonOptions);
                    MobileBridgeDispatchResult response = await _dispatcher.DispatchAsync(command ?? new MobileBridgeDispatchCommand(), serverToken).ConfigureAwait(false);
                    try
                    {
                        await WriteJsonAsync(stream, response.Ok ? 200 : 500, response, serverToken).ConfigureAwait(false);
                        response.StartAfterTransportResponse();
                    }
                    catch
                    {
                        response.StartTransportFailureCleanup();
                        throw;
                    }
                    return;
                }

                if (request.Method == "POST" && request.Path == "/api/mobile/message")
                {
                    MobileBridgeRequest? mobileRequest = JsonSerializer.Deserialize<MobileBridgeRequest>(request.Body, _jsonOptions);
                    MobileBridgeResponse response = await _messageHandler(mobileRequest ?? new MobileBridgeRequest(), serverToken).ConfigureAwait(false);
                    try
                    {
                        await WriteJsonAsync(stream, response.Ok ? 200 : 500, response, serverToken).ConfigureAwait(false);
                        response.StartAfterTransportResponse();
                    }
                    catch
                    {
                        response.StartTransportFailureCleanup();
                        throw;
                    }
                    return;
                }

                if (request.Method == "POST" && request.Path == "/api/mobile/audio")
                {
                    MobileBridgeRequest? mobileRequest = JsonSerializer.Deserialize<MobileBridgeRequest>(request.Body, _jsonOptions);
                    MobileBridgeResponse response = await _audioHandler(mobileRequest ?? new MobileBridgeRequest(), serverToken).ConfigureAwait(false);
                    try
                    {
                        await WriteJsonAsync(stream, response.Ok ? 200 : 500, response, serverToken).ConfigureAwait(false);
                        response.StartAfterTransportResponse();
                    }
                    catch
                    {
                        response.StartTransportFailureCleanup();
                        throw;
                    }
                    return;
                }

                if (_runtime != null && request.Method == "GET" && request.Path == "/api/world-events")
                {
                    long after = 0;
                    int max = 100;
                    string starId = request.Query.TryGetValue("starId", out string? starValue) ? starValue : string.Empty;
                    if (request.Query.TryGetValue("afterSequence", out string? afterValue)) long.TryParse(afterValue, out after);
                    if (request.Query.TryGetValue("max", out string? maxValue)) int.TryParse(maxValue, out max);
                    await WriteJsonAsync(stream, 200, await _runtime.GetWorldEventsAsync(after, max, starId, serverToken).ConfigureAwait(false), serverToken).ConfigureAwait(false);
                    return;
                }

                if (_runtime != null && request.Method == "POST" && (request.Path == "/api/world-events" || request.Path == "/api/world-events/batch"))
                {
                    if (!IsAuthorizedWorldEventRequest(request))
                    {
                        await WriteJsonAsync(stream, 401, new { ok = false, error = "Invalid world-event API key." }, serverToken).ConfigureAwait(false);
                        return;
                    }
                    List<WorldEventRecord> events = ParseWorldEvents(request.Body);
                    WorldEventInsertResult result = await _runtime.AddWorldEventsAsync(events, serverToken).ConfigureAwait(false);
                    result.StateRefreshQueued = result.AcceptedEvents.Any(x => x.RequiresStateRefresh);
                    result.AffectedStars = result.AcceptedEvents.SelectMany(x => x.TargetStars ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    await WriteJsonAsync(stream, result.Ok ? 200 : 500, result, serverToken).ConfigureAwait(false);
                    return;
                }

                if (_runtime != null && request.Method == "POST"
                    && (request.Path.Equals("/api/star-state/refresh", StringComparison.OrdinalIgnoreCase)
                        || request.Path.StartsWith("/api/star-state/refresh/", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!IsAuthorizedWorldEventRequest(request))
                    {
                        await WriteJsonAsync(stream, 401, new { ok = false, error = "Invalid world-event API key." }, serverToken).ConfigureAwait(false);
                        return;
                    }
                    StateRefreshRequest refresh = ParseStateRefreshRequest(request);
                    await _runtime.TriggerDailyStateRefreshAsync(refresh.SessionId, refresh.StarIds, refresh.Reason, serverToken).ConfigureAwait(false);
                    await WriteJsonAsync(stream, 200, new { ok = true, stateRefreshQueued = true, refresh.SessionId, refresh.StarIds, refresh.Reason }, serverToken).ConfigureAwait(false);
                    return;
                }

                if (_runtime != null && request.Method == "GET" && request.Path == "/api/speech-intents/manifest")
                {
                    string starId = request.Query.TryGetValue("starId", out string? value) ? value : string.Empty;
                    await WriteJsonAsync(stream, 200, await _runtime.GetSpeechIntentManifestAsync(starId, serverToken).ConfigureAwait(false), serverToken).ConfigureAwait(false);
                    return;
                }

                if (_runtime != null && request.Method == "GET" && request.Path == "/api/prepared-speech/manifest")
                {
                    string starId = request.Query.TryGetValue("starId", out string? value) ? value : string.Empty;
                    PreparedSpeechManifest prepared = await _runtime.GetPreparedSpeechManifestAsync(starId, serverToken).ConfigureAwait(false);
                    StarSpeechIntentManifest intents = await _runtime.GetSpeechIntentManifestAsync(starId, serverToken).ConfigureAwait(false);
                    Dictionary<string, string> currentArtifactsByIntent = intents.Intents
                        .Where(x => !string.IsNullOrWhiteSpace(x.IntentId) && !string.IsNullOrWhiteSpace(x.PreparedArtifactId))
                        .GroupBy(x => x.IntentId, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => x.First().PreparedArtifactId, StringComparer.OrdinalIgnoreCase);
                    List<PreparedSpeechArtifact> currentArtifacts = prepared.Artifacts
                        .Where(x => !string.IsNullOrWhiteSpace(x.IntentId)
                            && currentArtifactsByIntent.TryGetValue(x.IntentId, out string? expectedArtifactId)
                            && string.Equals(expectedArtifactId, x.ArtifactId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var policy = _runtime.ForegroundSpeechStyle;
                    var intentPolicy = _runtime.SpeechIntentOptions;
                    await WriteJsonAsync(stream, 200, new
                    {
                        ok = prepared.Ok && intents.Ok,
                        generatedAt = DateTimeOffset.UtcNow,
                        artifacts = currentArtifacts,
                        intents = intents.Intents,
                        highestEffectiveScore = intents.HighestEffectiveScore,
                        highestPriority = intents.HighestPriority,
                        clientPolicy = new
                        {
                            policy.SpeakBeforeReplyEnabled,
                            policy.SpeakBeforeReplyProbability,
                            policy.HighPrioritySpeakBeforeReplyProbability,
                            policy.SpeakBeforeReplyMaximumSeconds,
                            policy.MinimumPageDwellSeconds,
                            policy.MediumPriorityPageDwellSeconds,
                            policy.GirlfriendMediumSpeakProbability,
                            policy.HighConnectionMediumSpeakProbability,
                            policy.HighConnectionThreshold,
                            policy.MinimumMinutesBetweenAutoSpeech,
                            intentPolicy.HighPriorityAutoSpeakThreshold,
                            intentPolicy.MediumPriorityAutoSpeakThreshold
                        }
                    }, serverToken).ConfigureAwait(false);
                    return;
                }

                if (_runtime != null && request.Method == "GET" && TryParsePreparedSpeechChunkPath(request.Path, out string artifactId, out int chunkIndex))
                {
                    byte[]? bytes = await _runtime.ReadPreparedSpeechChunkAsync(artifactId, chunkIndex, serverToken).ConfigureAwait(false);
                    if (bytes == null)
                    {
                        await WriteJsonAsync(stream, 404, new { ok = false, error = "Prepared speech chunk not found.", artifactId, chunkIndex }, serverToken).ConfigureAwait(false);
                        return;
                    }
                    await WriteBytesAsync(stream, 200, "audio/wav", bytes, serverToken).ConfigureAwait(false);
                    return;
                }

                if (_runtime != null && request.Method == "POST" && TryParsePreparedSpeechActionPath(request.Path, out string intentId, out string action))
                {
                    StarSpeechIntent? intent = action switch
                    {
                        "lease" => await _runtime.LeaseSpeechIntentAsync(intentId, serverToken).ConfigureAwait(false),
                        "started" => await _runtime.MarkSpeechIntentPlaybackStartedAsync(intentId, serverToken).ConfigureAwait(false),
                        "completed" => await _runtime.MarkSpeechIntentSpokenAsync(intentId, serverToken).ConfigureAwait(false),
                        "dismissed" => await _runtime.DismissSpeechIntentAsync(intentId, serverToken).ConfigureAwait(false),
                        _ => null
                    };
                    await WriteJsonAsync(stream, intent == null ? 404 : 200, new { ok = intent != null, intent, action }, serverToken).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(stream, 404, new { ok = false, error = "Unknown route", request.Method, request.Path }, serverToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MobileBridge] Request error: " + ex);
                try
                {
                    await WriteJsonAsync(client.GetStream(), 500, new { ok = false, error = ex.Message }, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
                PublishStatus("Mobile bridge request error: " + ex.Message);
            }
        }
    }

    private static bool IsWebSocketUpgrade(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Upgrade", out string? upgrade)
            || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)) return false;
        if (!request.Headers.TryGetValue("Connection", out string? connection)
            || connection.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) < 0) return false;
        return request.Headers.ContainsKey("Sec-WebSocket-Key");
    }

    private async Task HandleWebSocketClientAsync(TcpClient client, NetworkStream stream, HttpRequest request, CancellationToken serverToken)
    {
        if (!request.Headers.TryGetValue("Sec-WebSocket-Key", out string? key) || string.IsNullOrWhiteSpace(key))
        {
            await WriteJsonAsync(stream, 400, new { ok = false, error = "Missing Sec-WebSocket-Key." }, serverToken).ConfigureAwait(false);
            return;
        }
        if (!request.Headers.TryGetValue("Sec-WebSocket-Version", out string? version) || version.Trim() != "13")
        {
            await WriteWebSocketVersionRequiredAsync(stream, serverToken).ConfigureAwait(false);
            return;
        }

        string acceptSource = key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        byte[] acceptHash;
        using (SHA1 sha1 = SHA1.Create()) acceptHash = sha1.ComputeHash(Encoding.ASCII.GetBytes(acceptSource));
        string accept = Convert.ToBase64String(acceptHash);
        string response = "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
        byte[] responseBytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(responseBytes, 0, responseBytes.Length, serverToken).ConfigureAwait(false);
        await stream.FlushAsync(serverToken).ConfigureAwait(false);

        client.ReceiveTimeout = 0;
        client.SendTimeout = 0;
        MobileBridgeWebSocketConnection connection = new MobileBridgeWebSocketConnection(Interlocked.Increment(ref _lastWebSocketClientId), client, Port);
        lock (_webSocketGate) _webSocketClients.Add(connection);
        PublishStatus("Android mobile WebSocket connected. Clients=" + WebSocketClientCount + ".");

        using CancellationTokenSource connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        try
        {
            long latestSequence = _dispatcher.GetEventsAfter(0, 1).LatestSequence;
            await connection.SendJsonAsync(new
            {
                type = "hello",
                protocolVersion = 1,
                clientId = connection.Id,
                latestSequence,
                connectedAtUtc = DateTimeOffset.UtcNow,
                preferredIpv6Address = connection.NetworkInfo.PreferredIpv6Address,
                preferredIpv6BridgeUrl = connection.NetworkInfo.PreferredIpv6BridgeUrl
            }, _jsonOptions, connectionCts.Token).ConfigureAwait(false);

            while (!connectionCts.IsCancellationRequested)
            {
                string? message = await connection.ReadTextMessageAsync(connectionCts.Token).ConfigureAwait(false);
                if (message == null) break;
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(message);
                }
                catch (JsonException ex)
                {
                    await connection.SendJsonAsync(new { type = "protocolError", error = "Invalid JSON: " + ex.Message }, _jsonOptions, connectionCts.Token).ConfigureAwait(false);
                    continue;
                }

                using (document)
                {
                    JsonElement root = document.RootElement;
                    string type = GetJsonString(root, "type");
                    if (type.Equals("ping", StringComparison.OrdinalIgnoreCase))
                    {
                        await connection.SendJsonAsync(new { type = "pong", sentAtUtc = DateTimeOffset.UtcNow }, _jsonOptions, connectionCts.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (type.Equals("subscribe", StringComparison.OrdinalIgnoreCase))
                    {
                        long afterSequence = GetJsonInt64(root, "afterSequence", 0L);
                        await SendMissedEventsAsync(connection, afterSequence, connectionCts.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (!type.Equals("request", StringComparison.OrdinalIgnoreCase))
                    {
                        await connection.SendJsonAsync(new { type = "protocolError", error = "Unknown message type: " + type }, _jsonOptions, connectionCts.Token).ConfigureAwait(false);
                        continue;
                    }

                    int requestId = GetJsonInt32(root, "requestId", 0);
                    string operation = GetJsonString(root, "operation");
                    JsonElement payload = root.TryGetProperty("payload", out JsonElement payloadElement)
                        ? payloadElement.Clone()
                        : JsonSerializer.SerializeToElement(new { }, _jsonOptions);
                    _ = Task.Run(() => ProcessWebSocketRequestAsync(connection, requestId, operation, payload, connectionCts.Token), CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[MobileBridgeSocket] Client error: " + ex);
        }
        finally
        {
            connectionCts.Cancel();
            RemoveWebSocketClient(connection);
        }
    }

    private async Task ProcessWebSocketRequestAsync(MobileBridgeWebSocketConnection connection, int requestId, string operation, JsonElement payload, CancellationToken cancellationToken)
    {
        if (requestId <= 0)
        {
            await SafeSendSocketAsync(connection, new { type = "response", requestId, operation, ok = false, error = "requestId must be a positive integer." }, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            SocketOperationResult result = await ExecuteSocketOperationAsync(connection, operation, payload, cancellationToken).ConfigureAwait(false);
            bool sent = await SafeSendSocketAsync(connection, new
            {
                type = "response",
                requestId,
                operation,
                ok = result.Ok,
                payload = result.Payload,
                error = result.Error,
                completedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
            if (sent)
            {
                result.MobileResponse?.StartAfterTransportResponse();
                result.DispatchResult?.StartAfterTransportResponse();
            }
            else
            {
                result.MobileResponse?.StartTransportFailureCleanup();
                result.DispatchResult?.StartTransportFailureCleanup();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await SafeSendSocketAsync(connection, new
            {
                type = "response",
                requestId,
                operation,
                ok = false,
                error = ex.Message,
                completedAtUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<SocketOperationResult> ExecuteSocketOperationAsync(MobileBridgeWebSocketConnection connection, string operation, JsonElement payload, CancellationToken cancellationToken)
    {
        string normalized = (operation ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "message":
            {
                MobileBridgeRequest request = JsonSerializer.Deserialize<MobileBridgeRequest>(payload.GetRawText(), _jsonOptions) ?? new MobileBridgeRequest();
                request.DeferConversationResponseEventUntilTransportFailure = true;
                MobileBridgeResponse response = await _messageHandler(request, cancellationToken).ConfigureAwait(false);
                return new SocketOperationResult(response.Ok, response, response.Ok ? string.Empty : GetMobileResponseError(response), response);
            }
            case "audio":
            {
                MobileBridgeRequest request = JsonSerializer.Deserialize<MobileBridgeRequest>(payload.GetRawText(), _jsonOptions) ?? new MobileBridgeRequest();
                request.DeferConversationResponseEventUntilTransportFailure = true;
                MobileBridgeResponse response = await _audioHandler(request, cancellationToken).ConfigureAwait(false);
                return new SocketOperationResult(response.Ok, response, response.Ok ? string.Empty : GetMobileResponseError(response), response);
            }
            case "dispatch":
            {
                MobileBridgeDispatchCommand command = JsonSerializer.Deserialize<MobileBridgeDispatchCommand>(payload.GetRawText(), _jsonOptions) ?? new MobileBridgeDispatchCommand();
                MobileBridgeDispatchResult response = await _dispatcher.DispatchAsync(command, cancellationToken).ConfigureAwait(false);
                return new SocketOperationResult(response.Ok, response, response.Ok ? string.Empty : response.Error, dispatchResult: response);
            }
            case "state":
            {
                string sessionId = GetJsonString(payload, "sessionId");
                object response = await _stateFactory(string.IsNullOrWhiteSpace(sessionId) ? "android-phone-dev" : sessionId, cancellationToken).ConfigureAwait(false);
                return new SocketOperationResult(true, response, string.Empty);
            }
            case "health":
                return new SocketOperationResult(true, CreateConnectionAwareHealthPayload(connection.NetworkInfo), string.Empty);
            case "stars":
                return new SocketOperationResult(true, _starsFactory(), string.Empty);
            default:
                return new SocketOperationResult(false, new { }, "Unknown mobile WebSocket operation: " + operation);
        }
    }

    private object CreateConnectionAwareHealthPayload(MobileBridgeConnectionNetworkInfo networkInfo)
    {
        Dictionary<string, object?> payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        JsonElement basePayload = JsonSerializer.SerializeToElement(_healthFactory(), _jsonOptions);
        if (basePayload.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in basePayload.EnumerateObject()) payload[property.Name] = property.Value.Clone();
        }

        payload["connectionLocalAddress"] = networkInfo.LocalAddress;
        payload["connectionRemoteAddress"] = networkInfo.RemoteAddress;
        payload["connectionAddressFamily"] = networkInfo.AddressFamily;
        payload["localAdapterName"] = networkInfo.AdapterName;
        payload["localAdapterDescription"] = networkInfo.AdapterDescription;
        payload["localConnection"] = networkInfo.IsLocalConnection;
        payload["ipv6Available"] = networkInfo.HasPreferredIpv6;
        payload["preferredIpv6Address"] = networkInfo.PreferredIpv6Address;
        payload["preferredIpv6Host"] = networkInfo.PreferredIpv6Host;
        payload["preferredIpv6AudioHost"] = networkInfo.PreferredIpv6Host;
        payload["preferredIpv6BridgeUrl"] = networkInfo.PreferredIpv6BridgeUrl;
        payload["preferredIpv6Addresses"] = networkInfo.PreferredIpv6Addresses;
        payload["addressDiscoverySource"] = networkInfo.DiscoverySource;
        return payload;
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

    private async Task SendMissedEventsAsync(MobileBridgeWebSocketConnection connection, long afterSequence, CancellationToken cancellationToken)
    {
        await _eventBroadcastLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MobileBridgeEventBatch batch = _dispatcher.GetEventsAfter(afterSequence, 100);
            foreach (MobileBridgeDispatchEvent evt in batch.Events)
            {
                await connection.SendJsonAsync(new { type = "event", sequence = evt.Sequence, payload = evt }, _jsonOptions, cancellationToken).ConfigureAwait(false);
            }
            await connection.SendJsonAsync(new { type = "subscribed", afterSequence, latestSequence = batch.LatestSequence }, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _eventBroadcastLock.Release();
        }
    }

    private void OnDispatcherEventPublished(object? sender, MobileBridgeDispatchEvent evt)
    {
        _ = BroadcastDispatcherEventAsync(evt);
    }

    private async Task BroadcastDispatcherEventAsync(MobileBridgeDispatchEvent evt)
    {
        await _eventBroadcastLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            List<MobileBridgeWebSocketConnection> clients;
            lock (_webSocketGate) clients = new List<MobileBridgeWebSocketConnection>(_webSocketClients);
            foreach (MobileBridgeWebSocketConnection connection in clients)
            {
                try
                {
                    await connection.SendJsonAsync(new { type = "event", sequence = evt.Sequence, payload = evt }, _jsonOptions, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    RemoveWebSocketClient(connection);
                }
            }
        }
        finally
        {
            _eventBroadcastLock.Release();
        }
    }

    private async Task<bool> SafeSendSocketAsync(MobileBridgeWebSocketConnection connection, object value, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendJsonAsync(value, _jsonOptions, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            RemoveWebSocketClient(connection);
            return false;
        }
    }

    private void RemoveWebSocketClient(MobileBridgeWebSocketConnection connection)
    {
        bool removed;
        lock (_webSocketGate) removed = _webSocketClients.Remove(connection);
        connection.Dispose();
        if (removed) PublishStatus("Android mobile WebSocket disconnected. Clients=" + WebSocketClientCount + ".");
    }

    private static string GetMobileResponseError(MobileBridgeResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.VoiceError)) return response.VoiceError;
        string note = response.Notes.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        return string.IsNullOrWhiteSpace(note) ? "Desktop mobile bridge request failed." : note;
    }

    private static async Task WriteWebSocketVersionRequiredAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        const string bodyText = "WebSocket version 13 is required.";
        byte[] body = Encoding.UTF8.GetBytes(bodyText);
        string header = "HTTP/1.1 426 Upgrade Required\r\n"
            + "Sec-WebSocket-Version: 13\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + "Connection: close\r\n"
            + "Content-Length: " + body.Length + "\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object) return string.Empty;
        if (element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        string alternate = char.ToUpperInvariant(propertyName[0]) + propertyName.Substring(1);
        if (element.TryGetProperty(alternate, out value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static int GetJsonInt32(JsonElement element, string propertyName, int fallback)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt32(out int result)) return result;
        return fallback;
    }

    private static long GetJsonInt64(JsonElement element, string propertyName, long fallback)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt64(out long result)) return result;
        return fallback;
    }

    private sealed class SocketOperationResult
    {
        public SocketOperationResult(
            bool ok,
            object payload,
            string error,
            MobileBridgeResponse? mobileResponse = null,
            MobileBridgeDispatchResult? dispatchResult = null)
        {
            Ok = ok;
            Payload = payload;
            Error = error ?? string.Empty;
            MobileResponse = mobileResponse;
            DispatchResult = dispatchResult;
        }

        public bool Ok { get; }
        public object Payload { get; }
        public string Error { get; }
        public MobileBridgeResponse? MobileResponse { get; }
        public MobileBridgeDispatchResult? DispatchResult { get; }
    }

    private async Task<HttpRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] headerBytes = await ReadHeaderBytesAsync(stream, cancellationToken).ConfigureAwait(false);
        string headerText = Encoding.ASCII.GetString(headerBytes);
        string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        string[] first = lines[0].Split(' ');
        if (first.Length < 2) throw new InvalidDataException("Invalid HTTP request line.");

        Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
        }

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? lengthText))
        {
            int.TryParse(lengthText, out contentLength);
        }

        byte[] bodyBytes = contentLength > 0
            ? await ReadExactlyAsync(stream, contentLength, cancellationToken).ConfigureAwait(false)
            : Array.Empty<byte>();

        string rawPath = first[1];
        string path = rawPath;
        Dictionary<string, string> query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int question = path.IndexOf('?');
        if (question >= 0)
        {
            string queryText = path.Substring(question + 1);
            path = path.Substring(0, question);
            foreach (string pair in queryText.Split('&'))
            {
                if (string.IsNullOrWhiteSpace(pair)) continue;
                int equals = pair.IndexOf('=');
                string key = equals >= 0 ? pair.Substring(0, equals) : pair;
                string value = equals >= 0 ? pair.Substring(equals + 1) : string.Empty;
                if (key.Length == 0) continue;
                query[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace("+", " "));
            }
        }

        return new HttpRequest
        {
            Method = first[0].ToUpperInvariant(),
            Path = path,
            Query = query,
            Headers = headers,
            Body = Encoding.UTF8.GetString(bodyBytes)
        };
    }

    private static async Task<byte[]> ReadHeaderBytesAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        List<byte> bytes = new List<byte>(2048);
        byte[] one = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(one, 0, 1, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Client closed before HTTP headers finished.");
            bytes.Add(one[0]);
            int n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
            {
                break;
            }
            if (bytes.Count > 64 * 1024) throw new InvalidDataException("HTTP headers too large.");
        }
        return bytes.ToArray();
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer, offset, length - offset, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Client closed before HTTP body finished.");
            offset += read;
        }
        return buffer;
    }

    private bool IsAuthorizedWorldEventRequest(HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(_worldEventApiKey)) return true;
        if (request.Headers.TryGetValue("Authorization", out string? authorization)
            && string.Equals(authorization.Trim(), "Bearer " + _worldEventApiKey.Trim(), StringComparison.Ordinal)) return true;
        return request.Headers.TryGetValue("X-Six-Stars-Key", out string? key)
            && string.Equals(key.Trim(), _worldEventApiKey.Trim(), StringComparison.Ordinal);
    }

    private List<WorldEventRecord> ParseWorldEvents(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new List<WorldEventRecord>();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<WorldEventRecord>>(root.GetRawText(), _jsonOptions) ?? new List<WorldEventRecord>();
        }
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "events", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
                {
                    return JsonSerializer.Deserialize<List<WorldEventRecord>>(property.Value.GetRawText(), _jsonOptions) ?? new List<WorldEventRecord>();
                }
            }
        }
        WorldEventRecord? single = JsonSerializer.Deserialize<WorldEventRecord>(root.GetRawText(), _jsonOptions);
        return single == null ? new List<WorldEventRecord>() : new List<WorldEventRecord> { single };
    }

    private StateRefreshRequest ParseStateRefreshRequest(HttpRequest request)
    {
        StateRefreshRequest result = string.IsNullOrWhiteSpace(request.Body)
            ? new StateRefreshRequest()
            : JsonSerializer.Deserialize<StateRefreshRequest>(request.Body, _jsonOptions) ?? new StateRefreshRequest();
        string prefix = "/api/star-state/refresh/";
        if (request.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && request.Path.Length > prefix.Length)
        {
            result.StarIds = new List<string> { Uri.UnescapeDataString(request.Path.Substring(prefix.Length)) };
        }
        if (string.IsNullOrWhiteSpace(result.SessionId)) result.SessionId = "android-phone-prod";
        if (string.IsNullOrWhiteSpace(result.Reason)) result.Reason = "API-triggered current Star state refresh";
        return result;
    }

    private static bool TryParsePreparedSpeechChunkPath(string path, out string artifactId, out int chunkIndex)
    {
        artifactId = string.Empty;
        chunkIndex = -1;
        string prefix = "/api/prepared-speech/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string[] parts = path.Substring(prefix.Length).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !string.Equals(parts[1], "chunks", StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[2], out chunkIndex)) return false;
        artifactId = Uri.UnescapeDataString(parts[0]);
        return !string.IsNullOrWhiteSpace(artifactId) && chunkIndex >= 0;
    }

    private static bool TryParsePreparedSpeechActionPath(string path, out string intentId, out string action)
    {
        intentId = string.Empty;
        action = string.Empty;
        string prefix = "/api/prepared-speech/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string[] parts = path.Substring(prefix.Length).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        string candidate = parts[1].ToLowerInvariant();
        if (candidate != "lease" && candidate != "started" && candidate != "completed" && candidate != "dismissed") return false;
        intentId = Uri.UnescapeDataString(parts[0]);
        action = candidate;
        return !string.IsNullOrWhiteSpace(intentId);
    }

    private static async Task WriteBytesAsync(NetworkStream stream, int statusCode, string contentType, byte[] body, CancellationToken cancellationToken)
    {
        byte[] bytes = body ?? Array.Empty<byte>();
        string header = "HTTP/1.1 " + statusCode + " OK\r\n"
            + "Content-Type: " + contentType + "\r\n"
            + "Access-Control-Allow-Origin: *\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n"
            + "Content-Length: " + bytes.Length + "\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteJsonAsync(NetworkStream stream, int statusCode, object value, CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions);
        string statusText = statusCode == 200 ? "OK" : statusCode == 404 ? "Not Found" : statusCode == 401 ? "Unauthorized" : "Error";
        string header = "HTTP/1.1 " + statusCode + " " + statusText + "\r\n"
            + "Content-Type: application/json; charset=utf-8\r\n"
            + "Access-Control-Allow-Origin: *\r\n"
            + "Connection: close\r\n"
            + "Content-Length: " + body.Length + "\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }


    private bool IsConversationArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path == "/") return true;
        return path.Equals(_conversationArchivePrefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(_conversationArchivePrefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task WriteConversationArchiveFileAsync(NetworkStream stream, string requestPath, CancellationToken cancellationToken)
    {
        string relative;
        if (string.Equals(requestPath, "/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestPath, _conversationArchivePrefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestPath, _conversationArchivePrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            relative = "index.html";
        }
        else
        {
            relative = requestPath.Substring(_conversationArchivePrefix.Length).TrimStart('/');
            if (string.IsNullOrWhiteSpace(relative)) relative = "index.html";
        }

        relative = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(_conversationArchiveRoot, relative));
        string rootWithSeparator = _conversationArchiveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            await WriteJsonAsync(stream, 404, new { ok = false, error = "Archive asset not found", requestPath }, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] body = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        string header = "HTTP/1.1 200 OK\r\n"
            + "Content-Type: " + GetContentType(fullPath) + "\r\n"
            + "Access-Control-Allow-Origin: *\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n"
            + "Content-Length: " + body.Length + "\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizePrefix(string? prefix)
    {
        string value = string.IsNullOrWhiteSpace(prefix) ? "/archive" : prefix.Trim();
        if (!value.StartsWith("/", StringComparison.Ordinal)) value = "/" + value;
        return value.TrimEnd('/');
    }

    private static string GetContentType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".html": return "text/html; charset=utf-8";
            case ".css": return "text/css; charset=utf-8";
            case ".js": return "application/javascript; charset=utf-8";
            case ".json": return "application/json; charset=utf-8";
            case ".png": return "image/png";
            case ".jpg":
            case ".jpeg": return "image/jpeg";
            case ".gif": return "image/gif";
            case ".svg": return "image/svg+xml";
            case ".ico": return "image/x-icon";
            default: return "application/octet-stream";
        }
    }

    private void PublishStatus(string status) => StatusChanged?.Invoke(this, status);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dispatcher.EventPublished -= OnDispatcherEventPublished;
        Stop();
        // In-flight event broadcasts may still be unwinding after their sockets are closed. Leave
        // the lightweight semaphore for GC rather than racing a pending Release().
    }

    private sealed class StateRefreshRequest
    {
        public string SessionId { get; set; } = "android-phone-prod";
        public List<string> StarIds { get; set; } = new List<string>();
        public string Reason { get; set; } = string.Empty;
    }

    private sealed class HttpRequest
    {
        public string Method { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public Dictionary<string, string> Query { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public string Body { get; set; } = string.Empty;
    }
}
