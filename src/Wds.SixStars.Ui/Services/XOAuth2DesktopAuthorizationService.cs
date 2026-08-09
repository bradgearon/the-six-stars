using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Wds.SixStars.Api.XPosting;

namespace Wds.SixStars.Ui.Services;

/// <summary>Completes X OAuth 2.0 Authorization Code with PKCE through a one-use loopback callback.</summary>
public sealed class XOAuth2DesktopAuthorizationService : IDisposable
{
    private const string AuthorizeUrl = "https://x.com/i/oauth2/authorize";
    private const string TokenUrl = "https://api.x.com/2/oauth2/token";
    private readonly IXOAuth2TokenStore _tokenStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _authorizationGate = new SemaphoreSlim(1, 1);

    public XOAuth2DesktopAuthorizationService(IXOAuth2TokenStore tokenStore, HttpClient? httpClient = null)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient == null;
    }

    public async Task<StarXOAuth2AuthorizationResult> AuthorizeAsync(
        StarXAccountOptions account,
        string scopes,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (account == null) throw new ArgumentNullException(nameof(account));
        string accountId = (account.Id ?? string.Empty).Trim().ToLowerInvariant();
        if (!account.HasClientConfiguration)
        {
            return Failure(accountId, "Set ClientId and a loopback http RedirectUri for this Star in XPosting.StarAccounts first.");
        }

        Uri redirectUri = new Uri(account.RedirectUri, UriKind.Absolute);
        IPAddress? redirectAddress;
        if (string.Equals(redirectUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            redirectAddress = IPAddress.Loopback;
        }
        else if (!IPAddress.TryParse(redirectUri.Host, out redirectAddress) || !IPAddress.IsLoopback(redirectAddress))
        {
            return Failure(accountId, "RedirectUri must use localhost or an explicit loopback address such as http://127.0.0.1:47837/x-oauth2/callback/.");
        }
        if (redirectUri.Port <= 0)
        {
            return Failure(accountId, "RedirectUri must include a fixed local port registered exactly in the X Developer Console.");
        }

        await _authorizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, timeoutSeconds)));

            string state = CreateRandomBase64Url(32);
            string verifier = CreateRandomBase64Url(64);
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string normalizedScopes = NormalizeScopes(scopes);
            string authorizationUrl = BuildAuthorizeUrl(account.ClientId, redirectUri, normalizedScopes, state, challenge);

            TcpListener listener = new TcpListener(redirectAddress, redirectUri.Port);
            try
            {
                listener.Start(1);
                Process.Start(new ProcessStartInfo
                {
                    FileName = authorizationUrl,
                    UseShellExecute = true
                });

                using TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                using NetworkStream stream = client.GetStream();
                string requestTarget = await ReadRequestTargetAsync(stream, timeout.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestTarget))
                {
                    await WriteBrowserResponseAsync(stream, false, "The local callback did not contain an OAuth request.", timeout.Token).ConfigureAwait(false);
                    return Failure(accountId, "The local X authorization callback was empty.");
                }

                Uri callback = requestTarget.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(requestTarget, UriKind.Absolute)
                    : new Uri(redirectUri.GetLeftPart(UriPartial.Authority) + requestTarget, UriKind.Absolute);

                if (!string.Equals(callback.AbsolutePath.TrimEnd('/'), redirectUri.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    await WriteBrowserResponseAsync(stream, false, "The callback path did not match the configured redirect URI.", timeout.Token).ConfigureAwait(false);
                    return Failure(accountId, "X returned to an unexpected local callback path.");
                }

                Dictionary<string, string> query = ParseQuery(callback.Query);
                if (query.TryGetValue("error", out string? oauthError))
                {
                    string description = query.TryGetValue("error_description", out string? value) ? value : oauthError;
                    await WriteBrowserResponseAsync(stream, false, description, timeout.Token).ConfigureAwait(false);
                    return Failure(accountId, "X authorization was declined or failed: " + description);
                }
                if (!query.TryGetValue("state", out string? returnedState) || !FixedTimeEquals(state, returnedState))
                {
                    await WriteBrowserResponseAsync(stream, false, "The OAuth state check failed.", timeout.Token).ConfigureAwait(false);
                    return Failure(accountId, "The X authorization state check failed; no token was saved.");
                }
                if (!query.TryGetValue("code", out string? code) || string.IsNullOrWhiteSpace(code))
                {
                    await WriteBrowserResponseAsync(stream, false, "X did not return an authorization code.", timeout.Token).ConfigureAwait(false);
                    return Failure(accountId, "X did not return an OAuth authorization code.");
                }

                StarXOAuth2Token? token = await ExchangeCodeAsync(account, redirectUri, code, verifier, timeout.Token).ConfigureAwait(false);
                if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                {
                    await WriteBrowserResponseAsync(stream, false, "The authorization code could not be exchanged for an access token.", timeout.Token).ConfigureAwait(false);
                    return Failure(accountId, "X returned the callback, but the OAuth token exchange failed.");
                }
                if (!token.HasRefreshToken)
                {
                    await WriteBrowserResponseAsync(stream, false, "X did not issue a refresh token. Make sure offline.access is requested.", timeout.Token).ConfigureAwait(false);
                    return Failure(accountId, "X did not issue a refresh token. Keep offline.access in OAuthScopes and authorize again.");
                }

                await _tokenStore.SaveAsync(token, timeout.Token).ConfigureAwait(false);
                await WriteBrowserResponseAsync(stream, true, accountId + " is now authorized for X posting. You may close this tab.", timeout.Token).ConfigureAwait(false);
                return new StarXOAuth2AuthorizationResult
                {
                    Success = true,
                    AccountId = accountId,
                    ExpiresAt = token.ExpiresAt,
                    Message = accountId + " is authorized for X posting. The access token will refresh automatically."
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure(accountId, "X authorization timed out before the local callback completed.");
            }
            catch (SocketException ex)
            {
                return Failure(accountId, "The local OAuth callback could not listen on " + redirectUri.GetLeftPart(UriPartial.Authority) + ": " + ex.Message);
            }
            finally
            {
                listener.Stop();
            }
        }
        catch (Exception ex)
        {
            return Failure(accountId, "X authorization failed: " + ex.Message);
        }
        finally
        {
            _authorizationGate.Release();
        }
    }

    private async Task<StarXOAuth2Token?> ExchangeCodeAsync(
        StarXAccountOptions account,
        Uri redirectUri,
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["client_id"] = account.ClientId.Trim(),
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code_verifier"] = verifier
        });
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return XApiPostClient.ParseTokenResponse(account.Id, account.ClientId, responseText);
    }

    private static string BuildAuthorizeUrl(
        string clientId,
        Uri redirectUri,
        string scopes,
        string state,
        string challenge)
    {
        Dictionary<string, string> values = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId.Trim(),
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = scopes,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        return AuthorizeUrl + "?" + string.Join("&", values.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
    }

    private static async Task<string> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 2048, leaveOpen: true);
        string? requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        string? line;
        do
        {
            line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        while (!string.IsNullOrEmpty(line));

        if (string.IsNullOrWhiteSpace(requestLine)) return string.Empty;
        string[] parts = requestLine.Split(' ');
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }

    private static async Task WriteBrowserResponseAsync(
        NetworkStream stream,
        bool success,
        string message,
        CancellationToken cancellationToken)
    {
        string title = success ? "X authorization complete" : "X authorization failed";
        string body = "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + title + "</title></head>"
            + "<body style=\"font-family:Segoe UI,Arial,sans-serif;padding:32px;max-width:720px\"><h1>" + title + "</h1><p>"
            + WebUtility.HtmlEncode(message) + "</p><p>You can return to The Six Stars desktop app.</p></body></html>";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string headers = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: "
            + bodyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in (query ?? string.Empty).TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = part.IndexOf('=');
            string key = separator >= 0 ? part.Substring(0, separator) : part;
            string value = separator >= 0 ? part.Substring(separator + 1) : string.Empty;
            values[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return values;
    }

    private static string NormalizeScopes(string scopes)
    {
        string[] required = { "tweet.read", "tweet.write", "users.read", "offline.access" };
        HashSet<string> values = new HashSet<string>(
            (scopes ?? string.Empty).Split(new[] { ' ', ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
        foreach (string value in required) values.Add(value);
        return string.Join(" ", values);
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        byte[] left = Encoding.UTF8.GetBytes(expected ?? string.Empty);
        byte[] right = Encoding.UTF8.GetBytes(actual ?? string.Empty);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string CreateRandomBase64Url(int bytes)
    {
        return Base64Url(RandomNumberGenerator.GetBytes(bytes));
    }

    private static string Base64Url(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static StarXOAuth2AuthorizationResult Failure(string accountId, string message)
    {
        return new StarXOAuth2AuthorizationResult { Success = false, AccountId = accountId, Message = message };
    }

    public void Dispose()
    {
        _authorizationGate.Dispose();
        if (_ownsClient) _httpClient.Dispose();
    }
}
