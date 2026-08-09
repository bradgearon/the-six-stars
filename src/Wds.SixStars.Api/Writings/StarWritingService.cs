using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Writings
{
    public sealed class StarWritingService
    {
        private const string GlobalOverallRuleId = "global-overall-excellence";
        private const double DefaultGlobalOverallThreshold = 80.0;
        private static readonly double GlobalOverallThreshold = ReadGlobalOverallThreshold();

        private readonly IStarProfileProvider _profiles;
        private readonly IStarWritingAgent _agent;
        private readonly StarWritingFileStore _store;
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);

        public StarWritingService(IStarProfileProvider profiles, IStarWritingAgent agent, StarWritingFileStore store)
        {
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _agent = agent ?? throw new ArgumentNullException(nameof(agent));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public string RootDirectory => _store.RootDirectory;

        public Task<StarWritingNotesState> GetStateAsync(string starId, CancellationToken cancellationToken)
        {
            return _store.LoadAsync(starId, cancellationToken);
        }

        public async Task<StarWritingCreateResult> GenerateAndReviewAsync(string starId, CancellationToken cancellationToken)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                System.Diagnostics.Stopwatch totalTimer = System.Diagnostics.Stopwatch.StartNew();
                StarProfile profile = _profiles.GetProfile(starId);
                StarWritingNotesState state = await _store.LoadAsync(profile.StarId, cancellationToken).ConfigureAwait(false);

                // Do not make a new-writing button repair legacy vectors first. Older malformed
                // ratings are historical data and must never block or add hidden LLM calls to a
                // fresh generation. They can be migrated by a separate maintenance action later.
                int repairedRatingCount = 0;

                // Deliberately staged calls: first select five unrelated word pairs without revealing the creative task;
                // then compose from those fixed pairs; finally use a separate structured critic for persistence scores.
                long generationStartedAt = totalTimer.ElapsedMilliseconds;
                StarWritingGenerationResult generation = await _agent.GenerateWritingCandidatesAsync(profile, state, cancellationToken).ConfigureAwait(false);
                NormalizeGeneration(generation);
                long generationFinishedAt = totalTimer.ElapsedMilliseconds;
                StarWritingReviewResult review = await _agent.RateWritingCandidatesAsync(profile, state, generation, cancellationToken).ConfigureAwait(false);
                NormalizeReview(review);
                long reviewFinishedAt = totalTimer.ElapsedMilliseconds;

                DateTimeOffset now = DateTimeOffset.UtcNow;
                StarWritingHistoryBatch batch = new StarWritingHistoryBatch
                {
                    Id = "writing-batch-" + Guid.NewGuid().ToString("N"),
                    GeneratedAt = now
                };

                int persistedCount = 0;
                for (int i = 0; i < generation.Items.Count; i++)
                {
                    StarWritingCandidate candidate = generation.Items[i];
                    StarWritingRating rating = review.Ratings.First(x => x.Index == i);
                    rating.EmotionalVector = StarWritingEmotionVector.Normalize(rating.EmotionalVector, out Dictionary<string, StarWritingEmotionValue> values);
                    List<string> matchedRules = GetMatchedRuleIds(profile, values);
                    string itemId = "writing-" + Guid.NewGuid().ToString("N");
                    StarWritingHistoryItem historyItem = new StarWritingHistoryItem
                    {
                        Id = itemId,
                        SourceBatchId = batch.Id,
                        GeneratedAt = now,
                        Index = i,
                        WordsUsed = candidate.WordsUsed.ToList(),
                        Writing = candidate.Writing,
                        EmotionalVector = rating.EmotionalVector,
                        Persisted = matchedRules.Count > 0,
                        MatchedPersistenceRuleIds = matchedRules
                    };
                    batch.Items.Add(historyItem);

                    if (historyItem.Persisted)
                    {
                        state.PersistedWritings.Add(new PersistedStarWriting
                        {
                            Id = itemId,
                            SourceBatchId = batch.Id,
                            WordsUsed = historyItem.WordsUsed.ToList(),
                            Writing = historyItem.Writing,
                            EmotionalVector = historyItem.EmotionalVector,
                            MatchedPersistenceRuleIds = matchedRules.ToList(),
                            GeneratedAt = now,
                            PersistedAt = now,
                            ToldCount = 0
                        });
                        persistedCount++;
                    }
                }

                state.RecentWritingHistory.AddRange(batch.Items);
                state.RecentWritingHistory = state.RecentWritingHistory
                    .OrderByDescending(x => x.GeneratedAt)
                    .Take(10)
                    .OrderBy(x => x.GeneratedAt)
                    .ThenBy(x => x.Index)
                    .ToList();
                RebuildUsedWordsFromPersistedWritings(state);
                // Once generation and review have completed, make the five statements durable even
                // if foreground activity cancels the background loop a moment later. The shared
                // append-only log and the per-Star state are written together under the store gate.
                await _store.SaveGeneratedBatchAsync(state, profile.DisplayName, batch, CancellationToken.None).ConfigureAwait(false);
                long saveFinishedAt = totalTimer.ElapsedMilliseconds;
                System.Diagnostics.Debug.WriteLine(
                    "[StarWritingTiming] " + profile.StarId
                    + " generation=" + (generationFinishedAt - generationStartedAt) + "ms"
                    + ", review=" + (reviewFinishedAt - generationFinishedAt) + "ms"
                    + ", saveAndRules=" + (saveFinishedAt - reviewFinishedAt) + "ms"
                    + ", total=" + saveFinishedAt + "ms.");

                return new StarWritingCreateResult
                {
                    State = state,
                    Batch = batch,
                    PersistedCount = persistedCount,
                    RepairedRatingCount = repairedRatingCount
                };
            }
            finally { _operationGate.Release(); }
        }

        public async Task<StarWritingPerformanceResult?> TellPendingAsync(string starId, CancellationToken cancellationToken)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StarProfile profile = _profiles.GetProfile(starId);
                StarWritingNotesState state = await _store.LoadAsync(profile.StarId, cancellationToken).ConfigureAwait(false);
                List<PersistedStarWriting> pending = state.PersistedWritings.Where(x => x != null && x.ToldCount <= 0).OrderByDescending(x => x.PersistedAt).ToList();
                if (pending.Count == 0) return null;

                StarWritingPerformancePlan plan;
                try
                {
                    plan = await _agent.PrepareWritingPerformanceAsync(profile, state, pending, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // The writings are already durable and TTS-ready. A performance-planning LLM
                    // failure must not turn the Play button into a failed bridge command with no
                    // speech. Fall back to the five newest untold pieces verbatim.
                    List<PersistedStarWriting> fallback = pending.Take(5).ToList();
                    System.Diagnostics.Debug.WriteLine(
                        "[StarWritingPerformance] " + profile.StarId
                        + " performance plan failed; using exact-writing fallback: " + ex.Message);
                    plan = new StarWritingPerformancePlan
                    {
                        StarId = profile.StarId,
                        SelectedWritingIds = fallback.Select(x => x.Id).ToList(),
                        SpokenText = fallback.Select(x => x.Writing).ToList(),
                        RawModelJson = "fallback-after-performance-plan-error: " + ex.Message
                    };
                }

                HashSet<string> pendingIds = new HashSet<string>(pending.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
                int targetCount = Math.Min(5, pending.Count);
                List<string> selectedIds = (plan.SelectedWritingIds ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x) && pendingIds.Contains(x.Trim()))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(targetCount)
                    .ToList();
                if (selectedIds.Count < targetCount)
                {
                    selectedIds.AddRange(pending
                        .Where(x => !selectedIds.Contains(x.Id, StringComparer.OrdinalIgnoreCase))
                        .Take(targetCount - selectedIds.Count)
                        .Select(x => x.Id));
                }

                List<PersistedStarWriting> selected = selectedIds
                    .Select(id => pending.First(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                List<string> spoken = (plan.SpokenText ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Take(targetCount)
                    .ToList();
                if (spoken.Count != selected.Count)
                {
                    spoken = selected.Select(x => x.Writing).ToList();
                }

                DateTimeOffset toldAt = DateTimeOffset.UtcNow;
                foreach (PersistedStarWriting item in selected)
                {
                    item.ToldCount++;
                    item.LastToldAt = toldAt;
                }
                await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);

                StarInteractionResponse response = new StarInteractionResponse
                {
                    StarId = profile.StarId,
                    EmotionalPurpose = "share_best_latest_persisted_writing",
                    VoiceDirective = CreateVoiceDirective(profile, selected)
                };
                response.SetSpokenParts(spoken);

                return new StarWritingPerformanceResult
                {
                    State = state,
                    SelectedWritings = selected,
                    Response = response
                };
            }
            finally { _operationGate.Release(); }
        }

        public async Task ClearAsync(string starId, CancellationToken cancellationToken)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await _store.ClearAsync(starId, cancellationToken).ConfigureAwait(false); }
            finally { _operationGate.Release(); }
        }

        private static void NormalizeGeneration(StarWritingGenerationResult generation)
        {
            generation.Jokes ??= new List<string>();
            generation.UsedWords ??= new List<List<string>>();
            generation.Items ??= new List<StarWritingCandidate>();

            // The public response shape is two parallel arrays. Convert it into paired internal records.
            if (generation.Jokes.Count > 0 || generation.UsedWords.Count > 0)
            {
                generation.Items = Enumerable.Range(0, Math.Min(5, Math.Max(generation.Jokes.Count, generation.UsedWords.Count)))
                    .Select(index => new StarWritingCandidate
                    {
                        Writing = index < generation.Jokes.Count ? generation.Jokes[index] ?? string.Empty : string.Empty,
                        WordsUsed = index < generation.UsedWords.Count && generation.UsedWords[index] != null
                            ? generation.UsedWords[index].ToList()
                            : new List<string>()
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Writing))
                    .ToList();
            }
            else
            {
                generation.Items = generation.Items
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Writing))
                    .Take(5)
                    .ToList();
            }

            string[][] fallbacks =
            {
                new[] { "Starlight", "Pebble" }, new[] { "Void", "Teacup" }, new[] { "Shadow", "Tapestry" },
                new[] { "Fragment", "Echo" }, new[] { "Stone", "Whisper" }
            };
            while (generation.Items.Count < 5)
            {
                int index = generation.Items.Count;
                generation.Items.Add(new StarWritingCandidate
                {
                    WordsUsed = fallbacks[index].ToList(),
                    Writing = "The " + fallbacks[index][0].ToLowerInvariant() + " regarded the " + fallbacks[index][1].ToLowerInvariant() + " and decided their silence was already an answer."
                });
            }
            generation.Items = generation.Items.Take(5).ToList();
            for (int i = 0; i < generation.Items.Count; i++)
            {
                StarWritingCandidate item = generation.Items[i];
                item.Writing = item.Writing.Trim();
                item.WordsUsed ??= new List<string>();
                item.WordsUsed = item.WordsUsed.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
                while (item.WordsUsed.Count < 2) item.WordsUsed.Add(fallbacks[i][item.WordsUsed.Count]);
            }

            // Keep the externally visible arrays synchronized with the normalized one-to-one pairing.
            generation.Jokes = generation.Items.Select(x => x.Writing).ToList();
            generation.UsedWords = generation.Items.Select(x => x.WordsUsed.ToList()).ToList();
        }

        private async Task<int> RepairIncompleteRecentRatingsAsync(
            StarProfile profile,
            StarWritingNotesState state,
            CancellationToken cancellationToken)
        {
            List<StarWritingHistoryItem> invalid = (state.RecentWritingHistory ?? new List<StarWritingHistoryItem>())
                .Where(x => x != null && NeedsRatingRepair(x))
                .OrderBy(x => x.GeneratedAt)
                .ThenBy(x => x.Index)
                .Take(10)
                .ToList();
            if (invalid.Count == 0) return 0;

            int repairedCount = 0;
            DateTimeOffset repairedAt = DateTimeOffset.UtcNow;
            for (int offset = 0; offset < invalid.Count; offset += 5)
            {
                List<StarWritingHistoryItem> group = invalid.Skip(offset).Take(5).ToList();
                StarWritingGenerationResult generation = new StarWritingGenerationResult
                {
                    StarId = profile.StarId,
                    Items = group.Select(item => new StarWritingCandidate
                    {
                        WordsUsed = (item.WordsUsed ?? new List<string>()).Take(2).ToList(),
                        Writing = item.Writing ?? string.Empty
                    }).ToList()
                };

                while (generation.Items.Count < 5)
                {
                    generation.Items.Add(new StarWritingCandidate
                    {
                        WordsUsed = new List<string> { "rating", "placeholder" },
                        Writing = "A temporary rating placeholder that will not be stored."
                    });
                }

                StarWritingReviewResult review = await _agent
                    .RateWritingCandidatesAsync(profile, state, generation, cancellationToken)
                    .ConfigureAwait(false);
                NormalizeReview(review);

                for (int i = 0; i < group.Count; i++)
                {
                    StarWritingHistoryItem item = group[i];
                    StarWritingRating rating = review.Ratings.First(x => x.Index == i);
                    rating.EmotionalVector = StarWritingEmotionVector.Normalize(
                        rating.EmotionalVector,
                        out Dictionary<string, StarWritingEmotionValue> values);
                    List<string> matchedRules = GetMatchedRuleIds(profile, values);

                    item.EmotionalVector = rating.EmotionalVector;
                    item.Persisted = matchedRules.Count > 0;
                    item.MatchedPersistenceRuleIds = matchedRules;

                    PersistedStarWriting? existing = state.PersistedWritings
                        .FirstOrDefault(x => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
                    if (item.Persisted)
                    {
                        if (existing == null)
                        {
                            state.PersistedWritings.Add(new PersistedStarWriting
                            {
                                Id = item.Id,
                                SourceBatchId = item.SourceBatchId,
                                WordsUsed = (item.WordsUsed ?? new List<string>()).ToList(),
                                Writing = item.Writing,
                                EmotionalVector = item.EmotionalVector,
                                MatchedPersistenceRuleIds = matchedRules.ToList(),
                                GeneratedAt = item.GeneratedAt,
                                PersistedAt = repairedAt,
                                ToldCount = 0
                            });
                        }
                        else
                        {
                            existing.SourceBatchId = item.SourceBatchId;
                            existing.WordsUsed = (item.WordsUsed ?? new List<string>()).ToList();
                            existing.Writing = item.Writing;
                            existing.EmotionalVector = item.EmotionalVector;
                            existing.MatchedPersistenceRuleIds = matchedRules.ToList();
                            existing.GeneratedAt = item.GeneratedAt;
                        }
                    }
                    else if (existing != null)
                    {
                        state.PersistedWritings.Remove(existing);
                    }

                    repairedCount++;
                }
            }

            if (repairedCount > 0)
            {
                RebuildUsedWordsFromPersistedWritings(state);
                await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }

            return repairedCount;
        }

        private static bool NeedsRatingRepair(StarWritingHistoryItem item)
        {
            return !StarWritingEmotionVector.TryNormalizeUsable(
                item?.EmotionalVector,
                out _,
                out _);
        }

        private static void NormalizeReview(StarWritingReviewResult review)
        {
            review.Ratings ??= new List<StarWritingRating>();
            List<StarWritingRating> source = review.Ratings.Where(x => x != null).Take(5).ToList();
            if (source.Count != 5)
            {
                throw new InvalidOperationException("Writing review must contain exactly five ratings; received " + source.Count + ".");
            }

            List<StarWritingRating> normalized = new List<StarWritingRating>();
            HashSet<StarWritingRating> used = new HashSet<StarWritingRating>();
            for (int i = 0; i < 5; i++)
            {
                StarWritingRating? rating = source.FirstOrDefault(x => !used.Contains(x) && x.Index == i)
                    ?? source.FirstOrDefault(x => !used.Contains(x));
                if (rating == null)
                {
                    throw new InvalidOperationException("Writing review is missing rating index " + i + ".");
                }

                used.Add(rating);
                if (!StarWritingEmotionVector.TryNormalizeUsable(
                    rating.EmotionalVector,
                    out string emotionalVector,
                    out _))
                {
                    throw new InvalidOperationException(
                        "Writing review rating " + i + " did not contain 27 valid scores in category order.");
                }

                rating.Index = i;
                rating.EmotionalVector = emotionalVector;
                normalized.Add(rating);
            }

            review.Ratings = normalized;
        }

        private static void RebuildUsedWordsFromPersistedWritings(StarWritingNotesState state)
        {
            state.AllUsedWords = (state.PersistedWritings ?? new List<PersistedStarWriting>())
                .Where(x => x != null)
                .OrderBy(x => x.PersistedAt)
                .ThenBy(x => x.GeneratedAt)
                .SelectMany(x => x.WordsUsed ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> GetMatchedRuleIds(StarProfile profile, IReadOnlyDictionary<string, StarWritingEmotionValue> values)
        {
            // The review model occasionally emits an inflated Overall value while craft dimensions remain low.
            // Require a broader score consensus before any character route or the global backstop can persist it.
            if (!PassesPersistenceQualityGate(profile.WritingPersistenceQualityGate, values))
            {
                return new List<string>();
            }

            List<StarWritingPersistenceRule> rules = profile.WritingPersistenceRules ?? new List<StarWritingPersistenceRule>();
            if (rules.Count == 0)
            {
                rules = new List<StarWritingPersistenceRule>
                {
                    new StarWritingPersistenceRule
                    {
                        Id = "default-overall",
                        Requirements = new List<StarWritingPersistenceRequirement>
                        {
                            new StarWritingPersistenceRequirement { Category = "Overall", MinimumScore = 75 }
                        }
                    }
                };
            }

            List<string> matched = new List<string>();
            foreach (StarWritingPersistenceRule rule in rules.Where(x => x != null && x.Requirements != null && x.Requirements.Count > 0))
            {
                IEnumerable<bool> checks = rule.Requirements.Select(requirement =>
                {
                    if (!values.TryGetValue(requirement.Category ?? string.Empty, out StarWritingEmotionValue? value)) return false;
                    return value.Score >= requirement.MinimumScore;
                });
                bool isAny = string.Equals(rule.MatchMode, "any", StringComparison.OrdinalIgnoreCase);
                bool qualifies = isAny ? checks.Any(x => x) : checks.All(x => x);
                if (qualifies) matched.Add(string.IsNullOrWhiteSpace(rule.Id) ? "writing-rule" : rule.Id.Trim());
            }

            // Every Star also gets the same conservative excellence backstop. The comparison is
            // deliberately strict: an Overall score of exactly 80 does not qualify at the default
            // threshold, while 80.01 and above do. Character-specific rules remain the primary
            // route; this catches rare excellent pieces whose strengths do not fit their rule shape.
            if (values.TryGetValue("Overall", out StarWritingEmotionValue? overall)
                && overall.Score > GlobalOverallThreshold
                && !matched.Contains(GlobalOverallRuleId, StringComparer.OrdinalIgnoreCase))
            {
                matched.Add(GlobalOverallRuleId);
            }

            return matched;
        }

        private static bool PassesPersistenceQualityGate(
            StarWritingPersistenceQualityGate? gate,
            IReadOnlyDictionary<string, StarWritingEmotionValue> values)
        {
            if (gate == null) return true;

            if (gate.MinimumOverallScore > 0)
            {
                if (!values.TryGetValue("Overall", out StarWritingEmotionValue? overall)
                    || overall.Score < gate.MinimumOverallScore)
                {
                    return false;
                }
            }

            List<StarWritingPersistenceRequirement> supporting = (gate.SupportingRequirements
                    ?? new List<StarWritingPersistenceRequirement>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Category))
                .ToList();
            if (supporting.Count == 0) return true;

            int requiredMatches = gate.MinimumSupportingMatches <= 0
                ? supporting.Count
                : Math.Min(gate.MinimumSupportingMatches, supporting.Count);
            int matched = supporting.Count(requirement =>
                values.TryGetValue(requirement.Category, out StarWritingEmotionValue? value)
                && value.Score >= requirement.MinimumScore);
            return matched >= requiredMatches;
        }

        private static double ReadGlobalOverallThreshold()
        {
            string? configured = Environment.GetEnvironmentVariable("WDS_STAR_WRITING_GLOBAL_OVERALL_THRESHOLD");
            if (!string.IsNullOrWhiteSpace(configured)
                && double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                return Math.Max(0.0, Math.Min(100.0, parsed));
            }

            return DefaultGlobalOverallThreshold;
        }

        private static IEnumerable<KeyValuePair<string, StarWritingEmotionValue>> ReadEmotionValues(PersistedStarWriting writing)
        {
            StarWritingEmotionVector.Normalize(writing?.EmotionalVector, out Dictionary<string, StarWritingEmotionValue> values);
            return values;
        }

        private static VoiceDirective CreateVoiceDirective(StarProfile profile, IReadOnlyList<PersistedStarWriting> selected)
        {
            string primary = "Poetic";
            double intensity = 0.65;
            if (selected.Count > 0)
            {
                HashSet<string> speakableEmotions = new HashSet<string>(new[]
                {
                    "Funny", "Deep", "Intrigued", "Interested", "Playful", "Surprising",
                    "Mysterious", "Haunting", "Melancholy", "Hopeful", "Romantic", "Moving"
                }, StringComparer.OrdinalIgnoreCase);
                var strongest = selected
                    .SelectMany(ReadEmotionValues)
                    .Where(x => speakableEmotions.Contains(x.Key))
                    .OrderByDescending(x => x.Value.Score)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(strongest.Key))
                {
                    primary = strongest.Key;
                    intensity = Math.Max(0.25, Math.Min(1, strongest.Value.Score / 100.0));
                }
            }
            VoiceDirective directive = new VoiceDirective
            {
                VoiceId = string.IsNullOrWhiteSpace(profile.VoiceId) ? profile.StarId : profile.VoiceId,
                VoiceDesignPrompt = string.IsNullOrWhiteSpace(profile.VoiceDesignPrompt) ? profile.VoiceDirection : profile.VoiceDesignPrompt,
                VoiceCloneAudioFile = profile.VoiceCloneAudioFile,
                VoiceCloneRefText = profile.VoiceCloneRefText,
                DeliveryInstructions = "Speak in character as " + profile.DisplayName + ". Present each chosen joke or poetic fragment as something personally composed and worth sharing. Preserve timing, pauses, wit, and emotional restraint.",
                PrimaryEmotion = primary,
                EmotionIntensity = intensity,
                Pace = "slow-medium, with deliberate comic and poetic timing",
                Pitch = "natural",
                Volume = "normal-soft",
                Prosody = "intimate, clear, expressive, never rushed",
                Language = "Auto"
            };
            directive.Avoid.Add("robotic list reading");
            directive.Avoid.Add("breaking character");
            directive.Avoid.Add("explaining the scoring vector aloud");
            return directive;
        }
    }
}
