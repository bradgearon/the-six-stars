using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.XPosting
{
    /// <summary>Minimal official X API v2 REST client for POST /2/tweets using OAuth 1.0a user context.</summary>
    public sealed class XApiPostClient : IXPostClient, IDisposable
    {
        private const string CreatePostUrl = "https://api.x.com/2/tweets";
        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;

        public XApiPostClient(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _ownsClient = httpClient == null;
        }

        public async Task<StarXPostApiResult> CreatePostAsync(
            StarXAccountOptions account,
            string text,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            string postText = (text ?? string.Empty).Trim();
            if (!account.HasCredentials)
            {
                return new StarXPostApiResult { Error = "The selected Star account does not have complete OAuth 1.0a credentials." };
            }
            if (postText.Length == 0)
            {
                return new StarXPostApiResult { Error = "X post text was empty." };
            }
            if (postText.Length > 280)
            {
                return new StarXPostApiResult { Error = "X post text exceeded 280 characters." };
            }

            Dictionary<string, string> oauth = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["oauth_consumer_key"] = account.ApiKey.Trim(),
                ["oauth_nonce"] = Guid.NewGuid().ToString("N"),
                ["oauth_signature_method"] = "HMAC-SHA1",
                ["oauth_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ["oauth_token"] = account.AccessToken.Trim(),
                ["oauth_version"] = "1.0"
            };
            oauth["oauth_signature"] = CreateSignature(oauth, account.ApiSecret.Trim(), account.AccessTokenSecret.Trim());

            string authorization = "OAuth " + string.Join(", ", oauth
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => PercentEncode(x.Key) + "=\"" + PercentEncode(x.Value) + "\""));

            string payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = postText });
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, CreatePostUrl);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
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

        private static string CreateSignature(
            IReadOnlyDictionary<string, string> oauth,
            string consumerSecret,
            string tokenSecret)
        {
            string normalized = string.Join("&", oauth
                .OrderBy(x => PercentEncode(x.Key), StringComparer.Ordinal)
                .ThenBy(x => PercentEncode(x.Value), StringComparer.Ordinal)
                .Select(x => PercentEncode(x.Key) + "=" + PercentEncode(x.Value)));
            string baseString = "POST&" + PercentEncode(CreatePostUrl) + "&" + PercentEncode(normalized);
            string signingKey = PercentEncode(consumerSecret ?? string.Empty) + "&" + PercentEncode(tokenSecret ?? string.Empty);
            using HMACSHA1 hmac = new HMACSHA1(Encoding.ASCII.GetBytes(signingKey));
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(baseString)));
        }

        private static string PercentEncode(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty)
                .Replace("!", "%21")
                .Replace("*", "%2A")
                .Replace("'", "%27")
                .Replace("(", "%28")
                .Replace(")", "%29");
        }

        public void Dispose()
        {
            if (_ownsClient) _httpClient.Dispose();
        }
    }
}
