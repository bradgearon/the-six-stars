using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Mobile;

namespace Wds.SixStars.Ui.Networking;

public sealed class MobileBridgeHttpServer : IDisposable
{
    private readonly Func<MobileBridgeRequest, CancellationToken, Task<MobileBridgeResponse>> _messageHandler;
    private readonly Func<MobileBridgeRequest, CancellationToken, Task<MobileBridgeResponse>> _audioHandler;
    private readonly Func<CancellationToken, Task<object>> _testAudioHandler;
    private readonly Func<object> _healthFactory;
    private readonly Func<object> _starsFactory;
    private readonly Func<string, CancellationToken, Task<object>> _stateFactory;
    private readonly Func<string, int, CancellationToken, Task<object>>? _conversationArchiveFactory;
    private readonly string _conversationArchiveRoot;
    private readonly string _conversationArchivePrefix;
    private readonly bool _conversationArchiveEnabled;
    private readonly MobileBridgeDispatcher _dispatcher;
    private readonly JsonSerializerOptions _jsonOptions;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;

    public event EventHandler<string>? StatusChanged;

    public int Port { get; private set; }
    public bool IsRunning => _listener != null;

    public MobileBridgeHttpServer(
        int port,
        Func<MobileBridgeRequest, CancellationToken, Task<MobileBridgeResponse>> messageHandler,
        Func<MobileBridgeRequest, CancellationToken, Task<MobileBridgeResponse>> audioHandler,
        Func<CancellationToken, Task<object>> testAudioHandler,
        Func<object> healthFactory,
        Func<object> starsFactory,
        Func<string, CancellationToken, Task<object>> stateFactory,
        MobileBridgeDispatcher dispatcher,
        Func<string, int, CancellationToken, Task<object>>? conversationArchiveFactory = null,
        string? conversationArchiveRoot = null,
        string? conversationArchivePrefix = null,
        bool conversationArchiveEnabled = true)
    {
        Port = port;
        _messageHandler = messageHandler;
        _audioHandler = audioHandler;
        _testAudioHandler = testAudioHandler;
        _healthFactory = healthFactory;
        _starsFactory = starsFactory;
        _stateFactory = stateFactory;
        _conversationArchiveFactory = conversationArchiveFactory;
        _conversationArchiveRoot = string.IsNullOrWhiteSpace(conversationArchiveRoot)
            ? Path.Combine(AppContext.BaseDirectory, "Content", "WebArchive")
            : Path.GetFullPath(conversationArchiveRoot.Trim());
        _conversationArchivePrefix = NormalizePrefix(conversationArchivePrefix);
        _conversationArchiveEnabled = conversationArchiveEnabled;
        _dispatcher = dispatcher;
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
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        PublishStatus("Mobile HTTP bridge listening on port " + Port + ".");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        PublishStatus("Mobile HTTP bridge stopped.");
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

                if (request.Method == "GET" && request.Path == "/health")
                {
                    await WriteJsonAsync(stream, 200, _healthFactory(), serverToken).ConfigureAwait(false);
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
                    await WriteJsonAsync(stream, response.Ok ? 200 : 500, response, serverToken).ConfigureAwait(false);
                    return;
                }

                if (request.Method == "POST" && request.Path == "/api/mobile/message")
                {
                    MobileBridgeRequest? mobileRequest = JsonSerializer.Deserialize<MobileBridgeRequest>(request.Body, _jsonOptions);
                    MobileBridgeResponse response = await _messageHandler(mobileRequest ?? new MobileBridgeRequest(), serverToken).ConfigureAwait(false);
                    await WriteJsonAsync(stream, response.Ok ? 200 : 500, response, serverToken).ConfigureAwait(false);
                    return;
                }

                if (request.Method == "POST" && request.Path == "/api/mobile/audio")
                {
                    MobileBridgeRequest? mobileRequest = JsonSerializer.Deserialize<MobileBridgeRequest>(request.Body, _jsonOptions);
                    MobileBridgeResponse response = await _audioHandler(mobileRequest ?? new MobileBridgeRequest(), serverToken).ConfigureAwait(false);
                    await WriteJsonAsync(stream, response.Ok ? 200 : 500, response, serverToken).ConfigureAwait(false);
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

    private async Task WriteJsonAsync(NetworkStream stream, int statusCode, object value, CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions);
        string statusText = statusCode == 200 ? "OK" : statusCode == 404 ? "Not Found" : "Error";
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
        Stop();
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
