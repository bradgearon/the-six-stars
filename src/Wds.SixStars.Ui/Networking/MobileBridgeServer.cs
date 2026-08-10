using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Ui.Networking;

/// <summary>
/// Small dependency-free LAN HTTP server for the Android companion app.
/// It intentionally uses TcpListener instead of HttpListener so listening on the LAN
/// does not require Windows URL ACL reservations or administrator launch.
/// </summary>
public sealed class MobileBridgeServer : IDisposable
{
    private readonly Func<MobileBridgeHttpRequest, CancellationToken, Task<MobileBridgeHttpResponse>> _handler;
    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public MobileBridgeServer(Func<MobileBridgeHttpRequest, CancellationToken, Task<MobileBridgeHttpResponse>> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public bool IsRunning => _listener != null;
    public int Port { get; private set; }

    public void Start(int port)
    {
        if (IsRunning)
        {
            return;
        }

        Port = port <= 0 ? 8066 : port;
        _cts = new CancellationTokenSource();
        _listener = CreateDualStackListener(Port);
        _listener.Start(backlog: 16);
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
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
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch
        {
        }
        finally
        {
            _listener = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                TcpListener listener = _listener ?? throw new ObjectDisposedException(nameof(MobileBridgeServer));
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                TcpClient acceptedClient = client;
                client = null;
                _ = Task.Run(() => HandleClientAsync(acceptedClient, cancellationToken), cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                client?.Dispose();
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient? client, CancellationToken cancellationToken)
    {
        if (client == null)
        {
            return;
        }

        using (client)
        {
            client.ReceiveTimeout = 120000;
            client.SendTimeout = 120000;
            using NetworkStream stream = client.GetStream();

            MobileBridgeHttpRequest request;
            try
            {
                request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                request.RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { ok = false, error = "bad_request", message = ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }

            MobileBridgeHttpResponse response;
            try
            {
                response = await _handler(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                response = MobileBridgeHttpResponse.Json(500, new
                {
                    ok = false,
                    error = "mobile_bridge_handler_error",
                    message = ex.Message,
                    type = ex.GetType().FullName,
                    stack = ex.ToString()
                });
            }

            if (response.JsonBody != null)
            {
                await WriteJsonAsync(stream, response.StatusCode, response.JsonBody, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteTextAsync(stream, response.StatusCode, response.ContentType, response.Body ?? string.Empty, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<MobileBridgeHttpRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] headerBytes = await ReadHeaderBytesAsync(stream, cancellationToken).ConfigureAwait(false);
        string headerText = Encoding.ASCII.GetString(headerBytes);
        string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
        {
            throw new InvalidOperationException("The HTTP request line was empty.");
        }

        string[] parts = lines[0].Split(' ');
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("The HTTP request line was invalid: " + lines[0]);
        }

        Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
        }

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? contentLengthText))
        {
            int.TryParse(contentLengthText, out contentLength);
        }

        string body = string.Empty;
        if (contentLength > 0)
        {
            if (contentLength > 32 * 1024 * 1024)
            {
                throw new InvalidOperationException("Request body too large for mobile bridge. Limit is 32 MB.");
            }

            byte[] bodyBytes = await ReadExactAsync(stream, contentLength, cancellationToken).ConfigureAwait(false);
            body = Encoding.UTF8.GetString(bodyBytes);
        }

        return new MobileBridgeHttpRequest
        {
            Method = parts[0].Trim().ToUpperInvariant(),
            Path = parts[1].Split('?')[0],
            RawTarget = parts[1],
            Headers = headers,
            Body = body
        };
    }

    private static async Task<byte[]> ReadHeaderBytesAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        List<byte> bytes = new List<byte>(2048);
        byte[] one = new byte[1];
        while (bytes.Count < 64 * 1024)
        {
            int read = await stream.ReadAsync(one, 0, 1, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            bytes.Add(one[0]);
            int count = bytes.Count;
            if (count >= 4
                && bytes[count - 4] == (byte)'\r'
                && bytes[count - 3] == (byte)'\n'
                && bytes[count - 2] == (byte)'\r'
                && bytes[count - 1] == (byte)'\n')
            {
                return bytes.ToArray();
            }
        }

        throw new InvalidOperationException("Could not read complete HTTP headers.");
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer, offset, count - offset, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The client closed the connection before the full request body was read.");
            }

            offset += read;
        }

        return buffer;
    }

    private async Task WriteJsonAsync(NetworkStream stream, int statusCode, object jsonBody, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(jsonBody, _jsonOptions);
        await WriteTextAsync(stream, statusCode, "application/json; charset=utf-8", json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(NetworkStream stream, int statusCode, string contentType, string body, CancellationToken cancellationToken)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
        string statusText = statusCode switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "OK"
        };

        string headers =
            $"HTTP/1.1 {statusCode} {statusText}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Access-Control-Allow-Methods: GET,POST,OPTIONS\r\n" +
            "Connection: close\r\n" +
            "\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

public sealed class MobileBridgeHttpRequest
{
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string RawTarget { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string Body { get; set; } = string.Empty;
    public string RemoteEndPoint { get; set; } = string.Empty;
}

public sealed class MobileBridgeHttpResponse
{
    public int StatusCode { get; set; } = 200;
    public string ContentType { get; set; } = "application/json; charset=utf-8";
    public string? Body { get; set; }
    public object? JsonBody { get; set; }

    public static MobileBridgeHttpResponse Json(int statusCode, object body)
    {
        return new MobileBridgeHttpResponse { StatusCode = statusCode, JsonBody = body };
    }

    public static MobileBridgeHttpResponse Text(int statusCode, string body, string contentType = "text/plain; charset=utf-8")
    {
        return new MobileBridgeHttpResponse { StatusCode = statusCode, Body = body, ContentType = contentType };
    }
}
