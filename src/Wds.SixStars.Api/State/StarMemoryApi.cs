using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.State
{
    /// <summary>
    /// Owns the durable relationship/memory state for each Star. SixStarsRuntime asks this API
    /// for the latest state before model calls and lets it apply the accepted state changes after
    /// each interaction. The store underneath may be a JSON file for the desktop harness or a Unity
    /// save object in the game.
    /// </summary>
    public sealed class StarMemoryApi : IStarMemoryApi
    {
        private readonly IStarStateStore _stateStore;
        private readonly StarMemoryOptions _options;

        public StarMemoryApi(IStarStateStore stateStore)
            : this(stateStore, new StarMemoryOptions())
        {
        }

        public StarMemoryApi(IStarStateStore stateStore, StarMemoryOptions options)
        {
            _stateStore = stateStore;
            _options = options;
        }

        public Task<StarState> GetStarStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            return _stateStore.GetAsync(sessionId, starId, cancellationToken);
        }

        public async Task<StarMemorySnapshot> GetStarMemorySnapshotAsync(string sessionId, string starId, int recentHistoryCount, CancellationToken cancellationToken)
        {
            StarState state = await _stateStore.GetAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            int count = recentHistoryCount <= 0 ? _options.MaxRecentHistoryForPrompt : recentHistoryCount;
            return new StarMemorySnapshot
            {
                State = state,
                RecentHistory = state.MemoryLog.Take(count).ToList()
            };
        }

        public async Task<StarInteractionHistoryPage> GetStarInteractionHistoryAsync(string sessionId, string starId, int skip, int take, CancellationToken cancellationToken)
        {
            StarState state = await _stateStore.GetAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            int safeSkip = Math.Max(0, skip);
            int safeTake = Math.Max(1, Math.Min(500, take <= 0 ? 50 : take));
            return new StarInteractionHistoryPage
            {
                SessionId = sessionId,
                StarId = starId,
                Skip = safeSkip,
                Take = safeTake,
                TotalCount = state.MemoryLog.Count,
                Entries = state.MemoryLog.Skip(safeSkip).Take(safeTake).ToList()
            };
        }

        public async Task<StarStateUpdateResult> UpdateStarWithInteractionAsync(StarStateUpdateRequest request, CancellationToken cancellationToken)
        {
            StarState state = await _stateStore.GetAsync(request.SessionId, request.StarId, cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            state.StarId = string.IsNullOrWhiteSpace(state.StarId) ? request.StarId : state.StarId;
            state.InteractionCount += 1;
            state.LastInteractionAt = now;

            RelationshipDelta sourceDelta = _options.AcceptModelSuggestedDeltas ? request.SuggestedDelta : request.ActualDelta;
            RelationshipDelta applied = new RelationshipDelta
            {
                Heart = sourceDelta.Heart,
                Trust = sourceDelta.Trust,
                Romance = sourceDelta.Romance,
                BenaveilInfluence = sourceDelta.BenaveilInfluence,
                // The connection meter is always deterministic even if a dev toggle accepts model heart/trust/romance suggestions.
                EmotionalConnectionPercent = request.ActualDelta.EmotionalConnectionPercent
            };
            double emotionalConnectionBefore = state.EmotionalConnectionPercent;
            ApplyRelationshipDelta(state, applied);
            UpdateGirlfriendReadiness(state, now);

            if (!string.IsNullOrWhiteSpace(request.ConversationSummary))
            {
                state.ConversationSummary = request.ConversationSummary.Trim();
            }

            AddUnique(state.ImportantPlayerQuotes, request.ImportantPlayerQuotes, _options.MaxImportantQuotes);
            AddUnique(state.MemoryNotes, request.MemoryNotes, _options.MaxMemoryNotes);
            AddUnique(state.SecretsUnlocked, request.SecretsUnlocked, _options.MaxSecrets);
            AddUnique(state.RelationshipEvents, request.RelationshipEvents, _options.MaxRelationshipEvents);
            AddUnique(state.GirlfriendHistory, request.GirlfriendHistoryUpdates, _options.MaxGirlfriendHistoryEntries);
            AddUnique(state.PetNamesPlayerUsesForStar, request.PetNamesPlayerUsesForStar, 20);
            AddUnique(state.PetNamesStarUsesForPlayer, request.PetNamesStarUsesForPlayer, 20);
            UpsertLearnedFunctions(state, request.LearnedFunctions);
            UpsertSecrets(state, request.SecretsUnlocked, now);
            UpdateCombos(state, request.DetectedCombos, now);
            UpdateSuccessionCounters(state, applied, request, now);
            UpdateEmotionStateHistory(state, request, applied, now, emotionalConnectionBefore, state.EmotionalConnectionPercent);
            UpdateStoryHistory(state, request, now);

            string autoQuote = TryCreateAutoQuote(request, applied);
            if (!string.IsNullOrWhiteSpace(autoQuote))
            {
                AddUnique(state.ImportantPlayerQuotes, new[] { autoQuote }, _options.MaxImportantQuotes);
            }

            ConversationLogEntry entry = new ConversationLogEntry
            {
                CreatedAt = now,
                RequestType = request.RequestType,
                PlayerText = request.PlayerText ?? string.Empty,
                StarResponse = request.StarResponse ?? string.Empty,
                Summary = string.IsNullOrWhiteSpace(request.ChangesSummary) ? request.ConversationSummary : request.ChangesSummary,
                ActualDelta = request.ActualDelta,
                SuggestedDelta = request.SuggestedDelta,
                EmotionalConnectionPercentBefore = emotionalConnectionBefore,
                EmotionalConnectionPercentAfter = state.EmotionalConnectionPercent,
                IsGirlfriendReadyAfter = state.IsGirlfriendReady,
                EmotionalEffectVector = request.EmotionalEffectVector ?? string.Empty,
                NegativeEmotionVector = request.NegativeEmotionVector ?? string.Empty,
                DeterministicEmotionSummary = request.DeterministicEmotionSummary ?? string.Empty,
                WisdomScore = request.WisdomScore
            };
            entry.ImportantQuotes.AddRange(request.ImportantPlayerQuotes);
            if (!string.IsNullOrWhiteSpace(autoQuote)) entry.ImportantQuotes.Add(autoQuote);
            entry.Tags.AddRange(request.DetectedCombos);
            entry.RelationshipEvents.AddRange(request.RelationshipEvents);
            entry.AcceptedPositiveSignals.AddRange(request.AcceptedPositiveSignals);
            entry.AcceptedNegativeSignals.AddRange(request.AcceptedNegativeSignals);
            entry.AcceptedCombos.AddRange(request.AcceptedCombos);
            entry.LearnedFunctionIds.AddRange(request.LearnedFunctions.Select(x => x.Id).Where(x => !string.IsNullOrWhiteSpace(x)));
            entry.SecretsUnlocked.AddRange(request.SecretsUnlocked);

            state.MemoryLog.Insert(0, entry);
            Trim(state.MemoryLog, _options.MaxMemoryLogEntries);

            await _stateStore.SaveAsync(request.SessionId, state, cancellationToken).ConfigureAwait(false);

            StarStateUpdateResult result = new StarStateUpdateResult
            {
                State = state,
                LogEntry = entry
            };
            result.UnityEvents.Add(CreateMemoryUpdatedEvent(request.StarId, state, entry, applied, _options.AcceptModelSuggestedDeltas));
            if (IsStoryRequest(request.RequestType) && (!string.IsNullOrWhiteSpace(request.StoryTitle) || !string.IsNullOrWhiteSpace(request.StoryId)))
            {
                result.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "StarStoryRemembered",
                    Target = request.StarId,
                    Data =
                    {
                        { "storyId", request.StoryId ?? string.Empty },
                        { "title", request.StoryTitle ?? string.Empty },
                        { "finished", request.StoryFinished.ToString() }
                    }
                });
            }
            foreach (string combo in request.DetectedCombos)
            {
                result.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "StarComboUpdated",
                    Target = request.StarId,
                    Data = { { "combo", combo } }
                });
            }
            foreach (StarLearnedFunction learned in request.LearnedFunctions)
            {
                result.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "StarLearnedFunctionUpdated",
                    Target = request.StarId,
                    Data =
                    {
                        { "functionId", learned.Id },
                        { "name", learned.Name }
                    }
                });
            }

            return result;
        }

        public async Task<SixStarsSaveData> ExportSaveDataAsync(string sessionId, CancellationToken cancellationToken)
        {
            IReadOnlyList<StarState> states = await _stateStore.GetAllAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return new SixStarsSaveData
            {
                Version = 1,
                SessionId = sessionId,
                SavedAt = DateTimeOffset.UtcNow,
                Stars = states.ToList()
            };
        }

        public Task ImportSaveDataAsync(SixStarsSaveData saveData, CancellationToken cancellationToken)
        {
            string sessionId = string.IsNullOrWhiteSpace(saveData.SessionId) ? "local-dev" : saveData.SessionId;
            return _stateStore.ReplaceSessionAsync(sessionId, saveData.Stars, cancellationToken);
        }


        public async Task<StarState> ResetStarMemoryAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            await _stateStore.ResetStarAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            return await _stateStore.GetAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
        }



        public async Task<StarState> SetStarAllyAsync(string sessionId, string starId, bool isAlly, CancellationToken cancellationToken)
        {
            StarState state = await _stateStore.GetAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            state.IsAlly = isAlly;
            state.RelationshipEvents.Insert(0, (isAlly ? "Became ally" : "Ally flag removed") + " via host/gameplay state.");
            Trim(state.RelationshipEvents, _options.MaxRelationshipEvents);
            await _stateStore.SaveAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            return state;
        }



        private static void UpdateEmotionStateHistory(StarState state, StarStateUpdateRequest request, RelationshipDelta applied, DateTimeOffset now, double emotionalConnectionBefore, double emotionalConnectionAfter)
        {
            state.LastEmotionalEffectVector = request.EmotionalEffectVector ?? string.Empty;
            state.LastNegativeEmotionVector = request.NegativeEmotionVector ?? string.Empty;
            state.LastDeterministicEmotionSummary = request.DeterministicEmotionSummary ?? string.Empty;
            if (string.IsNullOrWhiteSpace(state.EmotionalConnectionSkillLevel))
            {
                state.EmotionalConnectionSkillLevel = "interested_private_potential_partner";
            }

            state.EmotionUpdateHistory.Insert(0, new EmotionUpdateHistoryEntry
            {
                CreatedAt = now,
                RequestType = request.RequestType,
                PlayerText = request.PlayerText ?? string.Empty,
                EmotionalEffectVector = request.EmotionalEffectVector ?? string.Empty,
                NegativeEmotionVector = request.NegativeEmotionVector ?? string.Empty,
                AcceptedPositiveSignals = request.AcceptedPositiveSignals.ToList(),
                AcceptedNegativeSignals = request.AcceptedNegativeSignals.ToList(),
                AcceptedCombos = request.AcceptedCombos.ToList(),
                SuggestedDelta = request.SuggestedDelta,
                ActualDelta = applied,
                EmotionalConnectionPercentBefore = emotionalConnectionBefore,
                EmotionalConnectionPercentAfter = emotionalConnectionAfter,
                IsGirlfriendReadyAfter = state.IsGirlfriendReady,
                Summary = request.DeterministicEmotionSummary ?? string.Empty
            });
            Trim(state.EmotionUpdateHistory, 80);
        }

        private static void UpdateStoryHistory(StarState state, StarStateUpdateRequest request, DateTimeOffset now)
        {
            if (!IsStoryRequest(request.RequestType))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(request.StoryTitle)
                && string.IsNullOrWhiteSpace(request.StorySummary)
                && string.IsNullOrWhiteSpace(request.StoryId))
            {
                return;
            }

            string title = string.IsNullOrWhiteSpace(request.StoryTitle) ? "Untitled story" : request.StoryTitle.Trim();
            string id = string.IsNullOrWhiteSpace(request.StoryId) ? Slugify(title) : request.StoryId.Trim();
            StarStoryHistoryEntry? existing = state.StoryHistory.FirstOrDefault(x => string.Equals(x.StoryId, id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new StarStoryHistoryEntry
                {
                    StoryId = id,
                    StarId = request.StarId,
                    Title = title,
                    StartedAt = now,
                    Seed = request.StorySeed ?? string.Empty
                };
                state.StoryHistory.Insert(0, existing);
            }

            existing.Title = title;
            if (!string.IsNullOrWhiteSpace(request.StorySummary)) existing.Summary = request.StorySummary.Trim();
            if (!string.IsNullOrWhiteSpace(request.StorySeed)) existing.Seed = request.StorySeed.Trim();
            existing.Finished = request.StoryFinished;
            if (request.StoryFinished && existing.CompletedAt == null) existing.CompletedAt = now;
            Trim(state.StoryHistory, 80);
        }


        private static bool IsStoryRequest(string requestType)
        {
            return string.Equals(requestType, RequestType.Story.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyRelationshipDelta(StarState state, RelationshipDelta delta)
        {
            state.Heart += delta.Heart;
            state.Trust += delta.Trust;
            state.Romance += delta.Romance;
            state.BenaveilInfluence += delta.BenaveilInfluence;
            state.LastEmotionalConnectionPercentDelta = delta.EmotionalConnectionPercent;
            state.EmotionalConnectionPercent = ClampPercent(state.EmotionalConnectionPercent + delta.EmotionalConnectionPercent);
        }

        private static void UpdateGirlfriendReadiness(StarState state, DateTimeOffset now)
        {
            bool ready = state.IsGirlfriend || (state.IsAlly && state.EmotionalConnectionPercent >= 85 && state.Heart >= 20 && state.Trust >= 15 && state.Romance >= 12);
            if (ready && !state.IsGirlfriendReady)
            {
                state.IsGirlfriendReady = true;
                state.GirlfriendReadySince = now;
                state.RelationshipEvents.Insert(0, "Girlfriend readiness gate reached: ally + emotional connection + heart/trust/romance thresholds.");
            }
            else if (!ready && state.IsGirlfriendReady && !state.IsGirlfriend)
            {
                state.IsGirlfriendReady = false;
                state.GirlfriendReadySince = null;
                state.RelationshipEvents.Insert(0, "Girlfriend readiness gate no longer met after connection/relationship change.");
            }

            state.GirlfriendReadinessSummary = ready
                ? "Ready for girlfriend path if the story/game allows it."
                : "Not ready: requires ally status, EmotionalConnectionPercent >= 85, Heart >= 20, Trust >= 15, and Romance >= 12.";
        }

        private static double ClampPercent(double value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }

        private void UpsertLearnedFunctions(StarState state, IEnumerable<StarLearnedFunction> functions)
        {
            foreach (StarLearnedFunction learned in functions)
            {
                if (string.IsNullOrWhiteSpace(learned.Id) && string.IsNullOrWhiteSpace(learned.Name)) continue;
                if (string.IsNullOrWhiteSpace(learned.Id)) learned.Id = Slugify(learned.Name);
                StarLearnedFunction? existing = state.LearnedFunctions.FirstOrDefault(x => string.Equals(x.Id, learned.Id, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    if (learned.LearnedAt == default(DateTimeOffset)) learned.LearnedAt = DateTimeOffset.UtcNow;
                    state.LearnedFunctions.Insert(0, learned);
                }
                else
                {
                    existing.Name = string.IsNullOrWhiteSpace(learned.Name) ? existing.Name : learned.Name;
                    existing.Description = string.IsNullOrWhiteSpace(learned.Description) ? existing.Description : learned.Description;
                    existing.SourcePlayerQuote = string.IsNullOrWhiteSpace(learned.SourcePlayerQuote) ? existing.SourcePlayerQuote : learned.SourcePlayerQuote;
                    existing.Confidence = Math.Max(existing.Confidence, learned.Confidence);
                    existing.IsActive = learned.IsActive;
                    AddUnique(existing.Tags, learned.Tags, 20);
                }
            }

            Trim(state.LearnedFunctions, _options.MaxLearnedFunctions);
        }

        private void UpsertSecrets(StarState state, IEnumerable<string> secretIds, DateTimeOffset now)
        {
            foreach (string secret in secretIds)
            {
                if (string.IsNullOrWhiteSpace(secret)) continue;
                string id = Slugify(secret);
                if (state.SecretStates.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                state.SecretStates.Insert(0, new StarSecretState
                {
                    Id = id,
                    Title = secret.Trim(),
                    Summary = secret.Trim(),
                    Source = "model_response",
                    UnlockedAt = now,
                    KnownByPlayer = true,
                    KnownByStar = true
                });
            }

            Trim(state.SecretStates, _options.MaxSecrets);
        }

        private static void UpdateCombos(StarState state, IEnumerable<string> combos, DateTimeOffset now)
        {
            foreach (string combo in combos)
            {
                if (string.IsNullOrWhiteSpace(combo)) continue;
                RelationshipComboState? existing = state.RelationshipCombos.FirstOrDefault(x => string.Equals(x.Id, combo, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    state.RelationshipCombos.Insert(0, new RelationshipComboState
                    {
                        Id = combo,
                        Name = combo,
                        Count = 1,
                        LastTriggeredAt = now,
                        Summary = "Triggered during conversation."
                    });
                }
                else
                {
                    existing.Count += 1;
                    existing.LastTriggeredAt = now;
                }
            }
        }

        private static void UpdateSuccessionCounters(StarState state, RelationshipDelta delta, StarStateUpdateRequest request, DateTimeOffset now)
        {
            bool positive = delta.Heart > 0 || delta.Trust > 0 || delta.Romance > 0;
            bool negative = delta.Heart < 0 || delta.Trust < 0 || delta.Romance < 0;
            bool romantic = delta.Romance > 0
                || request.EmotionScores.Any(x => IsRomanceEmotion(x.Key) && x.Value.Score >= 70 && x.Value.Confidence >= 0.70)
                || request.AcceptedPositiveSignals.Any(x => StartsWithAny(x, "Flattered", "Attracted", "Tender", "Yearning", "Affectionate"));
            bool hurtful = delta.Heart < -1 || delta.Trust < -1
                || request.EmotionScores.Any(x => IsHurtEmotion(x.Key) && x.Value.Score >= 75 && x.Value.Confidence >= 0.70)
                || request.AcceptedNegativeSignals.Any(x => StartsWithAny(x, "Hurt", "Unsafe", "CreepedOut", "Disrespected", "Pressured"));

            state.PositiveSuccessionCount = positive && !negative ? state.PositiveSuccessionCount + 1 : 0;
            state.RomanticSuccessionCount = romantic ? state.RomanticSuccessionCount + 1 : 0;
            state.HurtSuccessionCount = hurtful ? state.HurtSuccessionCount + 1 : 0;
            state.LastPositiveInteractionAt = positive && !negative ? now : state.LastPositiveInteractionAt;
            state.LastRomanticInteractionAt = romantic ? now : state.LastRomanticInteractionAt;
            state.LastHurtInteractionAt = hurtful ? now : state.LastHurtInteractionAt;
        }

        private string TryCreateAutoQuote(StarStateUpdateRequest request, RelationshipDelta delta)
        {
            if (!_options.AutoCaptureHighImpactPlayerQuotes) return string.Empty;
            string text = (request.PlayerText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            if (text.Length > 500) text = text.Substring(0, 500).Trim() + "...";

            bool strongDelta = Math.Abs(delta.Heart) >= 4 || Math.Abs(delta.Trust) >= 4 || Math.Abs(delta.Romance) >= 3;
            bool secretUnlocked = request.SecretsUnlocked.Count > 0;
            bool highEmotion = request.EmotionScores.Any(x => x.Value.Score >= 88 && x.Value.Confidence >= 0.80)
                || Math.Abs(delta.EmotionalConnectionPercent) >= 1.5;
            if (!strongDelta && !secretUnlocked && !highEmotion) return string.Empty;

            return text;
        }

        private static UnityEventEnvelope CreateMemoryUpdatedEvent(string starId, StarState state, ConversationLogEntry entry, RelationshipDelta delta, bool acceptedModelDelta)
        {
            return new UnityEventEnvelope
            {
                Type = "StarMemoryUpdated",
                Target = starId,
                Data =
                {
                    { "interactionCount", state.InteractionCount.ToString() },
                    { "conversationSummary", state.ConversationSummary ?? string.Empty },
                    { "lastChangeSummary", entry.Summary ?? string.Empty },
                    { "heartDelta", delta.Heart.ToString() },
                    { "trustDelta", delta.Trust.ToString() },
                    { "romanceDelta", delta.Romance.ToString() },
                    { "benaveilInfluenceDelta", delta.BenaveilInfluence.ToString() },
                    { "emotionalConnectionPercentDelta", delta.EmotionalConnectionPercent.ToString("0.00") },
                    { "emotionalConnectionPercent", state.EmotionalConnectionPercent.ToString("0.0") },
                    { "isGirlfriendReady", state.IsGirlfriendReady ? "true" : "false" },
                    { "girlfriendReadinessSummary", state.GirlfriendReadinessSummary ?? string.Empty },
                    { "acceptedModelSuggestedDeltas", acceptedModelDelta ? "true" : "false" },
                    { "isAlly", state.IsAlly ? "true" : "false" },
                    { "emotionalConnectionSkillLevel", state.EmotionalConnectionSkillLevel ?? string.Empty },
                    { "deterministicEmotionSummary", entry.DeterministicEmotionSummary ?? string.Empty }
                }
            };
        }

        private static bool StartsWithAny(string value, params string[] prefixes)
        {
            foreach (string prefix in prefixes)
            {
                if ((value ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsRomanceEmotion(string key)
        {
            return key == "Flattered" || key == "Attracted" || key == "Tender" || key == "Yearning" || key == "Jealous" || key == "Affectionate";
        }

        private static bool IsHurtEmotion(string key)
        {
            return key == "Hurt" || key == "Sad" || key == "Angry" || key == "Frustrated" || key == "Suspicious" || key == "Guilty" || key == "Lonely";
        }

        private static void AddUnique(List<string> target, IEnumerable<string> source, int maxCount)
        {
            foreach (string item in source)
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                string trimmed = item.Trim();
                if (target.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase))) continue;
                target.Insert(0, trimmed);
            }
            Trim(target, maxCount);
        }

        private static void Trim<T>(List<T> target, int maxCount)
        {
            while (target.Count > Math.Max(1, maxCount))
            {
                target.RemoveAt(target.Count - 1);
            }
        }

        private static string Slugify(string value)
        {
            string raw = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (raw.Length == 0) return Guid.NewGuid().ToString("N");
            char[] chars = raw.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            string slug = new string(chars).Trim('_');
            while (slug.Contains("__")) slug = slug.Replace("__", "_");
            return string.IsNullOrWhiteSpace(slug) ? Guid.NewGuid().ToString("N") : slug;
        }
    }
}
