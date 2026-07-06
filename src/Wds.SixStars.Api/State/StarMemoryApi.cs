using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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

        public async Task<StarState> GetStarStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            StarState state = await _stateStore.GetAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            if (NormalizeWorldDefaults(state) || PruneExpiredEffectors(state, DateTimeOffset.UtcNow))
            {
                await _stateStore.SaveAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            }
            return state;
        }

        public async Task<StarMemorySnapshot> GetStarMemorySnapshotAsync(string sessionId, string starId, int recentHistoryCount, CancellationToken cancellationToken)
        {
            StarState state = await _stateStore.GetAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            if (NormalizeWorldDefaults(state) || PruneExpiredEffectors(state, DateTimeOffset.UtcNow))
            {
                await _stateStore.SaveAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            }
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
            NormalizeWorldDefaults(state);
            PruneExpiredEffectors(state, now);

            state.StarId = string.IsNullOrWhiteSpace(state.StarId) ? request.StarId : state.StarId;
            state.InteractionCount += 1;
            state.LastInteractionAt = now;
            state.LastDailyStateSourceUpdateAt = now;

            RelationshipDelta applied = new RelationshipDelta
            {
                // Heart/Trust/Romance are always deterministic now. The model can provide emotional evidence,
                // but it cannot directly award relationship points even if the legacy dev toggle is enabled.
                Heart = request.ActualDelta.Heart,
                Trust = request.ActualDelta.Trust,
                Romance = request.ActualDelta.Romance,
                BenaveilInfluence = request.ActualDelta.BenaveilInfluence,
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
            List<StarRememberedMarker> markersToStore = PrepareRememberedMarkers(request, now);
            UpsertRememberedMarkers(state, markersToStore, now);
            AddImportantQuoteSourceRecords(state, request, request.ImportantPlayerQuotes, markersToStore, applied, now, "model-important-quote");
            MarkReferencedMemoryMarkers(state, request.ReferencedMemoryMarkerIds, now);
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
            PruneExpiredEffectors(state, now);

            string autoQuote = TryCreateAutoQuote(request, applied);
            if (!string.IsNullOrWhiteSpace(autoQuote))
            {
                AddUnique(state.ImportantPlayerQuotes, new[] { autoQuote }, _options.MaxImportantQuotes);
                AddImportantQuoteSourceRecords(state, request, new[] { autoQuote }, markersToStore, applied, now, "auto-emotion-marker-quote");
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
            entry.RememberedMarkers.AddRange(markersToStore);
            entry.ReferencedMemoryMarkerIds.AddRange(request.ReferencedMemoryMarkerIds);
            entry.Tags.AddRange(request.DetectedCombos);
            entry.RelationshipEvents.AddRange(request.RelationshipEvents);
            entry.AcceptedPositiveSignals.AddRange(request.AcceptedPositiveSignals);
            entry.AcceptedNegativeSignals.AddRange(request.AcceptedNegativeSignals);
            entry.AcceptedCombos.AddRange(request.AcceptedCombos);
            entry.LearnedFunctionIds.AddRange(request.LearnedFunctions.Select(x => x.Id).Where(x => !string.IsNullOrWhiteSpace(x)));
            entry.SecretsUnlocked.AddRange(request.SecretsUnlocked);

            state.MemoryLog.Insert(0, entry);
            ArchiveOldConversationTurns(state, now);
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
            foreach (StarRememberedMarker marker in markersToStore)
            {
                result.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "StarMemoryMarkerRemembered",
                    Target = request.StarId,
                    Data =
                    {
                        { "markerId", marker.Id ?? string.Empty },
                        { "markerName", marker.MarkerName ?? string.Empty },
                        { "coordinates", marker.Coordinates ?? string.Empty },
                        { "locationText", marker.LocationText ?? string.Empty },
                        { "description", marker.Description ?? string.Empty }
                    }
                });
            }
            foreach (string markerId in request.ReferencedMemoryMarkerIds.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                result.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "StarMemoryMarkerReferenced",
                    Target = request.StarId,
                    Data = { { "markerId", markerId } }
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



        public Task<StarState> SetStarAllyAsync(string sessionId, string starId, bool isAlly, CancellationToken cancellationToken)
        {
            return SetStarWorldStateAsync(new StarWorldStateUpdateRequest
            {
                SessionId = sessionId,
                StarId = starId,
                IsAlly = isAlly,
                IsMet = isAlly ? true : (bool?)null,
                Reason = "ally flag changed through host/gameplay API"
            }, cancellationToken);
        }

        public Task<StarState> SetStarMetAsync(string sessionId, string starId, bool isMet, CancellationToken cancellationToken)
        {
            return SetStarWorldStateAsync(new StarWorldStateUpdateRequest
            {
                SessionId = sessionId,
                StarId = starId,
                IsMet = isMet,
                IsAlly = isMet ? (bool?)null : false,
                Reason = "met flag changed through host/gameplay API"
            }, cancellationToken);
        }

        public async Task<StarState> SetStarWorldStateAsync(StarWorldStateUpdateRequest request, CancellationToken cancellationToken)
        {
            string sessionId = string.IsNullOrWhiteSpace(request.SessionId) ? "local-dev" : request.SessionId;
            StarState state = await _stateStore.GetAsync(sessionId, request.StarId, cancellationToken).ConfigureAwait(false);
            NormalizeWorldDefaults(state);

            if (request.IsMet.HasValue)
            {
                state.IsMet = request.IsMet.Value;
                state.RelationshipEvents.Insert(0, (state.IsMet ? "Met Star" : "Met flag removed") + " via host/gameplay state" + BuildReasonSuffix(request.Reason) + ".");
            }

            if (request.IsAlly.HasValue)
            {
                state.IsAlly = request.IsAlly.Value;
                if (state.IsAlly) state.IsMet = true;
                state.RelationshipEvents.Insert(0, (state.IsAlly ? "Became ally" : "Ally flag removed") + " via host/gameplay state" + BuildReasonSuffix(request.Reason) + ".");
            }

            AddUnique(state.RelationshipEvents, request.RelationshipEvents ?? new List<string>(), _options.MaxRelationshipEvents);
            Trim(state.RelationshipEvents, _options.MaxRelationshipEvents);
            await _stateStore.SaveAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            return state;
        }

        public async Task<StarState> SetStarDailyStateContextAsync(string sessionId, string starId, StarDailyStateContext context, CancellationToken cancellationToken)
        {
            StarState state = await _stateStore.GetAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            NormalizeWorldDefaults(state);
            state.TodayState = context ?? new StarDailyStateContext();
            await _stateStore.SaveAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            return state;
        }

        public async Task<StarState> ApplyStarEffectorAsync(string sessionId, string starId, StarRelationshipEffector effector, CancellationToken cancellationToken)
        {
            StarState state = await _stateStore.GetAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            NormalizeWorldDefaults(state);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            PruneExpiredEffectors(state, now);

            StarRelationshipEffector normalized = NormalizeEffector(effector, now);
            int index = state.ActiveEffectors.FindIndex(x => string.Equals(x.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) state.ActiveEffectors[index] = normalized;
            else state.ActiveEffectors.Insert(0, normalized);

            state.RelationshipEvents.Insert(0, "Applied deterministic effector '" + normalized.Id + "'" + BuildReasonSuffix(normalized.Reason) + ".");
            Trim(state.RelationshipEvents, _options.MaxRelationshipEvents);
            await _stateStore.SaveAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            return state;
        }

        public async Task<StarState> ClearStarEffectorAsync(string sessionId, string starId, string effectorId, CancellationToken cancellationToken)
        {
            StarState state = await _stateStore.GetAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            NormalizeWorldDefaults(state);
            if (string.IsNullOrWhiteSpace(effectorId) || string.Equals(effectorId, "*", StringComparison.OrdinalIgnoreCase) || string.Equals(effectorId, "all", StringComparison.OrdinalIgnoreCase))
            {
                state.ActiveEffectors.Clear();
                state.RelationshipEvents.Insert(0, "Cleared all deterministic effectors via host/gameplay state.");
            }
            else
            {
                string id = Slugify(effectorId);
                state.ActiveEffectors.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                state.RelationshipEvents.Insert(0, "Cleared deterministic effector '" + id + "' via host/gameplay state.");
            }
            Trim(state.RelationshipEvents, _options.MaxRelationshipEvents);
            await _stateStore.SaveAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            return state;
        }

        public async Task<StarState> AddStarDialogueEntryAsync(StarDialogueMemoryUpdateRequest request, CancellationToken cancellationToken)
        {
            string sessionId = string.IsNullOrWhiteSpace(request.SessionId) ? "local-dev" : request.SessionId;
            StarState state = await _stateStore.GetAsync(sessionId, request.StarId, cancellationToken).ConfigureAwait(false);
            NormalizeWorldDefaults(state);

            StarDialogueMemoryEntry entry = NormalizeDialogueEntry(request.Entry, request.StarId, DateTimeOffset.UtcNow);
            int existingIndex = state.GameDialogueHistory.FindIndex(x => string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0) state.GameDialogueHistory.RemoveAt(existingIndex);
            state.GameDialogueHistory.Insert(0, entry);
            Trim(state.GameDialogueHistory, _options.MaxGameDialogueHistory);

            if (!string.IsNullOrWhiteSpace(entry.DialogId))
            {
                state.CurrentDialogueState.DialogId = entry.DialogId;
                state.CurrentDialogueState.Title = string.IsNullOrWhiteSpace(entry.Title) ? state.CurrentDialogueState.Title : entry.Title;
                state.CurrentDialogueState.CurrentNodeId = entry.NodeId;
                state.CurrentDialogueState.CurrentLineIndex = entry.SequenceIndex;
                state.CurrentDialogueState.Area = string.IsNullOrWhiteSpace(entry.Area) ? state.CurrentDialogueState.Area : entry.Area;
                state.CurrentDialogueState.QuestId = string.IsNullOrWhiteSpace(entry.QuestId) ? state.CurrentDialogueState.QuestId : entry.QuestId;
                state.CurrentDialogueState.SceneId = string.IsNullOrWhiteSpace(entry.SceneId) ? state.CurrentDialogueState.SceneId : entry.SceneId;
                state.CurrentDialogueState.LastAuthoredLine = string.IsNullOrWhiteSpace(entry.Text) ? state.CurrentDialogueState.LastAuthoredLine : entry.Text;
                state.CurrentDialogueState.SummarySoFar = string.IsNullOrWhiteSpace(entry.Summary) ? state.CurrentDialogueState.SummarySoFar : entry.Summary;
                state.CurrentDialogueState.IsActive = !entry.IsComplete;
                state.CurrentDialogueState.IsComplete = entry.IsComplete;
                state.CurrentDialogueState.UpdatedAt = DateTimeOffset.UtcNow;
            }

            state.RelationshipEvents.Insert(0, "Recorded game dialogue beat '" + entry.Id + "' for " + state.StarId + ".");
            Trim(state.RelationshipEvents, _options.MaxRelationshipEvents);
            await _stateStore.SaveAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            return state;
        }

        public async Task<StarState> SetStarDialogueStateAsync(StarDialogueProgressUpdateRequest request, CancellationToken cancellationToken)
        {
            string sessionId = string.IsNullOrWhiteSpace(request.SessionId) ? "local-dev" : request.SessionId;
            StarState state = await _stateStore.GetAsync(sessionId, request.StarId, cancellationToken).ConfigureAwait(false);
            NormalizeWorldDefaults(state);

            StarDialogueProgressState dialogueState = NormalizeDialogueProgressState(request.DialogueState, DateTimeOffset.UtcNow);
            state.CurrentDialogueState = dialogueState;
            state.RelationshipEvents.Insert(0, "Set full authored dialogue progress '" + (string.IsNullOrWhiteSpace(dialogueState.DialogId) ? "dialogue" : dialogueState.DialogId) + "' for " + state.StarId + ".");
            Trim(state.RelationshipEvents, _options.MaxRelationshipEvents);
            await _stateStore.SaveAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            return state;
        }

        public async Task<StarState> ClearStarDialogueStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            string safeSessionId = string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId;
            StarState state = await _stateStore.GetAsync(safeSessionId, starId, cancellationToken).ConfigureAwait(false);
            NormalizeWorldDefaults(state);
            state.CurrentDialogueState = new StarDialogueProgressState();
            state.RelationshipEvents.Insert(0, "Cleared current authored dialogue progress for " + state.StarId + ".");
            Trim(state.RelationshipEvents, _options.MaxRelationshipEvents);
            await _stateStore.SaveAsync(safeSessionId, state, cancellationToken).ConfigureAwait(false);
            return state;
        }





        private static bool NormalizeWorldDefaults(StarState state)
        {
            bool changed = false;
            if (state.ActiveEffectors == null)
            {
                state.ActiveEffectors = new List<StarRelationshipEffector>();
                changed = true;
            }
            if (state.GameDialogueHistory == null)
            {
                state.GameDialogueHistory = new List<StarDialogueMemoryEntry>();
                changed = true;
            }
            if (state.CurrentDialogueState == null)
            {
                state.CurrentDialogueState = new StarDialogueProgressState();
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
            if (state.IsAlly && !state.IsMet)
            {
                state.IsMet = true;
                changed = true;
            }
            if (string.Equals(state.StarId, "Elusiaa", StringComparison.OrdinalIgnoreCase))
            {
                bool explicitlyRemovedMet = state.RelationshipEvents.Any(x => x.IndexOf("Met flag removed", StringComparison.OrdinalIgnoreCase) >= 0);
                bool explicitlyRemovedAlly = state.RelationshipEvents.Any(x => x.IndexOf("Ally flag removed", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!state.IsMet && !explicitlyRemovedMet) { state.IsMet = true; changed = true; }
                if (!state.IsAlly && !explicitlyRemovedAlly) { state.IsAlly = true; changed = true; }
            }
            return changed;
        }

        private static bool PruneExpiredEffectors(StarState state, DateTimeOffset now)
        {
            if (state.ActiveEffectors == null)
            {
                state.ActiveEffectors = new List<StarRelationshipEffector>();
                return true;
            }

            int before = state.ActiveEffectors.Count;
            state.ActiveEffectors.RemoveAll(x => x == null || x.IsExpired(now));
            return state.ActiveEffectors.Count != before;
        }

        private static StarRelationshipEffector NormalizeEffector(StarRelationshipEffector? source, DateTimeOffset now)
        {
            StarRelationshipEffector effector = source ?? new StarRelationshipEffector();
            if (string.IsNullOrWhiteSpace(effector.Id))
            {
                effector.Id = string.IsNullOrWhiteSpace(effector.Mode) ? Guid.NewGuid().ToString("N") : effector.Mode;
            }
            effector.Id = Slugify(effector.Id);
            effector.Mode = (effector.Mode ?? string.Empty).Trim();
            effector.DisplayName = string.IsNullOrWhiteSpace(effector.DisplayName) ? effector.Id : effector.DisplayName.Trim();
            effector.Source = (effector.Source ?? string.Empty).Trim();
            effector.Reason = (effector.Reason ?? string.Empty).Trim();
            if (effector.CreatedAt == default(DateTimeOffset)) effector.CreatedAt = now;
            if (effector.DurationSeconds > 0 && !effector.ExpiresAt.HasValue) effector.ExpiresAt = now.AddSeconds(effector.DurationSeconds);
            if (effector.ConnectionGainMultiplier <= 0) effector.ConnectionGainMultiplier = 1.0;
            if (effector.ConnectionLossMultiplier <= 0) effector.ConnectionLossMultiplier = 1.0;
            effector.Tags ??= new List<string>();
            effector.IsActive = true;
            return effector;
        }

        private static string BuildReasonSuffix(string reason)
        {
            return string.IsNullOrWhiteSpace(reason) ? string.Empty : ": " + reason.Trim();
        }

        private static StarDialogueMemoryEntry NormalizeDialogueEntry(StarDialogueMemoryEntry? source, string starId, DateTimeOffset now)
        {
            StarDialogueMemoryEntry entry = source ?? new StarDialogueMemoryEntry();
            entry.DialogId = CleanMarkerText(entry.DialogId, 120);
            entry.NodeId = CleanMarkerText(entry.NodeId, 120);
            entry.Title = CleanMarkerText(entry.Title, 180);
            entry.Speaker = string.IsNullOrWhiteSpace(entry.Speaker) ? starId : CleanMarkerText(entry.Speaker, 120);
            entry.Text = CleanMarkerText(entry.Text, 4000);
            entry.Summary = CleanMarkerText(entry.Summary, 1000);
            entry.SceneId = CleanMarkerText(entry.SceneId, 120);
            entry.Area = CleanMarkerText(entry.Area, 160);
            entry.QuestId = CleanMarkerText(entry.QuestId, 120);
            entry.Source = string.IsNullOrWhiteSpace(entry.Source) ? "game" : CleanMarkerText(entry.Source, 80);
            if (entry.CreatedAt == default(DateTimeOffset)) entry.CreatedAt = now;
            if (entry.IsComplete && !entry.CompletedAt.HasValue) entry.CompletedAt = now;
            entry.Tags ??= new List<string>();
            entry.Metadata ??= new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                string raw = string.Join("_", new[] { entry.DialogId, entry.NodeId, entry.SequenceIndex.ToString(), entry.Title }.Where(x => !string.IsNullOrWhiteSpace(x)));
                entry.Id = Slugify(string.IsNullOrWhiteSpace(raw) ? Guid.NewGuid().ToString("N") : raw);
            }
            else
            {
                entry.Id = Slugify(entry.Id);
            }
            return entry;
        }

        private static StarDialogueProgressState NormalizeDialogueProgressState(StarDialogueProgressState? source, DateTimeOffset now)
        {
            StarDialogueProgressState state = source ?? new StarDialogueProgressState();
            state.DialogId = CleanMarkerText(state.DialogId, 120);
            state.Title = CleanMarkerText(state.Title, 180);
            state.SceneId = CleanMarkerText(state.SceneId, 120);
            state.Area = CleanMarkerText(state.Area, 160);
            state.QuestId = CleanMarkerText(state.QuestId, 120);
            state.CurrentNodeId = CleanMarkerText(state.CurrentNodeId, 120);
            state.CurrentStage = CleanMarkerText(state.CurrentStage, 160);
            state.SummarySoFar = CleanMarkerText(state.SummarySoFar, 3000);
            state.LastAuthoredLine = CleanMarkerText(state.LastAuthoredLine, 2000);
            state.NextExpectedBeat = CleanMarkerText(state.NextExpectedBeat, 1200);
            state.FullScriptText = CleanMarkerText(state.FullScriptText, 20000);
            state.FullScriptJson = CleanMarkerText(state.FullScriptJson, 20000);
            state.UpdatedAt = now;
            state.Tags ??= new List<string>();
            state.Metadata ??= new Dictionary<string, string>();
            if (state.IsComplete) state.IsActive = false;
            return state;
        }

        private List<StarRememberedMarker> PrepareRememberedMarkers(StarStateUpdateRequest request, DateTimeOffset now)
        {
            List<StarRememberedMarker> markers = (request.RememberedMarkers ?? new List<StarRememberedMarker>())
                .Where(x => x != null)
                .ToList();

            if (markers.Count == 0)
            {
                StarRememberedMarker? heuristic = TryCreateHeuristicRememberedMarker(request, now);
                if (heuristic != null)
                {
                    markers.Add(heuristic);
                }
            }

            foreach (StarRememberedMarker marker in markers)
            {
                marker.StarId = string.IsNullOrWhiteSpace(marker.StarId) ? request.StarId : marker.StarId.Trim();
                marker.SourcePlayerQuote = string.IsNullOrWhiteSpace(marker.SourcePlayerQuote) ? (request.PlayerText ?? string.Empty).Trim() : marker.SourcePlayerQuote.Trim();
                marker.MarkerName = CleanMarkerText(marker.MarkerName, 160);
                marker.Description = CleanMarkerText(marker.Description, 500);
                marker.LocationText = CleanMarkerText(marker.LocationText, 300);
                marker.Coordinates = CleanMarkerText(marker.Coordinates, 120);
                marker.Area = CleanMarkerText(marker.Area, 120);
                marker.ReminderText = CleanMarkerText(marker.ReminderText, 600);
                if (marker.Confidence <= 0) marker.Confidence = 0.75;
                if (marker.Confidence > 1) marker.Confidence = 1;
                if (marker.CreatedAt == default(DateTimeOffset)) marker.CreatedAt = now;
                marker.UpdatedAt = now;
                marker.IsActive = true;
                marker.Tags ??= new List<string>();
                if (string.IsNullOrWhiteSpace(marker.Id))
                {
                    marker.Id = BuildMarkerId(marker, request);
                }
                else
                {
                    marker.Id = Slugify(marker.Id);
                }
            }

            return markers.Where(x => !string.IsNullOrWhiteSpace(x.Description)
                    || !string.IsNullOrWhiteSpace(x.LocationText)
                    || !string.IsNullOrWhiteSpace(x.Coordinates)
                    || !string.IsNullOrWhiteSpace(x.MarkerName))
                .ToList();
        }

        private static StarRememberedMarker? TryCreateHeuristicRememberedMarker(StarStateUpdateRequest request, DateTimeOffset now)
        {
            string text = request.PlayerText ?? string.Empty;
            if (!LooksLikeRememberRequest(text)) return null;

            string coordinates = ExtractCoordinates(text);
            string markerName = ExtractMarkerName(text);
            string description = ExtractRememberDescription(text);
            if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(coordinates)) return null;

            return new StarRememberedMarker
            {
                StarId = request.StarId,
                MarkerName = string.IsNullOrWhiteSpace(markerName) ? "remembered marker" : markerName,
                Description = string.IsNullOrWhiteSpace(description) ? text.Trim() : description,
                LocationText = ExtractLocationText(text, coordinates),
                Coordinates = coordinates,
                Area = string.Empty,
                SourcePlayerQuote = text.Trim(),
                ReminderText = text.Trim(),
                Confidence = string.IsNullOrWhiteSpace(coordinates) ? 0.62 : 0.78,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
                Tags = new List<string> { "remember-request", "heuristic" }
            };
        }

        private void UpsertRememberedMarkers(StarState state, List<StarRememberedMarker> markers, DateTimeOffset now)
        {
            if (markers.Count == 0) return;
            state.RememberedMarkers ??= new List<StarRememberedMarker>();
            foreach (StarRememberedMarker marker in markers)
            {
                StarRememberedMarker? existing = state.RememberedMarkers.FirstOrDefault(x =>
                    (!string.IsNullOrWhiteSpace(marker.Id) && string.Equals(x.Id, marker.Id, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(marker.MarkerName)
                        && string.Equals(x.MarkerName, marker.MarkerName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.Coordinates, marker.Coordinates, StringComparison.OrdinalIgnoreCase)));

                if (existing == null)
                {
                    state.RememberedMarkers.Insert(0, marker);
                }
                else
                {
                    existing.MarkerName = string.IsNullOrWhiteSpace(marker.MarkerName) ? existing.MarkerName : marker.MarkerName;
                    existing.Description = string.IsNullOrWhiteSpace(marker.Description) ? existing.Description : marker.Description;
                    existing.LocationText = string.IsNullOrWhiteSpace(marker.LocationText) ? existing.LocationText : marker.LocationText;
                    existing.Coordinates = string.IsNullOrWhiteSpace(marker.Coordinates) ? existing.Coordinates : marker.Coordinates;
                    existing.Area = string.IsNullOrWhiteSpace(marker.Area) ? existing.Area : marker.Area;
                    existing.SourcePlayerQuote = string.IsNullOrWhiteSpace(marker.SourcePlayerQuote) ? existing.SourcePlayerQuote : marker.SourcePlayerQuote;
                    existing.ReminderText = string.IsNullOrWhiteSpace(marker.ReminderText) ? existing.ReminderText : marker.ReminderText;
                    existing.Confidence = Math.Max(existing.Confidence, marker.Confidence);
                    existing.IsActive = marker.IsActive;
                    existing.UpdatedAt = now;
                    AddUnique(existing.Tags, marker.Tags, 20);
                }
            }
            Trim(state.RememberedMarkers, _options.MaxRememberedMarkers);
        }

        private static void MarkReferencedMemoryMarkers(StarState state, IEnumerable<string>? markerIds, DateTimeOffset now)
        {
            if (markerIds == null || state.RememberedMarkers == null || state.RememberedMarkers.Count == 0) return;
            foreach (string raw in markerIds)
            {
                string id = (raw ?? string.Empty).Trim();
                if (id.Length == 0) continue;
                StarRememberedMarker? marker = state.RememberedMarkers.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (marker != null)
                {
                    marker.LastReferencedAt = now;
                    marker.UpdatedAt = now;
                }
            }
        }

        private static bool LooksLikeRememberRequest(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return Regex.IsMatch(text, @"\b(remember|mark|save|pin|note|remind\s+me\s+later|keep\s+track)\b", RegexOptions.IgnoreCase);
        }

        private static string ExtractCoordinates(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            Match match = Regex.Match(text, @"(?:(?:coords?|coordinates?|position|pos|x\s*[,=:]\s*y|at)\s*(?:are|is|=|:)?\s*)?(?<coords>[-+]?\d{1,6}(?:\.\d+)?\s*[,/]\s*[-+]?\d{1,6}(?:\.\d+)?(?:\s*[,/]\s*[-+]?\d{1,6}(?:\.\d+)?)?)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["coords"].Value.Trim() : string.Empty;
        }

        private static string ExtractMarkerName(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            Match quoted = Regex.Match(text, "[\\\"'“](?<name>[^\\\"'”]{2,80})[\\\"'”]");
            if (quoted.Success) return quoted.Groups["name"].Value.Trim();
            Match named = Regex.Match(text, @"(?:called|named|marker(?:\s+name)?\s*(?:is|=|:)|label(?:ed)?\s*(?:as)?)[\s:]+(?<name>[A-Za-z0-9 _\-]{2,80})", RegexOptions.IgnoreCase);
            if (named.Success) return named.Groups["name"].Value.Trim().Trim('.', ',');
            return string.Empty;
        }

        private static string ExtractRememberDescription(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string cleaned = Regex.Replace(text.Trim(), @"^.*?\b(?:remember|mark|save|pin|note)\b\s*(?:that|this|down|for me|the)?\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
            return CleanMarkerText(string.IsNullOrWhiteSpace(cleaned) ? text : cleaned, 500);
        }

        private static string ExtractLocationText(string text, string coordinates)
        {
            if (string.IsNullOrWhiteSpace(text)) return coordinates ?? string.Empty;
            Match loc = Regex.Match(text, @"\b(?:at|in|near|by|inside|under|above|beside)\s+(?<loc>[A-Za-z0-9 _'\-]+?)(?:\s+(?:at|coords?|coordinates?|x|y)\b|[\.,;]|$)", RegexOptions.IgnoreCase);
            if (loc.Success) return loc.Groups["loc"].Value.Trim();
            return coordinates ?? string.Empty;
        }

        private static string BuildMarkerId(StarRememberedMarker marker, StarStateUpdateRequest request)
        {
            string basis = !string.IsNullOrWhiteSpace(marker.MarkerName) ? marker.MarkerName : marker.Description;
            if (!string.IsNullOrWhiteSpace(marker.Coordinates)) basis += " " + marker.Coordinates;
            if (string.IsNullOrWhiteSpace(basis)) basis = request.StarId + " marker " + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return Slugify(basis);
        }

        private static string CleanMarkerText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string cleaned = value.Replace("\r", " ").Replace("\n", " ").Trim();
            while (cleaned.Contains("  ", StringComparison.Ordinal)) cleaned = cleaned.Replace("  ", " ");
            if (cleaned.Length <= maxLength) return cleaned;
            return cleaned.Substring(0, maxLength).Trim();
        }

        private static void UpdateEmotionStateHistory(StarState state, StarStateUpdateRequest request, RelationshipDelta applied, DateTimeOffset now, double emotionalConnectionBefore, double emotionalConnectionAfter)
        {
            state.LastEmotionalEffectVector = request.EmotionalEffectVector ?? string.Empty;
            state.LastNegativeEmotionVector = request.NegativeEmotionVector ?? string.Empty;
            state.LastDeterministicEmotionSummary = request.DeterministicEmotionSummary ?? string.Empty;
            state.LastTopAffectedEmotions = RelationshipDeltaCalculator.BuildTopAffectedEmotions(request.EmotionalEffectVector, request.NegativeEmotionVector);
            if (string.IsNullOrWhiteSpace(state.EmotionalConnectionSkillLevel))
            {
                state.EmotionalConnectionSkillLevel = "interested_private_potential_partner";
            }

            AccumulateDeterministicEmotionTotals(state, request, now);

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
                TopAffectedEmotions = state.LastTopAffectedEmotions.ToList(),
                SuggestedDelta = request.SuggestedDelta,
                ActualDelta = applied,
                EmotionalConnectionPercentBefore = emotionalConnectionBefore,
                EmotionalConnectionPercentAfter = emotionalConnectionAfter,
                IsGirlfriendReadyAfter = state.IsGirlfriendReady,
                Summary = request.DeterministicEmotionSummary ?? string.Empty
            });
            Trim(state.EmotionUpdateHistory, 80);
        }

        private static void AccumulateDeterministicEmotionTotals(StarState state, StarStateUpdateRequest request, DateTimeOffset now)
        {
            if (state == null || request == null) return;
            List<EmotionVectorScore> positive = RelationshipDeltaCalculator.ParseVector(request.EmotionalEffectVector, Wds.SixStars.Api.Stars.StarEmotionMatrix.StandardEmotionIds)
                .Where(x => ShouldAccumulateEmotion(x))
                .ToList();
            List<EmotionVectorScore> negative = RelationshipDeltaCalculator.ParseVector(request.NegativeEmotionVector, RelationshipDeltaCalculator.NegativeEmotionIds)
                .Where(x => ShouldAccumulateEmotion(x))
                .ToList();

            foreach (EmotionVectorScore signal in positive)
            {
                AccumulateOneEmotion(state.PositiveEmotionTotals, signal, "positive", now);
            }

            foreach (EmotionVectorScore signal in negative)
            {
                AccumulateOneEmotion(state.NegativeEmotionTotals, signal, "negative", now);
            }

            TrimAccumulatedEmotionTotals(state.PositiveEmotionTotals, 64);
            TrimAccumulatedEmotionTotals(state.NegativeEmotionTotals, 64);
        }

        private static bool ShouldAccumulateEmotion(EmotionVectorScore signal)
        {
            if (signal == null || string.IsNullOrWhiteSpace(signal.Id)) return false;
            return signal.Confidence >= 0.70 && signal.Score >= 80;
        }

        private static void AccumulateOneEmotion(List<StarEmotionAccumulatedScore> totals, EmotionVectorScore signal, string valence, DateTimeOffset now)
        {
            if (totals == null || signal == null || string.IsNullOrWhiteSpace(signal.Id)) return;
            StarEmotionAccumulatedScore? existing = totals.FirstOrDefault(x => string.Equals(x.EmotionId, signal.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new StarEmotionAccumulatedScore
                {
                    EmotionId = signal.Id,
                    Valence = valence,
                    FirstSeenAt = now
                };
                totals.Add(existing);
            }

            existing.Valence = valence;
            existing.Count += 1;
            existing.TotalScore += signal.Score;
            existing.TotalWeightedScore += signal.WeightedScore;
            existing.LastConfidence = signal.Confidence;
            existing.LastScore = signal.Score;
            existing.LastReason = signal.ToString();
            existing.LastSeenAt = now;
        }

        private static void TrimAccumulatedEmotionTotals(List<StarEmotionAccumulatedScore> totals, int maxCount)
        {
            if (totals == null || totals.Count <= maxCount) return;
            List<StarEmotionAccumulatedScore> keep = totals
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.EmotionId))
                .GroupBy(x => x.EmotionId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.TotalWeightedScore).First())
                .OrderByDescending(x => x.TotalWeightedScore)
                .Take(Math.Max(1, maxCount))
                .ToList();
            totals.Clear();
            totals.AddRange(keep);
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


        private void AddImportantQuoteSourceRecords(StarState state, StarStateUpdateRequest request, IEnumerable<string> quotes, IEnumerable<StarRememberedMarker> markers, RelationshipDelta applied, DateTimeOffset now, string reason)
        {
            state.AllImportantPlayerQuotes ??= new List<StarImportantQuoteRecord>();
            foreach (string raw in quotes ?? Array.Empty<string>())
            {
                string quote = CleanMarkerText(raw, 700);
                if (string.IsNullOrWhiteSpace(quote)) continue;
                if (state.AllImportantPlayerQuotes.Any(x => string.Equals(x.Quote, quote, StringComparison.OrdinalIgnoreCase))) continue;
                StarImportantQuoteRecord record = new StarImportantQuoteRecord
                {
                    Id = Slugify(quote + " " + now.ToUnixTimeMilliseconds()),
                    CreatedAt = now,
                    StarId = request.StarId,
                    Quote = quote,
                    Reason = reason,
                    RequestType = request.RequestType ?? string.Empty,
                    EmotionalEffectVector = request.EmotionalEffectVector ?? string.Empty,
                    NegativeEmotionVector = request.NegativeEmotionVector ?? string.Empty,
                    WisdomScore = request.WisdomScore,
                    ActualDelta = applied
                };
                record.Tags.AddRange(request.DetectedCombos.Where(x => !string.IsNullOrWhiteSpace(x)).Take(10));
                record.MarkerIds.AddRange((markers ?? Array.Empty<StarRememberedMarker>()).Select(x => x.Id).Where(x => !string.IsNullOrWhiteSpace(x)).Take(10));
                state.AllImportantPlayerQuotes.Insert(0, record);
            }
            Trim(state.AllImportantPlayerQuotes, _options.MaxAllImportantQuoteRecords);
        }

        private void ArchiveOldConversationTurns(StarState state, DateTimeOffset now)
        {
            if (state.MemoryLog == null || state.MemoryLog.Count == 0) return;
            state.OldConversationArchives ??= new List<StarConversationArchiveEntry>();
            int minutes = Math.Max(5, _options.ArchiveConversationAfterMinutes);
            DateTimeOffset cutoff = now.AddMinutes(-minutes);
            List<ConversationLogEntry> oldEntries = state.MemoryLog
                .Where(x => x != null && x.CreatedAt < cutoff)
                .OrderBy(x => x.CreatedAt)
                .Take(Math.Max(1, _options.MaxArchivedTurnsPerArchive))
                .ToList();
            if (oldEntries.Count == 0) return;

            HashSet<ConversationLogEntry> oldSet = new HashSet<ConversationLogEntry>(oldEntries);
            state.MemoryLog = state.MemoryLog.Where(x => !oldSet.Contains(x)).ToList();

            DateTimeOffset start = oldEntries.First().CreatedAt;
            DateTimeOffset end = oldEntries.Last().CreatedAt;
            StarConversationArchiveEntry archive = new StarConversationArchiveEntry
            {
                Id = Slugify(state.StarId + " conversation " + start.ToUnixTimeSeconds() + " " + end.ToUnixTimeSeconds()),
                StarId = state.StarId,
                StartedAt = start,
                EndedAt = end,
                ArchivedAt = now,
                EntryCount = oldEntries.Count,
                Summary = BuildArchiveSummary(oldEntries),
                Entries = oldEntries
            };
            foreach (string tag in oldEntries.SelectMany(x => x.Tags ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
            {
                archive.TopicTags.Add(tag);
            }
            state.OldConversationArchives.Insert(0, archive);
            Trim(state.OldConversationArchives, _options.MaxOldConversationArchives);
        }

        private static string BuildArchiveSummary(List<ConversationLogEntry> entries)
        {
            if (entries == null || entries.Count == 0) return string.Empty;
            List<string> summaries = entries
                .Select(x => string.IsNullOrWhiteSpace(x.Summary) ? ("Player: " + TruncateLocal(x.PlayerText, 120) + " / Star: " + TruncateLocal(x.StarResponse, 120)) : x.Summary)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(8)
                .ToList();
            string start = entries.First().CreatedAt.ToString("u");
            string end = entries.Last().CreatedAt.ToString("u");
            return "Archived conversation from " + start + " to " + end + " with " + entries.Count + " turn(s). " + string.Join(" ", summaries.Select(x => TruncateLocal(x, 220)));
        }

        private static string TruncateLocal(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string cleaned = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (cleaned.Length <= maxLength) return cleaned;
            return cleaned.Substring(0, maxLength).Trim() + "...";
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
                    { "isMet", state.IsMet ? "true" : "false" },
                    { "isAlly", state.IsAlly ? "true" : "false" },
                    { "activeEffectorCount", (state.ActiveEffectors == null ? 0 : state.ActiveEffectors.Count).ToString() },
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
