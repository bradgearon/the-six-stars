using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.WorldEvents
{
    public sealed class WorldEventService : IDisposable
    {
        private readonly WorldEventOptions _options;
        private readonly WorldEventStore _store;
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        private CancellationTokenSource? _feedCts;
        private readonly List<Task> _feedTasks = new List<Task>();

        public event EventHandler<WorldEventAddedEventArgs>? EventAdded;
        public event EventHandler<Exception>? FeedFailed;
        public WorldEventOptions Options => _options;
        public string StoreFilePath => _store.FilePath;

        public WorldEventService(WorldEventOptions? options = null)
        {
            _options = options ?? new WorldEventOptions();
            _store = new WorldEventStore(_options.StateRoot, _options.MaximumStoredEvents);
        }

        public async Task<WorldEventInsertResult> AddAsync(IEnumerable<WorldEventRecord>? events, CancellationToken cancellationToken)
        {
            if (!_options.Enabled) return new WorldEventInsertResult { Ok = false, Error = "World event service is disabled." };
            WorldEventInsertResult result = await _store.AddAsync(events, cancellationToken).ConfigureAwait(false);
            foreach (WorldEventRecord item in result.AcceptedEvents)
            {
                EventAdded?.Invoke(this, new WorldEventAddedEventArgs { Event = item });
            }
            return result;
        }

        public Task<WorldEventInsertResult> AddAsync(WorldEventRecord item, CancellationToken cancellationToken)
        {
            return AddAsync(new[] { item }, cancellationToken);
        }

        public async Task<WorldEventBatch> GetAfterAsync(long afterSequence, int max, string? starId, CancellationToken cancellationToken)
        {
            int take = Math.Max(1, Math.Min(500, max <= 0 ? 100 : max));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<WorldEventRecord> all = await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            IEnumerable<WorldEventRecord> query = all.Where(x => x.Sequence > Math.Max(0, afterSequence) && (!x.ExpiresAt.HasValue || x.ExpiresAt.Value > now));
            if (!string.IsNullOrWhiteSpace(starId))
            {
                string selected = starId.Trim();
                query = query.Where(x => x.TargetStars.Count == 0 || x.TargetStars.Any(y => string.Equals(y, selected, StringComparison.OrdinalIgnoreCase)));
            }
            List<WorldEventRecord> events = query.OrderBy(x => x.Sequence).Take(take).ToList();
            return new WorldEventBatch
            {
                Ok = true,
                AfterSequence = afterSequence,
                LatestSequence = all.Count == 0 ? 0 : all.Max(x => x.Sequence),
                Events = events
            };
        }

        public async Task<List<WorldEventRecord>> GetPromptEventsAsync(string starId, CancellationToken cancellationToken)
        {
            List<WorldEventRecord> all = await _store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return all
                .Where(x => (!x.ExpiresAt.HasValue || x.ExpiresAt.Value > now)
                    && (x.TargetStars.Count == 0 || x.TargetStars.Any(y => string.Equals(y, starId, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(x => x.Importance)
                .ThenByDescending(x => x.Sequence)
                .Take(Math.Max(4, _options.PromptEventCount <= 0 ? 32 : _options.PromptEventCount))
                .OrderBy(x => x.Sequence)
                .ToList();
        }

        public void StartFeedPolling()
        {
            if (!_options.Enabled || _options.Feeds == null || _options.Feeds.Count == 0 || _feedCts != null) return;
            _feedCts = new CancellationTokenSource();
            CancellationToken feedToken = _feedCts.Token;
            foreach (WorldEventFeedOptions feed in _options.Feeds.Where(x => x != null && x.Enabled && !string.IsNullOrWhiteSpace(x.Url)))
            {
                _feedTasks.Add(Task.Run(() => RunFeedAsync(feed, feedToken), CancellationToken.None));
            }
        }

        public void StopFeedPolling()
        {
            try { _feedCts?.Cancel(); } catch { }
            _feedCts?.Dispose();
            _feedCts = null;
            _feedTasks.Clear();
        }

        private async Task RunFeedAsync(WorldEventFeedOptions feed, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, feed.Url))
                    {
                        string key = string.IsNullOrWhiteSpace(feed.ApiKey) ? _options.ApiKey : feed.ApiKey;
                        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
                        using (HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();
                            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            List<WorldEventRecord> events = ParseFeed(json, feed.FeedId);
                            if (events.Count > 0) await AddAsync(events, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { FeedFailed?.Invoke(this, ex); }

                int seconds = Math.Max(5, Math.Min(3600, feed.PollSeconds <= 0 ? 20 : feed.PollSeconds));
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private List<WorldEventRecord> ParseFeed(string json, string feedId)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<WorldEventRecord>();
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement root = doc.RootElement;
                JsonElement values = root;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in root.EnumerateObject())
                    {
                        if (string.Equals(property.Name, "events", StringComparison.OrdinalIgnoreCase))
                        {
                            values = property.Value;
                            break;
                        }
                    }
                }
                if (values.ValueKind != JsonValueKind.Array) return new List<WorldEventRecord>();
                List<WorldEventRecord>? result = JsonSerializer.Deserialize<List<WorldEventRecord>>(values.GetRawText(), _jsonOptions);
                foreach (WorldEventRecord item in result ?? new List<WorldEventRecord>())
                {
                    if (string.IsNullOrWhiteSpace(item.Source) || string.Equals(item.Source, "manual", StringComparison.OrdinalIgnoreCase))
                        item.Source = string.IsNullOrWhiteSpace(feedId) ? "remote-feed" : feedId;
                    if (string.IsNullOrWhiteSpace(item.SourceEventId) && string.IsNullOrWhiteSpace(item.EventId))
                    {
                        item.SourceEventId = BuildStableFeedIdentity(item, feedId);
                    }
                }
                return result ?? new List<WorldEventRecord>();
            }
        }

        private static string BuildStableFeedIdentity(WorldEventRecord item, string feedId)
        {
            string raw = (feedId ?? string.Empty) + "|" + item.OccurredAt.ToUniversalTime().ToString("o") + "|"
                + (item.Kind ?? string.Empty) + "|" + string.Join(",", item.TargetStars ?? new List<string>()) + "|"
                + (item.Location ?? string.Empty) + "|" + (item.Summary ?? string.Empty) + "|" + (item.Details ?? string.Empty);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                StringBuilder text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) text.Append(value.ToString("x2"));
                return (string.IsNullOrWhiteSpace(feedId) ? "remote-feed" : feedId.Trim()) + ":" + text.ToString();
            }
        }

        public void Dispose()
        {
            StopFeedPolling();
            _httpClient.Dispose();
        }
    }
}
