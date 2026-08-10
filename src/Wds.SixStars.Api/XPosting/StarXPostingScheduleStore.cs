using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.XPosting
{
    public sealed class StarXPostingScheduleStore
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        public StarXPostingScheduleStore(string stateRoot)
        {
            string root = Path.GetFullPath(string.IsNullOrWhiteSpace(stateRoot) ? "state/x-posting" : stateRoot);
            _filePath = Path.Combine(root, "schedule-state.json");
        }

        public async Task<bool> IsCompletedAsync(string localDate, string slotId, string starId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StarXPostingScheduleState state = await LoadUnderGateAsync(cancellationToken).ConfigureAwait(false);
                return state.CompletedRuns.Any(x =>
                    string.Equals(x.LocalDate, localDate, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.SlotId, slotId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.StarId, starId, StringComparison.OrdinalIgnoreCase));
            }
            finally { _gate.Release(); }
        }

        public async Task RecordCompletedAsync(
            string localDate,
            string slotId,
            string starId,
            int postedCount,
            int failedCount,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StarXPostingScheduleState state = await LoadUnderGateAsync(cancellationToken).ConfigureAwait(false);
                state.CompletedRuns.RemoveAll(x =>
                    string.Equals(x.LocalDate, localDate, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.SlotId, slotId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.StarId, starId, StringComparison.OrdinalIgnoreCase));
                state.CompletedRuns.Add(new StarXPostingSlotRunRecord
                {
                    LocalDate = localDate,
                    SlotId = slotId,
                    StarId = starId,
                    CompletedAt = DateTimeOffset.Now,
                    PostedCount = Math.Max(0, postedCount),
                    FailedCount = Math.Max(0, failedCount)
                });
                DateTimeOffset cutoff = DateTimeOffset.Now.AddDays(-14);
                state.CompletedRuns = state.CompletedRuns
                    .Where(x => x.CompletedAt >= cutoff)
                    .OrderBy(x => x.CompletedAt)
                    .ToList();
                state.UpdatedAt = DateTimeOffset.Now;
                await SaveUnderGateAsync(state, cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        private async Task<StarXPostingScheduleState> LoadUnderGateAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_filePath)) return new StarXPostingScheduleState();
            try
            {
                using FileStream stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, true);
                StarXPostingScheduleState? state = await JsonSerializer.DeserializeAsync<StarXPostingScheduleState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
                state ??= new StarXPostingScheduleState();
                state.CompletedRuns ??= new System.Collections.Generic.List<StarXPostingSlotRunRecord>();
                return state;
            }
            catch (JsonException)
            {
                return new StarXPostingScheduleState();
            }
        }

        private async Task SaveUnderGateAsync(StarXPostingScheduleState state, CancellationToken cancellationToken)
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temp = _filePath + ".tmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(_filePath)) File.Delete(_filePath);
            File.Move(temp, _filePath);
        }
    }
}
