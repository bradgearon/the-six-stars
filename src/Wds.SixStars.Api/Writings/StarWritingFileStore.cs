using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Writings
{
    /// <summary>Canonical desktop/mobile writing-notes store. One separate JSON file is kept per Star.</summary>
    public sealed class StarWritingFileStore
    {
        private readonly string _rootDirectory;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        private readonly JsonSerializerOptions _jsonLineOptions = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        public StarWritingFileStore(string rootDirectory)
        {
            _rootDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(rootDirectory) ? Environment.CurrentDirectory : rootDirectory);
        }

        public string RootDirectory => _rootDirectory;
        public string AllStatementsLogPath => Path.Combine(_rootDirectory, "all-star-writing-statements.jsonl");

        public static StarWritingFileStore CreateDefault()
        {
            string? configured = Environment.GetEnvironmentVariable("SIX_STARS_WRITING_NOTES_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(configured)) return new StarWritingFileStore(configured);
            string root = FindProjectRoot(AppContext.BaseDirectory);
            return new StarWritingFileStore(Path.Combine(root, "state", "writing-notes"));
        }

        public async Task<StarWritingNotesState> LoadAsync(string starId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string path = BuildPath(starId);
                if (!File.Exists(path)) return CreateEmpty(starId);
                try
                {
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, true))
                    {
                        StarWritingNotesState? state = await JsonSerializer.DeserializeAsync<StarWritingNotesState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
                        return Normalize(state ?? CreateEmpty(starId), starId);
                    }
                }
                catch (JsonException)
                {
                    string backup = path + ".unreadable-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ".bak";
                    try { File.Copy(path, backup, true); } catch { }
                    return CreateEmpty(starId);
                }
            }
            finally { _gate.Release(); }
        }

        public async Task SaveAsync(StarWritingNotesState state, CancellationToken cancellationToken)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SaveStateUnderGateAsync(state, cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Appends every reviewed candidate to one shared JSON Lines file, then saves the Star's
        /// normal notes state. The aggregate log is deliberately not removed by ClearAsync.
        /// </summary>
        public async Task SaveGeneratedBatchAsync(
            StarWritingNotesState state,
            string speaker,
            StarWritingHistoryBatch batch,
            CancellationToken cancellationToken)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (batch == null) throw new ArgumentNullException(nameof(batch));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_rootDirectory);
                await AppendBatchUnderGateAsync(state.StarId, speaker, batch, cancellationToken).ConfigureAwait(false);
                await SaveStateUnderGateAsync(state, cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public async Task ClearAsync(string starId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string path = BuildPath(starId);
                if (File.Exists(path)) File.Delete(path);
            }
            finally { _gate.Release(); }
        }

        private async Task SaveStateUnderGateAsync(StarWritingNotesState state, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_rootDirectory);
            state = Normalize(state, state.StarId);
            state.UpdatedAt = DateTimeOffset.UtcNow;
            string path = BuildPath(state.StarId);
            string temp = path + ".tmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private async Task AppendBatchUnderGateAsync(
            string starId,
            string speaker,
            StarWritingHistoryBatch batch,
            CancellationToken cancellationToken)
        {
            if (batch.Items == null || batch.Items.Count == 0) return;

            StringBuilder lines = new StringBuilder();
            foreach (StarWritingHistoryItem item in batch.Items.Where(x => x != null))
            {
                StarWritingEmotionVector.TryNormalizeComplete(
                    item.EmotionalVector,
                    out string normalizedVector,
                    out Dictionary<string, StarWritingEmotionValue> values);

                Dictionary<string, double> scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (string category in StarWritingEmotionVector.Categories)
                {
                    if (values.TryGetValue(category, out StarWritingEmotionValue? value))
                    {
                        scores[category] = value.Score;
                    }
                }

                StarWritingStatementLogEntry entry = new StarWritingStatementLogEntry
                {
                    Id = item.Id,
                    SourceBatchId = item.SourceBatchId,
                    StarId = string.IsNullOrWhiteSpace(starId) ? "UnknownStar" : starId.Trim(),
                    Speaker = string.IsNullOrWhiteSpace(speaker) ? (string.IsNullOrWhiteSpace(starId) ? "Unknown Star" : starId.Trim()) : speaker.Trim(),
                    GeneratedAt = item.GeneratedAt,
                    Index = item.Index,
                    WordsUsed = (item.WordsUsed ?? new List<string>()).ToList(),
                    Statement = item.Writing ?? string.Empty,
                    EmotionalVector = string.IsNullOrWhiteSpace(normalizedVector) ? (item.EmotionalVector ?? string.Empty) : normalizedVector,
                    Scores = scores,
                    Persisted = item.Persisted,
                    MatchedPersistenceRuleIds = (item.MatchedPersistenceRuleIds ?? new List<string>()).ToList()
                };
                lines.Append(JsonSerializer.Serialize(entry, _jsonLineOptions));
                lines.Append('\n');
            }

            if (lines.Length == 0) return;
            byte[] bytes = Encoding.UTF8.GetBytes(lines.ToString());
            using (FileStream stream = new FileStream(AllStatementsLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 8192, true))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private string BuildPath(string starId) => Path.Combine(_rootDirectory, SafeName(starId) + "-writing-notes.json");

        private static StarWritingNotesState CreateEmpty(string starId) => new StarWritingNotesState { StarId = string.IsNullOrWhiteSpace(starId) ? "UnknownStar" : starId.Trim(), UpdatedAt = DateTimeOffset.UtcNow };

        private static StarWritingNotesState Normalize(StarWritingNotesState state, string starId)
        {
            state.Version = 4;
            state.StarId = string.IsNullOrWhiteSpace(state.StarId) ? (string.IsNullOrWhiteSpace(starId) ? "UnknownStar" : starId.Trim()) : state.StarId.Trim();
            state.RecentWritingHistory ??= new List<StarWritingHistoryItem>();
            state.AllUsedWords ??= new List<string>();
            state.PersistedWritings ??= new List<PersistedStarWriting>();

            foreach (StarWritingHistoryItem item in state.RecentWritingHistory.Where(x => x != null))
            {
                item.WordsUsed ??= new List<string>();
                item.MatchedPersistenceRuleIds ??= new List<string>();
                if (StarWritingEmotionVector.TryNormalizeComplete(item.EmotionalVector, out string normalizedVector, out _))
                {
                    item.EmotionalVector = normalizedVector;
                }
            }
            state.RecentWritingHistory = state.RecentWritingHistory
                .Where(x => x != null)
                .OrderByDescending(x => x.GeneratedAt)
                .Take(10)
                .OrderBy(x => x.GeneratedAt)
                .ThenBy(x => x.Index)
                .ToList();

            foreach (PersistedStarWriting item in state.PersistedWritings.Where(x => x != null))
            {
                item.WordsUsed ??= new List<string>();
                item.MatchedPersistenceRuleIds ??= new List<string>();
                if (StarWritingEmotionVector.TryNormalizeComplete(item.EmotionalVector, out string normalizedVector, out _))
                {
                    item.EmotionalVector = normalizedVector;
                }
            }
            state.PersistedWritings = state.PersistedWritings.Where(x => x != null).ToList();
            // A word becomes historical only when its writing reaches the shelf. Rebuild rather
            // than preserving older Version 1-3 files that marked every generated word as used.
            state.AllUsedWords = state.PersistedWritings
                .OrderBy(x => x.PersistedAt)
                .ThenBy(x => x.GeneratedAt)
                .SelectMany(x => x.WordsUsed ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return state;
        }

        private static string SafeName(string? value)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "unknown-star" : value.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
            return text.Replace(' ', '_');
        }

        private static string FindProjectRoot(string start)
        {
            DirectoryInfo? directory = new DirectoryInfo(string.IsNullOrWhiteSpace(start) ? Environment.CurrentDirectory : start);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SixStars.sln")) || Directory.Exists(Path.Combine(directory.FullName, "src"))) return directory.FullName;
                directory = directory.Parent;
            }
            return Environment.CurrentDirectory;
        }
    }
}
