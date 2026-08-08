using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Writings
{
    public sealed class StarWritingService
    {
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
                StarProfile profile = _profiles.GetProfile(starId);
                StarWritingNotesState state = await _store.LoadAsync(profile.StarId, cancellationToken).ConfigureAwait(false);

                // Deliberately separate calls: the first creates freely in character; the second acts as a structured critic.
                StarWritingGenerationResult generation = await _agent.GenerateWritingCandidatesAsync(profile, state, cancellationToken).ConfigureAwait(false);
                NormalizeGeneration(generation);
                StarWritingReviewResult review = await _agent.RateWritingCandidatesAsync(profile, state, generation, cancellationToken).ConfigureAwait(false);
                NormalizeReview(review);

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
                    StarWritingRating rating = review.Ratings.FirstOrDefault(x => x.Index == i) ?? review.Ratings.ElementAtOrDefault(i) ?? CreateZeroRating(i);
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
                AddUsedWords(state, generation.Items.SelectMany(x => x.WordsUsed));
                await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);

                return new StarWritingCreateResult { State = state, Batch = batch, PersistedCount = persistedCount };
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

                StarWritingPerformancePlan plan = await _agent.PrepareWritingPerformanceAsync(profile, state, pending, cancellationToken).ConfigureAwait(false);
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

        private static void NormalizeReview(StarWritingReviewResult review)
        {
            review.Ratings ??= new List<StarWritingRating>();
            List<StarWritingRating> normalized = new List<StarWritingRating>();
            for (int i = 0; i < 5; i++)
            {
                StarWritingRating rating = review.Ratings.FirstOrDefault(x => x.Index == i) ?? review.Ratings.ElementAtOrDefault(i) ?? CreateZeroRating(i);
                rating.Index = i;
                rating.EmotionalVector = StarWritingEmotionVector.Normalize(rating.EmotionalVector, out _);
                normalized.Add(rating);
            }
            review.Ratings = normalized;
        }

        private static StarWritingRating CreateZeroRating(int index)
        {
            string vector = StarWritingEmotionVector.Normalize(string.Empty, out _);
            return new StarWritingRating { Index = index, EmotionalVector = vector };
        }

        private static void AddUsedWords(StarWritingNotesState state, IEnumerable<string> words)
        {
            HashSet<string> seen = new HashSet<string>(state.AllUsedWords ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (string word in words.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
            {
                if (seen.Add(word)) state.AllUsedWords.Add(word);
            }
        }

        private static List<string> GetMatchedRuleIds(StarProfile profile, IReadOnlyDictionary<string, StarWritingEmotionValue> values)
        {
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
                            new StarWritingPersistenceRequirement { Category = "Overall", MinimumScore = 75, MinimumConfidence = 0.70 }
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
                    return value.Score >= requirement.MinimumScore && value.Confidence >= requirement.MinimumConfidence;
                });
                bool isAny = string.Equals(rule.MatchMode, "any", StringComparison.OrdinalIgnoreCase);
                bool qualifies = isAny ? checks.Any(x => x) : checks.All(x => x);
                if (qualifies) matched.Add(string.IsNullOrWhiteSpace(rule.Id) ? "writing-rule" : rule.Id.Trim());
            }
            return matched;
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
                    .OrderByDescending(x => x.Value.Score * x.Value.Confidence)
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
