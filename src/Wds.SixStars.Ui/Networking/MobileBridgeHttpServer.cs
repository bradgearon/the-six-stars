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

namespace Wds.SixStars.Ui.Networking;

public sealed class MobileBridgeHttpServer : IDisposable
{
    private readonly Func<MobileBridgeRequest, CancellationToken, Task<MobileBridgeResponse>> _messageHandler;
    private readonly Func<MobileBridgeRequest, CancellationToken, Task<MobileBridgeResponse>> _audioHandler;
    private readonly Func<CancellationToken, Task<object>> _testAudioHandler;
    private readonly Func<object> _healthFactory;
    private readonly Func<object> _starsFactory;
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
        Func<object> starsFactory)
    {
        Port = port;
        _messageHandler = messageHandler;
        _audioHandler = audioHandler;
        _testAudioHandler = testAudioHandler;
        _healthFactory = healthFactory;
        _starsFactory = starsFactory;
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

        string path = first[1];
        int question = path.IndexOf('?');
        if (question >= 0) path = path.Substring(0, question);

        return new HttpRequest
        {
            Method = first[0].ToUpperInvariant(),
            Path = path,
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
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public string Body { get; set; } = string.Empty;
    }
}
