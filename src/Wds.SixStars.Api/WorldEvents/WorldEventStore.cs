using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.WorldEvents
{
    /// <summary>Durable append/merge store. The file is rewritten atomically because the bounded event history is intentionally small.</summary>
    public sealed class WorldEventStore
    {
        private readonly string _filePath;
        private readonly int _maximumStoredEvents;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public string FilePath => _filePath;

        public WorldEventStore(string? configuredRoot, int maximumStoredEvents)
        {
            string root = ResolveRoot(configuredRoot);
            Directory.CreateDirectory(root);
            _filePath = Path.Combine(root, "events.json");
            _maximumStoredEvents = Math.Max(100, maximumStoredEvents <= 0 ? 2000 : maximumStoredEvents);
        }

        public async Task<List<WorldEventRecord>> ReadAllAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        public async Task<WorldEventInsertResult> AddAsync(IEnumerable<WorldEventRecord>? incoming, CancellationToken cancellationToken)
        {
            WorldEventInsertResult result = new WorldEventInsertResult { Ok = true };
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                List<WorldEventRecord> current = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
                long sequence = current.Count == 0 ? 0L : current.Max(x => x.Sequence);
                HashSet<string> identities = new HashSet<string>(current.Select(BuildIdentity), StringComparer.OrdinalIgnoreCase);
                DateTimeOffset now = DateTimeOffset.UtcNow;

                foreach (WorldEventRecord source in incoming ?? Enumerable.Empty<WorldEventRecord>())
                {
                    if (source == null || string.IsNullOrWhiteSpace(source.Summary)) continue;
                    WorldEventRecord item = Normalize(source, now);
                    string identity = BuildIdentity(item);
                    if (!identities.Add(identity))
                    {
                        result.DuplicateCount++;
                        continue;
                    }
                    item.Sequence = ++sequence;
                    current.Add(item);
                    result.AcceptedEvents.Add(item);
                    result.AcceptedCount++;
                    foreach (string star in item.TargetStars.Where(x => !string.IsNullOrWhiteSpace(x)))
                    {
                        if (!result.AffectedStars.Any(x => string.Equals(x, star, StringComparison.OrdinalIgnoreCase))) result.AffectedStars.Add(star.Trim());
                    }
                }

                if (result.AcceptedCount > 0)
                {
                    DateTimeOffset cutoff = DateTimeOffset.UtcNow;
                    current = current
                        .Where(x => !x.ExpiresAt.HasValue || x.ExpiresAt.Value > cutoff)
                        .OrderByDescending(x => x.Sequence)
                        .Take(_maximumStoredEvents)
                        .OrderBy(x => x.Sequence)
                        .ToList();
                    await WriteUnsafeAsync(current, cancellationToken).ConfigureAwait(false);
                }
                result.LatestSequence = sequence;
                result.StateRefreshQueued = result.AcceptedEvents.Any(x => x.RequiresStateRefresh);
                return result;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = ex.Message;
                return result;
            }
            finally { _gate.Release(); }
        }

        private async Task<List<WorldEventRecord>> ReadUnsafeAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_filePath)) return new List<WorldEventRecord>();
            try
            {
                using (FileStream stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, true))
                {
                    List<WorldEventRecord>? values = await JsonSerializer.DeserializeAsync<List<WorldEventRecord>>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
                    return values ?? new List<WorldEventRecord>();
                }
            }
            catch (FileNotFoundException) { return new List<WorldEventRecord>(); }
            catch (DirectoryNotFoundException) { return new List<WorldEventRecord>(); }
            catch (JsonException) { return new List<WorldEventRecord>(); }
        }

        private async Task WriteUnsafeAsync(List<WorldEventRecord> items, CancellationToken cancellationToken)
        {
            string temp = _filePath + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? Environment.CurrentDirectory);
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                await JsonSerializer.SerializeAsync(stream, items, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(_filePath)) File.Delete(_filePath);
            File.Move(temp, _filePath);
        }

        private static WorldEventRecord Normalize(WorldEventRecord source, DateTimeOffset now)
        {
            source.EventId = string.IsNullOrWhiteSpace(source.EventId) ? Guid.NewGuid().ToString("N") : source.EventId.Trim();
            source.Source = string.IsNullOrWhiteSpace(source.Source) ? "manual" : source.Source.Trim();
            source.SourceEventId = source.SourceEventId?.Trim() ?? string.Empty;
            source.Kind = string.IsNullOrWhiteSpace(source.Kind) ? "message" : source.Kind.Trim();
            source.OccurredAt = source.OccurredAt == default ? now : source.OccurredAt;
            source.ReceivedAt = now;
            source.Importance = Math.Max(0, Math.Min(100, source.Importance));
            source.Authority = Math.Max(0, Math.Min(100, source.Authority));
            source.Scope = string.IsNullOrWhiteSpace(source.Scope) ? "world" : source.Scope.Trim();
            source.Summary = source.Summary.Trim();
            source.Details = source.Details?.Trim() ?? string.Empty;
            source.Location = source.Location?.Trim() ?? string.Empty;
            source.TargetStars = (source.TargetStars ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
            source.Metadata = source.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return source;
        }

        private static string BuildIdentity(WorldEventRecord item)
        {
            if (!string.IsNullOrWhiteSpace(item.SourceEventId)) return (item.Source ?? string.Empty).Trim() + "|" + item.SourceEventId.Trim();
            return "event|" + (item.EventId ?? string.Empty).Trim();
        }

        private static string ResolveRoot(string? configured)
        {
            string root = string.IsNullOrWhiteSpace(configured) ? "state/world-events" : configured.Trim();
            if (Path.IsPathRooted(root)) return root;
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "SixStars.sln")) || Directory.Exists(Path.Combine(dir.FullName, "src")))
                {
                    return Path.GetFullPath(Path.Combine(dir.FullName, root));
                }
                dir = dir.Parent;
            }
            return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, root));
        }
    }
}
