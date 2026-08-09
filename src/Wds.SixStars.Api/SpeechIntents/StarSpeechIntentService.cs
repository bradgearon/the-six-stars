using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.SpeechIntents
{
    public sealed class StarSpeechIntentService
    {
        private readonly StarSpeechIntentOptions _options;
        private readonly StarSpeechIntentStore _store;

        public event EventHandler<StarSpeechIntentChangedEventArgs>? IntentChanged;
        public StarSpeechIntentOptions Options => _options;
        public string StoreFilePath => _store.FilePath;

        public StarSpeechIntentService(StarSpeechIntentOptions? options = null)
        {
            _options = options ?? new StarSpeechIntentOptions();
            _store = new StarSpeechIntentStore(_options.StateRoot);
        }

        public Task<List<StarSpeechIntent>> UpsertCandidatesAsync(string starId, IEnumerable<StarSpeechIntentCandidate>? candidates, DateTimeOffset generatedAt, CancellationToken cancellationToken)
        {
            if (!_options.Enabled) return Task.FromResult(new List<StarSpeechIntent>());
            return _store.WithLockAsync(values =>
            {
                DateTimeOffset now = generatedAt == default ? DateTimeOffset.UtcNow : generatedAt.ToUniversalTime();
                Cleanup(values, now);
                List<StarSpeechIntent> changed = new List<StarSpeechIntent>();
                foreach (StarSpeechIntentCandidate candidate in candidates ?? Enumerable.Empty<StarSpeechIntentCandidate>())
                {
                    if (candidate == null || string.IsNullOrWhiteSpace(candidate.SpeechGoal)) continue;
                    string dedupeKey = string.IsNullOrWhiteSpace(candidate.DedupeKey)
                        ? BuildDedupeKey(starId, candidate.Category, candidate.Topic, candidate.SpeechGoal)
                        : candidate.DedupeKey.Trim();
                    List<string> sourceEventIds = (candidate.SourceEventIds ?? new List<string>())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(16)
                        .ToList();
                    StarSpeechIntent? recentTerminal = values
                        .Where(x => string.Equals(x.StarId, starId, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(x.DedupeKey, dedupeKey, StringComparison.OrdinalIgnoreCase)
                            && (x.Status == StarSpeechIntentStatuses.Spoken || x.Status == StarSpeechIntentStatuses.Dismissed))
                        .OrderByDescending(x => x.SpokenAt ?? x.UpdatedAt)
                        .FirstOrDefault();
                    if (recentTerminal != null
                        && now - (recentTerminal.SpokenAt ?? recentTerminal.UpdatedAt) < TimeSpan.FromHours(Math.Max(0, _options.MinimumRepeatHours))
                        && string.Equals(recentTerminal.SpeechGoal, candidate.SpeechGoal.Trim(), StringComparison.Ordinal)
                        && !sourceEventIds.Except(recentTerminal.SourceEventIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase).Any())
                    {
                        continue;
                    }

                    StarSpeechIntent? existing = values
                        .Where(x => string.Equals(x.StarId, starId, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(x.DedupeKey, dedupeKey, StringComparison.OrdinalIgnoreCase)
                            && !IsTerminal(x.Status))
                        .OrderByDescending(x => x.UpdatedAt)
                        .FirstOrDefault();
                    bool speechGoalChanged = existing != null
                        && !string.Equals(existing.SpeechGoal, candidate.SpeechGoal.Trim(), StringComparison.Ordinal);
                    if (existing != null
                        && speechGoalChanged
                        && (existing.Status == StarSpeechIntentStatuses.Leased || existing.Status == StarSpeechIntentStatuses.Playing))
                    {
                        // Do not rewrite an intention whose old audio is already playing. Keep it
                        // completable and create a successor that can be prepared after it finishes.
                        existing = null;
                        speechGoalChanged = false;
                    }
                    if (existing == null)
                    {
                        string requestedIntentId = candidate.IntentId?.Trim() ?? string.Empty;
                        string intentId = !string.IsNullOrWhiteSpace(requestedIntentId)
                            && !values.Any(x => string.Equals(x.IntentId, requestedIntentId, StringComparison.OrdinalIgnoreCase))
                                ? requestedIntentId
                                : Guid.NewGuid().ToString("N");
                        existing = new StarSpeechIntent
                        {
                            IntentId = intentId,
                            StarId = starId,
                            CreatedAt = now,
                            UpdatedAt = now,
                            Status = StarSpeechIntentStatuses.Pending
                        };
                        values.Add(existing);
                    }
                    else if (speechGoalChanged)
                    {
                        // A changed communication goal invalidates any failed/prepared artifact and
                        // deserves a fresh preparation attempt. A playing item was handled above by
                        // creating a successor instead of mutating audio already in flight.
                        existing.Status = StarSpeechIntentStatuses.Pending;
                        existing.PreparedArtifactId = string.Empty;
                        existing.LeaseExpiresAt = null;
                        existing.PlaybackStartedAt = null;
                        existing.PrepareAttempts = 0;
                        existing.LastError = string.Empty;
                    }

                    bool preserveOperationalTimestamp = !speechGoalChanged
                        && (existing.Status == StarSpeechIntentStatuses.Preparing
                            || existing.Status == StarSpeechIntentStatuses.Failed
                            || existing.Status == StarSpeechIntentStatuses.Leased
                            || existing.Status == StarSpeechIntentStatuses.Playing);
                    DateTimeOffset operationalUpdatedAt = existing.UpdatedAt == default ? now : existing.UpdatedAt;

                    existing.Category = string.IsNullOrWhiteSpace(candidate.Category) ? "daily_state" : candidate.Category.Trim();
                    existing.Topic = candidate.Topic?.Trim() ?? string.Empty;
                    existing.SpeechGoal = candidate.SpeechGoal.Trim();
                    existing.BaseScore = ClampScore(candidate.BaseScore);
                    existing.Priority = StarSpeechIntentPriorities.Normalize(candidate.Priority);
                    existing.Confidence = Clamp01(candidate.Confidence);
                    existing.SourceEventIds = sourceEventIds;
                    existing.DedupeKey = dedupeKey;
                    existing.RequiresAcknowledgement = candidate.RequiresAcknowledgement;
                    existing.ExpiresAt = now.AddHours(Math.Max(1, Math.Min(720, candidate.ExpiresAfterHours <= 0 ? 24 : candidate.ExpiresAfterHours)));
                    existing.UpdatedAt = preserveOperationalTimestamp ? operationalUpdatedAt : now;
                    existing.EffectiveScore = CalculateEffectiveScore(existing, now);
                    if (!preserveOperationalTimestamp) existing.LastError = string.Empty;
                    changed.Add(Clone(existing));
                }

                values.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                int maximumStored = Math.Max(100, _options.MaximumStoredIntents <= 0 ? 800 : _options.MaximumStoredIntents);
                if (values.Count > maximumStored) values.RemoveRange(maximumStored, values.Count - maximumStored);
                return Task.FromResult(changed);
            }, true, cancellationToken).ContinueWith(task =>
            {
                List<StarSpeechIntent> changed = task.GetAwaiter().GetResult();
                foreach (StarSpeechIntent item in changed) IntentChanged?.Invoke(this, new StarSpeechIntentChangedEventArgs { Intent = item, ChangeType = "upserted" });
                return changed;
            }, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public Task<StarSpeechIntentManifest> GetManifestAsync(string? starId, CancellationToken cancellationToken)
        {
            return _store.WithLockAsync(values =>
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                Cleanup(values, now);
                List<StarSpeechIntent> active = values
                    .Where(x => !IsTerminal(x.Status) && (!x.ExpiresAt.HasValue || x.ExpiresAt.Value > now))
                    .Where(x => string.IsNullOrWhiteSpace(starId) || string.Equals(x.StarId, starId, StringComparison.OrdinalIgnoreCase))
                    .Select(x =>
                    {
                        StarSpeechIntent copy = Clone(x);
                        copy.EffectiveScore = CalculateEffectiveScore(copy, now);
                        return copy;
                    })
                    .OrderByDescending(x => StarSpeechIntentPriorities.Rank(x.Priority))
                    .ThenByDescending(x => x.EffectiveScore)
                    .ThenBy(x => x.CreatedAt)
                    .ToList();
                return Task.FromResult(new StarSpeechIntentManifest
                {
                    Ok = true,
                    GeneratedAt = now,
                    Intents = active,
                    HighestEffectiveScore = active.Count == 0 ? 0 : active.Max(x => x.EffectiveScore),
                    HighestPriority = active.Count == 0 ? StarSpeechIntentPriorities.Low : active.OrderByDescending(x => StarSpeechIntentPriorities.Rank(x.Priority)).First().Priority
                });
            }, true, cancellationToken);
        }

        public async Task<StarSpeechIntent?> GetNextForForegroundAsync(string starId, string? excludedIntentId, CancellationToken cancellationToken)
        {
            StarSpeechIntentManifest manifest = await GetManifestAsync(starId, cancellationToken).ConfigureAwait(false);
            return manifest.Intents.FirstOrDefault(x =>
            {
                if (!string.IsNullOrWhiteSpace(excludedIntentId)
                    && string.Equals(x.IntentId, excludedIntentId, StringComparison.OrdinalIgnoreCase)) return false;
                if (x.Status != StarSpeechIntentStatuses.Pending
                    && x.Status != StarSpeechIntentStatuses.Failed
                    && x.Status != StarSpeechIntentStatuses.Ready) return false;
                if (StarSpeechIntentPriorities.Rank(x.Priority) < StarSpeechIntentPriorities.Rank(StarSpeechIntentPriorities.High)) return false;
                return x.EffectiveScore >= _options.HighPriorityPreparationThreshold;
            });
        }

        public async Task<StarSpeechIntent?> GetHighestEligibleUnpreparedAsync(string starId, CancellationToken cancellationToken)
        {
            StarSpeechIntentManifest manifest = await GetManifestAsync(starId, cancellationToken).ConfigureAwait(false);
            return manifest.Intents.FirstOrDefault(x => IsEligibleForPreparation(x, DateTimeOffset.UtcNow));
        }

        public async Task<StarSpeechIntent?> GetNextToPrepareAsync(string starId, CancellationToken cancellationToken)
        {
            StarSpeechIntentManifest manifest = await GetManifestAsync(starId, cancellationToken).ConfigureAwait(false);
            int preparedOrPreparingCount = manifest.Intents.Count(x => x.Status == StarSpeechIntentStatuses.Preparing
                || x.Status == StarSpeechIntentStatuses.Ready
                || x.Status == StarSpeechIntentStatuses.Leased
                || x.Status == StarSpeechIntentStatuses.Playing);
            if (preparedOrPreparingCount >= Math.Max(1, _options.MaximumPreparedItemsPerStar)) return null;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return manifest.Intents.FirstOrDefault(x => IsEligibleForPreparation(x, now));
        }

        private bool IsEligibleForPreparation(StarSpeechIntent x, DateTimeOffset now)
        {
            if (x.Status != StarSpeechIntentStatuses.Pending && x.Status != StarSpeechIntentStatuses.Failed) return false;
            if (x.Status == StarSpeechIntentStatuses.Failed)
            {
                if (x.PrepareAttempts >= Math.Max(1, _options.MaximumPrepareAttempts)) return false;
                if (now - x.UpdatedAt < TimeSpan.FromSeconds(Math.Max(1, _options.PrepareRetryDelaySeconds))) return false;
            }
            int rank = StarSpeechIntentPriorities.Rank(x.Priority);
            if (rank >= StarSpeechIntentPriorities.Rank(StarSpeechIntentPriorities.High))
                return x.EffectiveScore >= _options.HighPriorityPreparationThreshold;
            return rank == StarSpeechIntentPriorities.Rank(StarSpeechIntentPriorities.Medium)
                && x.EffectiveScore >= _options.MediumPriorityPreparationThreshold;
        }

        public Task<StarSpeechIntent?> MarkPreparingAsync(string intentId, CancellationToken cancellationToken)
        {
            return MutateAsync(intentId, "preparing", x =>
            {
                x.Status = StarSpeechIntentStatuses.Preparing;
                x.PrepareAttempts++;
                x.LastError = string.Empty;
            }, cancellationToken, x => x.Status == StarSpeechIntentStatuses.Pending || x.Status == StarSpeechIntentStatuses.Failed);
        }

        public Task<StarSpeechIntent?> MarkReadyAsync(string intentId, string artifactId, CancellationToken cancellationToken)
        {
            return MutateAsync(intentId, "ready", x =>
            {
                x.Status = StarSpeechIntentStatuses.Ready;
                x.PreparedArtifactId = artifactId ?? string.Empty;
                x.LeaseExpiresAt = null;
                x.LastError = string.Empty;
            }, cancellationToken, x => x.Status == StarSpeechIntentStatuses.Preparing || x.Status == StarSpeechIntentStatuses.Pending || x.Status == StarSpeechIntentStatuses.Failed);
        }

        public Task<StarSpeechIntent?> MarkFailedAsync(string intentId, string error, CancellationToken cancellationToken)
        {
            return MutateAsync(intentId, "failed", x =>
            {
                x.Status = StarSpeechIntentStatuses.Failed;
                x.LastError = error ?? string.Empty;
                x.LeaseExpiresAt = null;
            }, cancellationToken, x => !IsTerminal(x.Status));
        }

        public Task<StarSpeechIntent?> RequeueForPreparationAsync(string intentId, string reason, CancellationToken cancellationToken)
        {
            return MutateAsync(intentId, "requeued", x =>
            {
                x.Status = StarSpeechIntentStatuses.Pending;
                x.PreparedArtifactId = string.Empty;
                x.LeaseExpiresAt = null;
                x.PlaybackStartedAt = null;
                x.PrepareAttempts = 0;
                x.LastError = string.IsNullOrWhiteSpace(reason)
                    ? "Prepared speech artifact was unavailable; the intention was requeued."
                    : reason.Trim();
            }, cancellationToken, x => !IsTerminal(x.Status));
        }

        public Task<StarSpeechIntent?> LeaseAsync(string intentId, CancellationToken cancellationToken)
        {
            return MutateAsync(intentId, "leased", x =>
            {
                if (x.Status != StarSpeechIntentStatuses.Leased) x.PlayAttempts++;
                x.Status = StarSpeechIntentStatuses.Leased;
                x.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _options.LeaseMinutes));
            }, cancellationToken, x => x.Status == StarSpeechIntentStatuses.Ready || x.Status == StarSpeechIntentStatuses.Leased);
        }

        public Task<StarSpeechIntent?> MarkPlaybackStartedAsync(string intentId, CancellationToken cancellationToken)
        {
            return MutateAsync(intentId, "playing", x =>
            {
                x.Status = StarSpeechIntentStatuses.Playing;
                if (!x.PlaybackStartedAt.HasValue) x.PlaybackStartedAt = DateTimeOffset.UtcNow;
                x.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _options.LeaseMinutes));
            }, cancellationToken, x => x.Status == StarSpeechIntentStatuses.Ready || x.Status == StarSpeechIntentStatuses.Leased || x.Status == StarSpeechIntentStatuses.Playing);
        }

        public Task<StarSpeechIntent?> MarkSpokenAsync(string intentId, CancellationToken cancellationToken)
        {
            return MutateAsync(intentId, "spoken", x =>
            {
                x.Status = StarSpeechIntentStatuses.Spoken;
                if (!x.SpokenAt.HasValue) x.SpokenAt = DateTimeOffset.UtcNow;
                x.LeaseExpiresAt = null;
            }, cancellationToken, x => !IsTerminal(x.Status) || x.Status == StarSpeechIntentStatuses.Spoken);
        }

        public Task<StarSpeechIntent?> DismissAsync(string intentId, CancellationToken cancellationToken)
        {
            return MutateAsync(intentId, "dismissed", x =>
            {
                x.Status = StarSpeechIntentStatuses.Dismissed;
                x.LeaseExpiresAt = null;
            }, cancellationToken, x => !IsTerminal(x.Status) || x.Status == StarSpeechIntentStatuses.Dismissed);
        }

        private async Task<StarSpeechIntent?> MutateAsync(
            string intentId,
            string changeType,
            Action<StarSpeechIntent> update,
            CancellationToken cancellationToken,
            Func<StarSpeechIntent, bool>? canUpdate = null)
        {
            StarSpeechIntent? changed = await _store.WithLockAsync(values =>
            {
                StarSpeechIntent? item = values.FirstOrDefault(x => string.Equals(x.IntentId, intentId, StringComparison.OrdinalIgnoreCase));
                if (item == null || (canUpdate != null && !canUpdate(item))) return Task.FromResult<StarSpeechIntent?>(null);
                update(item);
                item.UpdatedAt = DateTimeOffset.UtcNow;
                item.EffectiveScore = CalculateEffectiveScore(item, item.UpdatedAt);
                return Task.FromResult<StarSpeechIntent?>(Clone(item));
            }, true, cancellationToken).ConfigureAwait(false);
            if (changed != null) IntentChanged?.Invoke(this, new StarSpeechIntentChangedEventArgs { Intent = changed, ChangeType = changeType });
            return changed;
        }

        private void Cleanup(List<StarSpeechIntent> values, DateTimeOffset now)
        {
            foreach (StarSpeechIntent item in values)
            {
                if (item.Status == StarSpeechIntentStatuses.Preparing
                    && now - item.UpdatedAt >= TimeSpan.FromMinutes(Math.Max(1, _options.PreparationTimeoutMinutes)))
                {
                    item.Status = StarSpeechIntentStatuses.Failed;
                    item.PreparedArtifactId = string.Empty;
                    item.LeaseExpiresAt = null;
                    item.LastError = "Prepared speech generation timed out before producing a ready artifact.";
                    item.UpdatedAt = now;
                }
                if ((item.Status == StarSpeechIntentStatuses.Leased || item.Status == StarSpeechIntentStatuses.Playing)
                    && item.LeaseExpiresAt.HasValue && item.LeaseExpiresAt.Value <= now)
                {
                    item.Status = string.IsNullOrWhiteSpace(item.PreparedArtifactId) ? StarSpeechIntentStatuses.Pending : StarSpeechIntentStatuses.Ready;
                    item.LeaseExpiresAt = null;
                }
                if (!IsTerminal(item.Status) && item.ExpiresAt.HasValue && item.ExpiresAt.Value <= now)
                {
                    item.Status = StarSpeechIntentStatuses.Expired;
                }
            }
            DateTimeOffset archiveCutoff = now.AddDays(-Math.Max(1, _options.SpokenArchiveDays));
            values.RemoveAll(x => IsTerminal(x.Status) && x.UpdatedAt < archiveCutoff);
        }

        private double CalculateEffectiveScore(StarSpeechIntent item, DateTimeOffset now)
        {
            double hours = Math.Max(0, (now - item.CreatedAt).TotalHours);
            double ageBoost = Math.Min(Math.Max(0, _options.MaximumAgeBoost), hours * Math.Max(0, _options.AgingPointsPerHour));
            double priorityBoost = StarSpeechIntentPriorities.Rank(item.Priority) switch
            {
                4 => 10,
                3 => 6,
                2 => 2,
                _ => 0
            };
            double retryPenalty = Math.Min(15, Math.Max(0, item.PrepareAttempts - 1) * 2);
            return Math.Round(ClampScore(item.BaseScore + ageBoost + priorityBoost - retryPenalty), 2);
        }

        private static bool IsTerminal(string? status)
        {
            string value = status ?? string.Empty;
            return value == StarSpeechIntentStatuses.Spoken || value == StarSpeechIntentStatuses.Dismissed
                || value == StarSpeechIntentStatuses.Expired || value == StarSpeechIntentStatuses.Superseded;
        }

        private static string BuildDedupeKey(string starId, string? category, string? topic, string? speechGoal)
        {
            string raw = (starId ?? string.Empty) + "|" + (category ?? string.Empty) + "|" + (topic ?? string.Empty) + "|" + (speechGoal ?? string.Empty);
            return raw.Trim().ToLowerInvariant();
        }

        private static double ClampScore(double value) => Math.Max(0, Math.Min(100, value));
        private static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));

        private static StarSpeechIntent Clone(StarSpeechIntent item)
        {
            return new StarSpeechIntent
            {
                IntentId = item.IntentId,
                StarId = item.StarId,
                Category = item.Category,
                Topic = item.Topic,
                SpeechGoal = item.SpeechGoal,
                BaseScore = item.BaseScore,
                EffectiveScore = item.EffectiveScore,
                Priority = item.Priority,
                Confidence = item.Confidence,
                SourceEventIds = item.SourceEventIds == null ? new List<string>() : item.SourceEventIds.ToList(),
                DedupeKey = item.DedupeKey,
                RequiresAcknowledgement = item.RequiresAcknowledgement,
                Status = item.Status,
                PreparedArtifactId = item.PreparedArtifactId,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt,
                ExpiresAt = item.ExpiresAt,
                LeaseExpiresAt = item.LeaseExpiresAt,
                PlaybackStartedAt = item.PlaybackStartedAt,
                SpokenAt = item.SpokenAt,
                PrepareAttempts = item.PrepareAttempts,
                PlayAttempts = item.PlayAttempts,
                LastError = item.LastError
            };
        }
    }
}
