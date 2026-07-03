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
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(appData)) appData = AppContext.BaseDirectory;
            string folder = Path.Combine(appData, "Wds", "SixStars");
            return new JsonStarStateStore(Path.Combine(folder, "six-stars-local-dev-state.json"));
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
                    state = new StarState { StarId = starId };
                    data.Stars.Add(state);
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
                StarState reset = new StarState { StarId = starId };
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

            using (FileStream stream = File.OpenRead(_filePath))
            {
                SixStarsSaveData? loaded = await JsonSerializer.DeserializeAsync<SixStarsSaveData>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
                _cache = loaded ?? new SixStarsSaveData { SessionId = sessionId };
                if (string.IsNullOrWhiteSpace(_cache.SessionId)) _cache.SessionId = sessionId;
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
