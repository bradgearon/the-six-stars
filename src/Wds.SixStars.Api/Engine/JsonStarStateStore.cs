using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Engine
{
    /// <summary>
    /// JSON-backed state store for the desktop harness. Unity can skip this and use its own
    /// serialized SixStarsSaveData object with InMemoryStarStateStore/ReplaceSessionAsync.
    /// </summary>
    public sealed class JsonStarStateStore : IStarStateStore
    {
        private readonly string _filePath;
        public string FilePath => _filePath;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly JsonSerializerOptions _jsonOptions;
        private SixStarsSaveData? _cache;

        public JsonStarStateStore(string filePath)
        {
            _filePath = filePath;
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
            _jsonOptions.Converters.Add(new JsonStringEnumConverter());
        }

        public static JsonStarStateStore CreateDefaultLocalDevStore()
        {
            string? explicitPath = Environment.GetEnvironmentVariable("SIX_STARS_STATE_FILE");
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return new JsonStarStateStore(Path.GetFullPath(explicitPath));
            }

            string root = FindProjectRoot(AppContext.BaseDirectory);
            string folder = Path.Combine(root, "state");
            return new JsonStarStateStore(Path.Combine(folder, "six-stars-local-dev-state.json"));
        }

        private static string FindProjectRoot(string start)
        {
            DirectoryInfo? dir = new DirectoryInfo(string.IsNullOrWhiteSpace(start) ? Environment.CurrentDirectory : start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "SixStars.sln")) || Directory.Exists(Path.Combine(dir.FullName, "src")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return Environment.CurrentDirectory;
        }

        private static bool IsDefaultMet(string starId)
        {
            return string.Equals(starId, "Elusiaa", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDefaultAlly(string starId)
        {
            return string.Equals(starId, "Elusiaa", StringComparison.OrdinalIgnoreCase);
        }

        private static StarState CreateDefaultState(string starId)
        {
            return new StarState
            {
                StarId = starId,
                IsMet = IsDefaultMet(starId),
                IsAlly = IsDefaultAlly(starId),
                EmotionalConnectionSkillLevel = "interested_private_potential_partner",
                EmotionalConnectionPercent = 0,
                LastDailyStateSourceUpdateAt = DateTimeOffset.UtcNow,
                TodayState = new StarDailyStateContext(),
                GirlfriendReadinessSummary = "Not ready: requires ally status, EmotionalConnectionPercent >= 85, Heart >= 20, Trust >= 15, and Romance >= 12."
            };
        }

        private static bool NormalizeStateDefaults(StarState state)
        {
            bool changed = false;
            if (string.IsNullOrWhiteSpace(state.StarId)) return false;
            if (string.IsNullOrWhiteSpace(state.EmotionalConnectionSkillLevel))
            {
                state.EmotionalConnectionSkillLevel = "interested_private_potential_partner";
                changed = true;
            }

            if (state.EmotionalConnectionPercent < 0)
            {
                state.EmotionalConnectionPercent = 0;
                changed = true;
            }
            if (state.EmotionalConnectionPercent > 100)
            {
                state.EmotionalConnectionPercent = 100;
                changed = true;
            }
            if (state.ActiveEffectors == null)
            {
                state.ActiveEffectors = new List<StarRelationshipEffector>();
                changed = true;
            }
            if (state.EmotionVectorHistory == null)
            {
                state.EmotionVectorHistory = new List<StarEmotionVectorHistoryEntry>();
                changed = true;
            }
            if (state.RelationshipScoreAccumulators == null)
            {
                state.RelationshipScoreAccumulators = new List<StarRelationshipScoreAccumulator>();
                changed = true;
            }
            if (state.TodayState == null)
            {
                state.TodayState = new StarDailyStateContext();
                changed = true;
            }
            if (state.IsAlly && !state.IsMet)
            {
                state.IsMet = true;
                changed = true;
            }
            if (IsDefaultMet(state.StarId) && !state.IsMet)
            {
                bool explicitlyRemovedMet = state.RelationshipEvents.Any(x => x.IndexOf("Met flag removed", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!explicitlyRemovedMet)
                {
                    state.IsMet = true;
                    changed = true;
                }
            }
            if (string.IsNullOrWhiteSpace(state.GirlfriendReadinessSummary))
            {
                state.GirlfriendReadinessSummary = "Not ready: requires ally status, EmotionalConnectionPercent >= 85, Heart >= 20, Trust >= 15, and Romance >= 12.";
                changed = true;
            }

            // Backward compatibility for older local state files that had no IsAlly field.
            // Elusiaa is the only default ally; the other Stars remain locked/not ally until gameplay updates them.
            bool explicitlyRemoved = state.RelationshipEvents.Any(x => x.IndexOf("Ally flag removed", StringComparison.OrdinalIgnoreCase) >= 0);
            if (IsDefaultAlly(state.StarId) && !state.IsAlly && !explicitlyRemoved)
            {
                state.IsAlly = true;
                state.IsMet = true;
                changed = true;
            }
            return changed;
        }

        public async Task<StarState> GetAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SixStarsSaveData data = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
                StarState? state = data.Stars.FirstOrDefault(x => string.Equals(x.StarId, starId, StringComparison.OrdinalIgnoreCase));
                if (state == null)
                {
                    state = CreateDefaultState(starId);
                    data.Stars.Add(state);
                    await SaveFileAsync(data, cancellationToken).ConfigureAwait(false);
                }
                else if (NormalizeStateDefaults(state))
                {
                    await SaveFileAsync(data, cancellationToken).ConfigureAwait(false);
                }
                return state;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SaveAsync(string sessionId, StarState state, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SixStarsSaveData data = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
                int index = data.Stars.FindIndex(x => string.Equals(x.StarId, state.StarId, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) data.Stars[index] = state;
                else data.Stars.Add(state);
                await SaveFileAsync(data, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<StarState>> GetAllAsync(string sessionId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SixStarsSaveData data = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return data.Stars.ToList();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task ReplaceSessionAsync(string sessionId, IEnumerable<StarState> states, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SixStarsSaveData data = new SixStarsSaveData
                {
                    Version = 1,
                    SessionId = sessionId,
                    SavedAt = DateTimeOffset.UtcNow,
                    Stars = states.ToList()
                };
                _cache = data;
                await SaveFileAsync(data, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }


        public async Task ResetStarAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SixStarsSaveData data = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
                int index = data.Stars.FindIndex(x => string.Equals(x.StarId, starId, StringComparison.OrdinalIgnoreCase));
                StarState reset = CreateDefaultState(starId);
                if (index >= 0) data.Stars[index] = reset;
                else data.Stars.Add(reset);
                await SaveFileAsync(data, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<SixStarsSaveData> LoadAsync(string sessionId, CancellationToken cancellationToken)
        {
            if (_cache != null)
            {
                return _cache;
            }

            if (!File.Exists(_filePath))
            {
                _cache = new SixStarsSaveData { SessionId = sessionId };
                return _cache;
            }

            try
            {
                using (FileStream stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true))
                {
                    SixStarsSaveData? loaded = await JsonSerializer.DeserializeAsync<SixStarsSaveData>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
                    _cache = loaded ?? new SixStarsSaveData { SessionId = sessionId };
                    if (string.IsNullOrWhiteSpace(_cache.SessionId)) _cache.SessionId = sessionId;
                    return _cache;
                }
            }
            catch (FileNotFoundException)
            {
                _cache = new SixStarsSaveData { SessionId = sessionId };
                return _cache;
            }
            catch (DirectoryNotFoundException)
            {
                _cache = new SixStarsSaveData { SessionId = sessionId };
                return _cache;
            }
            catch (JsonException)
            {
                _cache = new SixStarsSaveData { SessionId = sessionId };
                return _cache;
            }
        }

        private async Task SaveFileAsync(SixStarsSaveData data, CancellationToken cancellationToken)
        {
            data.SavedAt = DateTimeOffset.UtcNow;
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string tempPath = _filePath + ".tmp";
            using (FileStream stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, data, _jsonOptions, cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(_filePath)) File.Delete(_filePath);
            File.Move(tempPath, _filePath);
        }
    }
}
