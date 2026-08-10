using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.XPosting
{
    /// <summary>Official X API v2 REST client using OAuth 2.0 Authorization Code with PKCE user tokens.</summary>
    public sealed class XApiPostClient : IXPostClient, IDisposable
    {
        private const string CreatePostUrl = "https://api.x.com/2/tweets";
        private const string CurrentUserUrl = "https://api.x.com/2/users/me?user.fields=username,name";
        private const string TokenUrl = "https://api.x.com/2/oauth2/token";
        private readonly IXOAuth2TokenStore _tokenStore;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;
        private readonly int _refreshSkewSeconds;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        public XApiPostClient(
            IXOAuth2TokenStore tokenStore,
            int refreshSkewSeconds = 120,
            HttpClient? httpClient = null)
        {
            _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
            _refreshSkewSeconds = Math.Max(30, refreshSkewSeconds);
            _httpClient = httpClient ?? new HttpClient();
            _ownsClient = httpClient == null;
        }

        public async Task<bool> HasAuthorizationAsync(
            StarXAccountOptions account,
            CancellationToken cancellationToken)
        {
            if (account == null || !account.HasClientConfiguration) return false;
            return await GetUsableTokenAsync(account, cancellationToken).ConfigureAwait(false) != null;
        }

        public async Task<StarXPostApiResult> CreatePostAsync(
            StarXAccountOptions account,
            string text,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            string postText = (text ?? string.Empty).Trim();
            if (!account.HasClientConfiguration)
            {
                return new StarXPostApiResult { Error = "The selected Star account is missing an OAuth 2.0 ClientId or valid loopback RedirectUri." };
            }
            if (postText.Length == 0)
            {
                return new StarXPostApiResult { Error = "X post text was empty." };
            }
            if (postText.Length > 280)
            {
                return new StarXPostApiResult { Error = "X post text exceeded 280 characters." };
            }

            StarXOAuth2Token? token = await GetUsableTokenAsync(account, cancellationToken).ConfigureAwait(false);
            if (token == null)
            {
                return new StarXPostApiResult { Error = "Authorize or reauthorize " + account.Id + " for X in the desktop app before posting." };
            }

            StarXPostApiResult first = await SendCreatePostAsync(token.AccessToken, postText, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (first.Success) return first;

            // 403 is a hard create-post denial, not an access-token refresh signal. Do not immediately
            // send the same post again. Diagnose the existing user token with /2/users/me so the UI can
            // distinguish an X-side posting restriction from a token that is no longer usable.
            if (first.StatusCode == (int)HttpStatusCode.Forbidden)
            {
                return await DiagnoseForbiddenAsync(token.AccessToken, first, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            }

            // 401 is the normal expired/rejected-token signal. Refresh exactly once and retry the post once.
            if (first.StatusCode != (int)HttpStatusCode.Unauthorized) return first;

            StarXOAuth2Token? refreshed = await RefreshTokenAsync(account, forceRefresh: true, cancellationToken).ConfigureAwait(false);
            if (refreshed == null)
            {
                first.Error = string.IsNullOrWhiteSpace(first.Error)
                    ? "X rejected the OAuth 2.0 token and it could not be refreshed. Reauthorize this Star account."
                    : first.Error;
                return first;
            }

            StarXPostApiResult second = await SendCreatePostAsync(refreshed.AccessToken, postText, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (second.StatusCode != (int)HttpStatusCode.Forbidden) return second;

            // A post retried because of an initial 401 may itself reveal a hard 403 restriction. Diagnose it,
            // but do not perform another refresh or another create-post retry.
            return await DiagnoseForbiddenAsync(refreshed.AccessToken, second, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }

        private async Task<StarXOAuth2Token?> GetUsableTokenAsync(
            StarXAccountOptions account,
            CancellationToken cancellationToken)
        {
            StarXOAuth2Token? token = await _tokenStore.LoadAsync(account.Id, cancellationToken).ConfigureAwait(false);
            if (token == null
                || !string.Equals(token.ClientId, account.ClientId.Trim(), StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(token.AccessToken)
                || !token.HasRefreshToken) return null;
            if (!token.NeedsRefresh(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(_refreshSkewSeconds))) return token;
            return await RefreshTokenAsync(account, forceRefresh: false, cancellationToken).ConfigureAwait(false);
        }

        private async Task<StarXOAuth2Token?> RefreshTokenAsync(
            StarXAccountOptions account,
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            SemaphoreSlim gate = _refreshGates.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StarXOAuth2Token? current = await _tokenStore.LoadAsync(account.Id, cancellationToken).ConfigureAwait(false);
                if (current == null
                    || !string.Equals(current.ClientId, account.ClientId.Trim(), StringComparison.Ordinal)
                    || !current.HasRefreshToken) return null;
                if (!forceRefresh && !current.NeedsRefresh(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(_refreshSkewSeconds))) return current;

                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["refresh_token"] = current.RefreshToken,
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = account.ClientId.Trim()
                });

                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                StarXOAuth2Token? refreshed = ParseTokenResponse(account.Id, account.ClientId, responseText, current.RefreshToken, current.Scope);
                if (refreshed == null || string.IsNullOrWhiteSpace(refreshed.AccessToken) || !refreshed.HasRefreshToken) return null;
                await _tokenStore.SaveAsync(refreshed, cancellationToken).ConfigureAwait(false);
                return refreshed;
            }
            catch
            {
                return null;
            }
            finally
            {
                gate.Release();
            }
        }


        private async Task<StarXPostApiResult> DiagnoseForbiddenAsync(
            string accessToken,
            StarXPostApiResult forbidden,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            forbidden.PermissionDenied = true;
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, CurrentUserUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, Math.Min(20, timeoutSeconds))));
            try
            {
                using HttpResponseMessage response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
                string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string username = ParseCurrentUsername(responseText);
                    forbidden.AuthenticatedUsername = username;
                    string who = string.IsNullOrWhiteSpace(username) ? "the authorized X user" : "@" + username;
                    string raw = string.IsNullOrWhiteSpace(forbidden.Error) ? "" : " X response: " + forbidden.Error.Trim();
                    forbidden.Error = "X accepted the OAuth token for " + who
                        + " but refused POST /2/tweets with HTTP 403. This is an X-side account/app posting permission restriction, not an expired token; the batch has been stopped to avoid repeated denied posts."
                        + raw;
                    return forbidden;
                }

                forbidden.Error = "X returned HTTP 403 for POST /2/tweets, and the current OAuth token could not validate with /2/users/me (HTTP "
                    + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                    + "). Reauthorize this Star account before trying again. X response: " + forbidden.Error;
                return forbidden;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                forbidden.Error = "X returned HTTP 403 for POST /2/tweets. The follow-up OAuth identity check timed out, so the batch was stopped without retrying the post. X response: " + forbidden.Error;
                return forbidden;
            }
            catch (Exception ex)
            {
                forbidden.Error = "X returned HTTP 403 for POST /2/tweets. The follow-up OAuth identity check failed (" + ex.Message + "), so the batch was stopped without retrying the post. X response: " + forbidden.Error;
                return forbidden;
            }
        }

        private static string ParseCurrentUsername(string responseText)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseText);
                if (document.RootElement.TryGetProperty("data", out JsonElement data)
                    && data.TryGetProperty("username", out JsonElement username))
                {
                    return username.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
            }
            return string.Empty;
        }

        private async Task<StarXPostApiResult> SendCreatePostAsync(
            string accessToken,
            string postText,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            string payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = postText });
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, CreatePostUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
            try
            {
                using HttpResponseMessage response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
                string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return new StarXPostApiResult
                    {
                        StatusCode = (int)response.StatusCode,
                        Error = string.IsNullOrWhiteSpace(responseText)
                            ? "X API returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + "."
                            : responseText
                    };
                }

                string postId = string.Empty;
                string returnedText = postText;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(responseText);
                    if (document.RootElement.TryGetProperty("data", out JsonElement data))
                    {
                        if (data.TryGetProperty("id", out JsonElement idElement)) postId = idElement.GetString() ?? string.Empty;
                        if (data.TryGetProperty("text", out JsonElement textElement)) returnedText = textElement.GetString() ?? postText;
                    }
                }
                catch (JsonException)
                {
                    // A successful status is authoritative even if X changes optional response fields.
                }

                return new StarXPostApiResult
                {
                    Success = true,
                    PostId = postId,
                    Text = returnedText,
                    StatusCode = (int)response.StatusCode
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new StarXPostApiResult { Error = "X API request timed out." };
            }
            catch (Exception ex)
            {
                return new StarXPostApiResult { Error = ex.Message };
            }
        }

        public static StarXOAuth2Token? ParseTokenResponse(
            string accountId,
            string clientId,
            string responseText,
            string existingRefreshToken = "",
            string existingScope = "")
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseText);
                JsonElement root = document.RootElement;
                string accessToken = root.TryGetProperty("access_token", out JsonElement access) ? access.GetString() ?? string.Empty : string.Empty;
                string refreshToken = root.TryGetProperty("refresh_token", out JsonElement refresh) ? refresh.GetString() ?? string.Empty : existingRefreshToken;
                string tokenType = root.TryGetProperty("token_type", out JsonElement type) ? type.GetString() ?? "bearer" : "bearer";
                string scope = root.TryGetProperty("scope", out JsonElement scopeElement)
                    ? scopeElement.GetString() ?? existingScope
                    : existingScope;
                int expiresIn = root.TryGetProperty("expires_in", out JsonElement expires) && expires.TryGetInt32(out int parsed) ? parsed : 7200;
                if (string.IsNullOrWhiteSpace(accessToken)) return null;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                return new StarXOAuth2Token
                {
                    AccountId = (accountId ?? string.Empty).Trim().ToLowerInvariant(),
                    ClientId = (clientId ?? string.Empty).Trim(),
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    TokenType = tokenType,
                    Scope = scope,
                    ExpiresAt = now.AddSeconds(Math.Max(60, expiresIn)),
                    UpdatedAt = now
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_ownsClient) _httpClient.Dispose();
            foreach (SemaphoreSlim gate in _refreshGates.Values) gate.Dispose();
        }
    }
}
