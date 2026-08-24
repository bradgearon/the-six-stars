using System;
using System.Collections.Generic;
using System.Globalization;
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
        private const string CanonicalSessionId = "local-dev";
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
            if (NormalizeWorldDefaults(state) || PruneExpiredEffectors(state, DateTimeOffset.UtcNow) || PruneExpiredShortTermMemory(state, DateTimeOffset.UtcNow))
            {
                await _stateStore.SaveAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            }
            return state;
        }

        public async Task<StarMemorySnapshot> GetStarMemorySnapshotAsync(string sessionId, string starId, int recentHistoryCount, CancellationToken cancellationToken)
        {
            StarState state = await _stateStore.GetAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            if (NormalizeWorldDefaults(state) || PruneExpiredEffectors(state, DateTimeOffset.UtcNow) || PruneExpiredShortTermMemory(state, DateTimeOffset.UtcNow))
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

        public async Task<StarState> ApplyUnresolvedThreadUpdatesAsync(string sessionId, string starId, string incomingVector, string outgoingVector, string resolutionVector, CancellationToken cancellationToken)
        {
            StarState state = await _stateStore.GetAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            NormalizeWorldDefaults(state);
            UpdateUnresolvedThreadState(state, new StarStateUpdateRequest
            {
                SessionId = sessionId ?? string.Empty,
                StarId = starId ?? string.Empty,
                IncomingUnresolvedVector = incomingVector ?? string.Empty,
                OutgoingUnresolvedVector = outgoingVector ?? string.Empty,
                UnresolvedResolutionVector = resolutionVector ?? string.Empty,
                IsInternalBackgroundUpdate = true,
                SuppressConversationLog = true,
                SuppressConversationSummaryUpdate = true
            });
            await _stateStore.SaveAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            return state;
        }

        public async Task<StarStateUpdateResult> UpdateStarWithInteractionAsync(StarStateUpdateRequest request, CancellationToken cancellationToken)
        {
            StarState state = await _stateStore.GetAsync(request.SessionId, request.StarId, cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            NormalizeWorldDefaults(state);
            PruneExpiredEffectors(state, now);
            PruneExpiredShortTermMemory(state, now);

            bool isInternalBackgroundUpdate = request.IsInternalBackgroundUpdate;
            bool suppressConversationSummaryUpdate = request.SuppressConversationSummaryUpdate || isInternalBackgroundUpdate;
            bool suppressConversationLog = request.SuppressConversationLog || isInternalBackgroundUpdate;

            state.StarId = string.IsNullOrWhiteSpace(state.StarId) ? request.StarId : state.StarId;
            if (!isInternalBackgroundUpdate)
            {
                state.InteractionCount += 1;
                state.LastInteractionAt = now;
            }
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
            AutoPromoteAllyIfReady(state, now);
            UpdateGirlfriendReadiness(state, now);

            if (!suppressConversationSummaryUpdate && !string.IsNullOrWhiteSpace(request.ConversationSummary))
            {
                state.ConversationSummary = request.ConversationSummary.Trim();
            }

            // Important player quotes are no longer chosen by the LLM. The deterministic layer
            // captures the exact latest player message only when the accepted compact emotion
            // vectors show a sufficiently strong reaction. Model-provided importantPlayerQuotes
            // are ignored for durable storage so ordinary turns do not bloat the quote list.
            AddUnique(state.MemoryNotes, request.MemoryNotes, _options.MaxMemoryNotes);
            AddShortTermMemoryEntries(state, request.ShortTermMemoryItems, now, request.RequestType);
            List<string> newlyLearnedOrSuggested = AddUniqueWithResult(state.ThingsLearnedOrSuggestedByPlayer, request.ThingsLearnedOrSuggestedByPlayer, _options.MaxThingsLearnedOrSuggestedByPlayer);
            UpdateUnresolvedThreadState(state, request);
            List<StarRememberedMarker> markersToStore = PrepareRememberedMarkers(request, now);
            UpsertRememberedMarkers(state, markersToStore, now);
            MarkReferencedMemoryMarkers(state, request.ReferencedMemoryMarkerIds, now);
            AddUnique(state.SecretsUnlocked, request.SecretsUnlocked, _options.MaxSecrets);
            AddUnique(state.RelationshipEvents, request.RelationshipEvents, _options.MaxRelationshipEvents);
            if (state.IsGirlfriend)
            {
                AddUnique(state.GirlfriendHistory, request.GirlfriendHistoryUpdates, _options.MaxGirlfriendHistoryEntries);
            }
            AddUnique(state.PetNamesPlayerUsesForStar, request.PetNamesPlayerUsesForStar, 20);
            AddUnique(state.PetNamesStarUsesForPlayer, request.PetNamesStarUsesForPlayer, 20);
            UpdateGirlfriendNeedsMetHistory(state, request, now);
            UpdateGirlfriendCoupleActivityHistory(state, request, now);
            UpsertLearnedFunctions(state, request.LearnedFunctions);
            UpsertSecrets(state, request.SecretsUnlocked, now);
            UpdateCombos(state, request.DetectedCombos, now);
            if (!isInternalBackgroundUpdate)
            {
                UpdateSuccessionCounters(state, applied, request, now);
                UpdateEmotionStateHistory(state, request, applied, now, emotionalConnectionBefore, state.EmotionalConnectionPercent);
            }
            else
            {
                AddUnique(state.MemoryNotes, BuildInternalUpdateMemoryNotes(request, applied), _options.MaxMemoryNotes);
            }
            UpdateStoryHistory(state, request, now);
            PruneExpiredEffectors(state, now);

            string autoQuoteReason = string.Empty;
            string autoQuote = isInternalBackgroundUpdate ? string.Empty : TryCreateAutoQuote(request, applied, out autoQuoteReason);
            if (!string.IsNullOrWhiteSpace(autoQuote))
            {
                AddUnique(state.ImportantPlayerQuotes, new[] { autoQuote }, _options.MaxImportantQuotes);
                AddImportantQuoteSourceRecords(state, request, new[] { autoQuote }, markersToStore, applied, now, string.IsNullOrWhiteSpace(autoQuoteReason) ? "auto-high-emotion-quote" : autoQuoteReason);
            }

            ConversationLogEntry entry = new ConversationLogEntry();
            if (!suppressConversationLog)
            {
            entry = new ConversationLogEntry
            {
                CreatedAt = now,
                RequestType = request.RequestType,
                PlayerText = CompactDurableText(request.PlayerText, 8000),
                StarResponse = CompactDurableText(request.StarResponse, 24000),
                Summary = CompactDurableText(string.IsNullOrWhiteSpace(request.ChangesSummary) ? request.ConversationSummary : request.ChangesSummary, 4000),
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
            if (!string.IsNullOrWhiteSpace(autoQuote)) entry.ImportantQuotes.Add(autoQuote);
            entry.ThingsLearnedOrSuggestedByPlayer.AddRange(newlyLearnedOrSuggested);
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
            StoreConversationRollups(state, request, now);
            ArchiveOldConversationTurns(state, now);
            Trim(state.MemoryLog, _options.MaxMemoryLogEntries);
            }

            await _stateStore.SaveAsync(request.SessionId, state, cancellationToken).ConfigureAwait(false);

            StarStateUpdateResult result = new StarStateUpdateResult
            {
                State = state,
                LogEntry = entry
            };
            result.UnityEvents.Add(CreateMemoryUpdatedEvent(request.StarId, state, entry, applied, _options.AcceptModelSuggestedDeltas));
            foreach (string learnedItem in newlyLearnedOrSuggested)
            {
                result.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "StarLearnedAboutPlayer",
                    Target = request.StarId,
                    Data =
                    {
                        { "item", learnedItem }
                    }
                });
            }
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
                Version = 2,
                SessionId = CanonicalSessionId,
                SavedAt = DateTimeOffset.UtcNow,
                Stars = states.ToList()
            };
        }

        public Task ImportSaveDataAsync(SixStarsSaveData saveData, CancellationToken cancellationToken)
        {
            return _stateStore.ReplaceSessionAsync(CanonicalSessionId, saveData.Stars, cancellationToken);
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

        public async Task<StarState> SetStarGirlfriendHourlyStateContextAsync(string sessionId, string starId, StarGirlfriendHourlyStateContext context, CancellationToken cancellationToken)
        {
            StarState state = await _stateStore.GetAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            NormalizeWorldDefaults(state);
            StarGirlfriendHourlyStateContext normalized = context ?? new StarGirlfriendHourlyStateContext();
            NormalizeGirlfriendHourlyContextJson(normalized);
            ArchiveGirlfriendDesiredState(state, sessionId, starId, normalized);
            state.GirlfriendHourlyState = normalized;
            await _stateStore.SaveAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            return state;
        }

        private static void ArchiveGirlfriendDesiredState(StarState state, string sessionId, string starId, StarGirlfriendHourlyStateContext context)
        {
            if (state == null || context == null || !context.IsAvailable) return;
            List<GirlfriendVectorItem> needs = CleanGirlfriendVector(context.NeedVector, 30);
            List<GirlfriendVectorItem> wants = CleanGirlfriendVector(context.WantVector, 30);
            List<string> things = CleanStringList(context.ThingsSheWantsToSay, 8);
            List<string> cute = CleanStringList(context.CuteThingsSheWantsToSay, 8);
            if (needs.Count == 0 && wants.Count == 0 && things.Count == 0 && cute.Count == 0) return;

            string id = "desired-" + (context.GeneratedAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyyMMddHHmmss") + "-" + (string.IsNullOrWhiteSpace(context.SlotId) ? "slot" : Slugify(context.SlotId));
            if (state.GirlfriendDesiredStateHistory.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) return;

            state.GirlfriendDesiredStateHistory.Insert(0, new GirlfriendVectorHistoryEntry
            {
                Id = id,
                SessionId = sessionId ?? string.Empty,
                StarId = string.IsNullOrWhiteSpace(context.StarId) ? starId : context.StarId,
                SlotId = context.SlotId ?? string.Empty,
                Source = "girlfriend-hourly-state",
                CreatedAt = context.GeneratedAt ?? DateTimeOffset.UtcNow,
                Summary = CleanMarkerText(context.Summary, 280),
                NeedSummary = CleanMarkerText(context.NeedSummary, 180),
                WantSummary = CleanMarkerText(context.WantSummary, 180),
                NeedVector = needs,
                WantVector = wants,
                ThingsSheWantsToSay = things,
                CuteThingsSheWantsToSay = cute,
                OverallConfidence = Math.Max(0, Math.Min(1, context.OverallConfidence)),
                OverallScore = Math.Max(0, Math.Min(100, context.OverallScore))
            });
            state.GirlfriendDesiredNeedVectorHistory.Insert(0, FormatHistoryCompactLine(context.GeneratedAt ?? DateTimeOffset.UtcNow, context.SlotId, needs, 12));
            state.GirlfriendDesiredWantVectorHistory.Insert(0, FormatHistoryCompactLine(context.GeneratedAt ?? DateTimeOffset.UtcNow, context.SlotId, wants, 12));
            Trim(state.GirlfriendDesiredNeedVectorHistory, 72);
            Trim(state.GirlfriendDesiredWantVectorHistory, 72);
            Trim(state.GirlfriendDesiredStateHistory, 72);
        }

        private static void UpdateGirlfriendNeedsMetHistory(StarState state, StarStateUpdateRequest request, DateTimeOffset now)
        {
            if (state == null || request == null || !state.IsGirlfriend) return;
            List<GirlfriendVectorItem> metNeeds = CleanGirlfriendVector(request.GirlfriendNeedsMetVector, 12);
            List<GirlfriendVectorItem> metWants = CleanGirlfriendVector(request.GirlfriendWantsMetVector, 12);
            if (metNeeds.Count == 0 && metWants.Count == 0 && string.IsNullOrWhiteSpace(request.GirlfriendNeedsMetSummary)) return;

            state.GirlfriendNeedsMetHistory.Insert(0, new GirlfriendNeedFulfillmentHistoryEntry
            {
                Id = "met-" + now.UtcDateTime.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                SessionId = request.SessionId ?? string.Empty,
                StarId = request.StarId ?? string.Empty,
                CreatedAt = now,
                PlayerText = CleanMarkerText(request.PlayerText, 600),
                StarResponse = CleanMarkerText(request.StarResponse, 600),
                Summary = CleanMarkerText(request.GirlfriendNeedsMetSummary, 240),
                MetNeedVector = metNeeds,
                MetWantVector = metWants
            });
            state.GirlfriendMetNeedVectorHistory.Insert(0, FormatHistoryCompactLine(now, string.Empty, metNeeds, 12));
            state.GirlfriendMetWantVectorHistory.Insert(0, FormatHistoryCompactLine(now, string.Empty, metWants, 12));
            Trim(state.GirlfriendMetNeedVectorHistory, 72);
            Trim(state.GirlfriendMetWantVectorHistory, 72);
            Trim(state.GirlfriendNeedsMetHistory, 72);
            AccumulateGirlfriendPerformanceVector(state.GirlfriendNeedsMetPerformanceVector, metNeeds, 30);
            AccumulateGirlfriendPerformanceVector(state.GirlfriendWantsMetPerformanceVector, metWants, 30);
            AddUnique(state.GirlfriendHistory, new[] { "Met girlfriend wants/needs: " + (string.IsNullOrWhiteSpace(request.GirlfriendNeedsMetSummary) ? FormatMetVectorSummary(metNeeds, metWants) : request.GirlfriendNeedsMetSummary) }, 120);
        }


        private static readonly Dictionary<string, string> GirlfriendCoupleActivityNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "holdingHands", "holding hands" },
            { "hugging", "hugging" },
            { "cuddling", "cuddling" },
            { "foreheadTouch", "forehead touch" },
            { "booping", "booping / playful touch" },
            { "petting", "petting" },
            { "stroking", "stroking / brushing" },
            { "purringClose", "purring close" },
            { "cheekKiss", "cheek kisses" },
            { "mouthKiss", "mouth kisses" },
            { "faceKissing", "face kisses" },
            { "lapHolding", "lap / being held" },
            { "verbalDevotion", "verbal devotion" },
            { "possessiveClaim", "possessive claim" },
            { "heatedRomance", "heated romance" },
            { "deepSexting", "deep sexting / raw textual intimacy" }
        };


        private static string NormalizeGirlfriendCoupleActivityId(string value)
        {
            string compact = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (compact.Length == 0) return string.Empty;
            foreach (string key in GirlfriendCoupleActivityNames.Keys)
            {
                string keyCompact = new string(key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                if (string.Equals(compact, keyCompact, StringComparison.OrdinalIgnoreCase)) return key;
            }
            return compact;
        }

        private static void UpdateUnresolvedThreadState(StarState state, StarStateUpdateRequest request)
        {
            if (state == null || request == null) return;
            state.IncomingUnresolvedVectors ??= new List<string>();
            state.OutgoingUnresolvedVectors ??= new List<string>();
            state.UnresolvedResolutionHistory ??= new List<string>();

            List<StarUnresolvedVectorCodec.IncomingThread> incoming = StarUnresolvedVectorCodec.ParseIncomingItems(state.IncomingUnresolvedVectors, 20);
            List<StarUnresolvedVectorCodec.OutgoingThread> outgoing = StarUnresolvedVectorCodec.ParseOutgoingItems(state.OutgoingUnresolvedVectors, 20);
            List<StarUnresolvedVectorCodec.IncomingThread> incomingUpdates = StarUnresolvedVectorCodec.ParseIncomingVector(request.IncomingUnresolvedVector, 10);
            List<StarUnresolvedVectorCodec.OutgoingThread> outgoingUpdates = StarUnresolvedVectorCodec.ParseOutgoingVector(request.OutgoingUnresolvedVector, 10);
            List<StarUnresolvedVectorCodec.Resolution> resolutions = StarUnresolvedVectorCodec.ParseResolutionVector(request.UnresolvedResolutionVector, 8);

            foreach (StarUnresolvedVectorCodec.IncomingThread update in incomingUpdates)
            {
                StarUnresolvedVectorCodec.IncomingThread? existing = incoming.FirstOrDefault(x =>
                    string.Equals(x.Id, update.Id, StringComparison.OrdinalIgnoreCase)
                    || SameUnresolvedText(x.OpenQuestion, update.OpenQuestion));
                if (existing == null) incoming.Add(update);
                else
                {
                    existing.Confidence = update.Confidence;
                    existing.Tags = update.Tags;
                    existing.OpenQuestion = update.OpenQuestion;
                    existing.ResolutionIntent = update.ResolutionIntent;
                }
            }

            foreach (StarUnresolvedVectorCodec.OutgoingThread update in outgoingUpdates)
            {
                StarUnresolvedVectorCodec.OutgoingThread? existing = outgoing.FirstOrDefault(x =>
                    string.Equals(x.Id, update.Id, StringComparison.OrdinalIgnoreCase)
                    || SameUnresolvedText(x.OpenThread, update.OpenThread));
                if (existing == null) outgoing.Add(update);
                else
                {
                    existing.Priority = update.Priority;
                    existing.Tags = update.Tags;
                    existing.OpenThread = update.OpenThread;
                    existing.ResolutionIntent = update.ResolutionIntent;
                    existing.IntendedEffects = update.IntendedEffects;
                }
            }

            List<StarUnresolvedVectorCodec.Resolution> priorResolutionHistory = StarUnresolvedVectorCodec.ParseResolutionItems(state.UnresolvedResolutionHistory, 24);
            foreach (StarUnresolvedVectorCodec.Resolution resolution in resolutions)
            {
                bool matchedActive = false;
                if (string.Equals(resolution.Direction, "in", StringComparison.OrdinalIgnoreCase))
                {
                    StarUnresolvedVectorCodec.IncomingThread? matched = incoming.FirstOrDefault(x => string.Equals(x.Id, resolution.Id, StringComparison.OrdinalIgnoreCase));
                    if (matched != null)
                    {
                        matchedActive = true;
                        if (string.IsNullOrWhiteSpace(resolution.ResolutionIntent)) resolution.ResolutionIntent = matched.ResolutionIntent;
                        incoming.RemoveAll(x => string.Equals(x.Id, resolution.Id, StringComparison.OrdinalIgnoreCase));
                    }
                }
                else
                {
                    StarUnresolvedVectorCodec.OutgoingThread? matched = outgoing.FirstOrDefault(x => string.Equals(x.Id, resolution.Id, StringComparison.OrdinalIgnoreCase));
                    if (matched != null)
                    {
                        matchedActive = true;
                        if (string.IsNullOrWhiteSpace(resolution.ResolutionIntent)) resolution.ResolutionIntent = matched.ResolutionIntent;
                        if (string.IsNullOrWhiteSpace(resolution.IntendedEffects)) resolution.IntendedEffects = matched.IntendedEffects;
                        outgoing.RemoveAll(x => string.Equals(x.Id, resolution.Id, StringComparison.OrdinalIgnoreCase));
                    }
                }

                // A recently resolved outgoing thread can receive a later "how it landed" update on
                // the next player turn. Merge that evidence into the existing resolution record rather
                // than reviving the active thread or losing the original plan/intended effect.
                StarUnresolvedVectorCodec.Resolution? prior = priorResolutionHistory.FirstOrDefault(x =>
                    string.Equals(x.Id, resolution.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Direction, resolution.Direction, StringComparison.OrdinalIgnoreCase));
                if (!matchedActive && prior == null) continue; // ignore hallucinated/unknown resolution ids
                if (prior != null)
                {
                    if (string.IsNullOrWhiteSpace(resolution.ResolutionIntent)) resolution.ResolutionIntent = prior.ResolutionIntent;
                    if (string.IsNullOrWhiteSpace(resolution.IntendedEffects)) resolution.IntendedEffects = prior.IntendedEffects;
                    if (string.IsNullOrWhiteSpace(resolution.LandedEffect)) resolution.LandedEffect = prior.LandedEffect;
                    if (string.IsNullOrWhiteSpace(resolution.ResolutionText)) resolution.ResolutionText = prior.ResolutionText;
                }

                string encoded = StarUnresolvedVectorCodec.EncodeResolutionHistory(resolution);
                if (!string.IsNullOrWhiteSpace(encoded))
                {
                    state.UnresolvedResolutionHistory.RemoveAll(x => ResolutionHistoryMatches(x, resolution.Id, resolution.Direction));
                    state.UnresolvedResolutionHistory.Insert(0, encoded);
                    priorResolutionHistory.RemoveAll(x => string.Equals(x.Id, resolution.Id, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.Direction, resolution.Direction, StringComparison.OrdinalIgnoreCase));
                    priorResolutionHistory.Insert(0, resolution);
                }
            }

            incoming = incoming
                .GroupBy(x => NormalizeUnresolvedText(x.OpenQuestion), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(y => y.Confidence).First())
                .OrderByDescending(x => x.Confidence)
                .Take(10)
                .ToList();
            outgoing = outgoing
                .GroupBy(x => NormalizeUnresolvedText(x.OpenThread), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(y => y.Priority).First())
                .OrderByDescending(x => x.Priority)
                .Take(10)
                .ToList();

            // Keep one tiny active working set across both directions. Incoming confidence is
            // compared on the same 0-100 scale as outgoing priority.
            while (incoming.Count + outgoing.Count > 10)
            {
                double weakestIncoming = incoming.Count == 0 ? double.MaxValue : incoming[incoming.Count - 1].Confidence * 100.0;
                double weakestOutgoing = outgoing.Count == 0 ? double.MaxValue : outgoing[outgoing.Count - 1].Priority;
                if (weakestIncoming <= weakestOutgoing && incoming.Count > 0) incoming.RemoveAt(incoming.Count - 1);
                else if (outgoing.Count > 0) outgoing.RemoveAt(outgoing.Count - 1);
                else break;
            }

            state.IncomingUnresolvedVectors = incoming
                .Select(StarUnresolvedVectorCodec.EncodeIncoming)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            state.OutgoingUnresolvedVectors = outgoing
                .Select(StarUnresolvedVectorCodec.EncodeOutgoing)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            Trim(state.UnresolvedResolutionHistory, 24);
        }

        private static bool ResolutionHistoryMatches(string raw, string id, string direction)
        {
            StarUnresolvedVectorCodec.Resolution? parsed = StarUnresolvedVectorCodec.ParseResolutionItems(new[] { raw }, 1).FirstOrDefault();
            return parsed != null
                && string.Equals(parsed.Id, id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parsed.Direction, direction, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameUnresolvedText(string? a, string? b)
        {
            string left = NormalizeUnresolvedText(a);
            string right = NormalizeUnresolvedText(b);
            return !string.IsNullOrWhiteSpace(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeUnresolvedText(string? value)
        {
            return new string((value ?? string.Empty).ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray()).Trim();
        }

        private static void UpdateGirlfriendCoupleActivityHistory(StarState state, StarStateUpdateRequest request, DateTimeOffset now)
        {
            if (state == null || request == null || !state.IsGirlfriend) return;
            List<GirlfriendVectorItem> activities = ParseGirlfriendCoupleActivityVector(request.GirlfriendCoupleActivityVector, 16);
            if (activities.Count == 0 && string.IsNullOrWhiteSpace(request.GirlfriendCoupleActivitySummary)) return;

            state.GirlfriendCoupleActivityHistory.Insert(0, new GirlfriendCoupleActivityHistoryEntry
            {
                Id = "couple-" + now.UtcDateTime.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                SessionId = request.SessionId ?? string.Empty,
                StarId = request.StarId ?? string.Empty,
                CreatedAt = now,
                PlayerText = CleanMarkerText(request.PlayerText, 1400),
                StarResponse = CleanMarkerText(request.StarResponse, 1400),
                Summary = CleanMarkerText(request.GirlfriendCoupleActivitySummary, 520),
                RawVector = CleanMarkerText(request.GirlfriendCoupleActivityVector, 1200),
                ActivityVector = activities
            });
            Trim(state.GirlfriendCoupleActivityHistory, 240);
            AccumulateGirlfriendCoupleActivityScores(state, activities, request, now);
            if (activities.Count > 0)
            {
                AddUnique(state.GirlfriendHistory, new[] { "Couple activity: " + FormatCoupleActivityVectorSummary(activities) }, 120);
            }
        }

        private static List<GirlfriendVectorItem> ParseGirlfriendCoupleActivityVector(string raw, int maxItems)
        {
            List<GirlfriendVectorItem> result = new List<GirlfriendVectorItem>();
            if (string.IsNullOrWhiteSpace(raw)) return result;
            string[] entries = raw.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string entry in entries)
            {
                string[] parts = entry.Split(':');
                if (parts.Length < 3) continue;
                string id = NormalizeGirlfriendCoupleActivityId(parts[0]);
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double score)) score = 0;
                if (!double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double confidence)) confidence = 0;
                if (confidence > 1) confidence /= 100.0;
                score = Math.Max(0, Math.Min(100, score));
                confidence = Math.Max(0, Math.Min(1, confidence));
                if (score <= 0 && confidence <= 0) continue;
                result.Add(new GirlfriendVectorItem
                {
                    ItemId = id,
                    DisplayName = GirlfriendCoupleActivityNames.TryGetValue(id, out string? name) ? name : id,
                    Score = score,
                    Confidence = confidence,
                    Reason = parts.Length > 3 ? CleanMarkerText(string.Join(":", parts.Skip(3)), 260) : string.Empty
                });
            }
            return result
                .OrderByDescending(x => x.Score * Math.Max(0.01, x.Confidence))
                .ThenByDescending(x => x.Score)
                .Take(Math.Max(1, maxItems))
                .ToList();
        }

        private static void AccumulateGirlfriendCoupleActivityScores(StarState state, IReadOnlyList<GirlfriendVectorItem> activities, StarStateUpdateRequest request, DateTimeOffset now)
        {
            if (state.GirlfriendCoupleActivityScores == null) state.GirlfriendCoupleActivityScores = new List<GirlfriendCoupleActivityScore>();
            if (state.GirlfriendCoupleActivityBestExamples == null) state.GirlfriendCoupleActivityBestExamples = new List<GirlfriendCoupleActivityBestExample>();
            foreach (GirlfriendVectorItem item in activities ?? Array.Empty<GirlfriendVectorItem>())
            {
                string id = NormalizeGirlfriendCoupleActivityId(FirstNonEmpty(item.ItemId, item.DisplayName));
                if (string.IsNullOrWhiteSpace(id)) continue;
                double score = Math.Max(0, Math.Min(100, item.Score));
                double confidence = Math.Max(0, Math.Min(1, item.Confidence));
                double contribution = score * confidence;
                if (contribution <= 0) continue;

                GirlfriendCoupleActivityScore? existing = state.GirlfriendCoupleActivityScores.FirstOrDefault(x => string.Equals(x.ActivityId, id, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new GirlfriendCoupleActivityScore { ActivityId = id, DisplayName = FirstNonEmpty(item.DisplayName, id) };
                    state.GirlfriendCoupleActivityScores.Add(existing);
                }
                existing.DisplayName = FirstNonEmpty(existing.DisplayName, item.DisplayName, id);
                existing.TotalScore = Math.Max(0, existing.TotalScore + contribution);
                existing.HighestSingleScore = Math.Max(existing.HighestSingleScore, score);
                existing.HighestConfidence = Math.Max(existing.HighestConfidence, confidence);
                existing.LastScoreAdded = contribution;
                existing.LastReason = CleanMarkerText(item.Reason, 360);
                existing.LastUpdatedAt = now;
                existing.Count++;

                GirlfriendCoupleActivityBestExample? best = state.GirlfriendCoupleActivityBestExamples.FirstOrDefault(x => string.Equals(x.ActivityId, id, StringComparison.OrdinalIgnoreCase));
                if (best == null || contribution >= best.WeightedScore)
                {
                    if (best == null)
                    {
                        best = new GirlfriendCoupleActivityBestExample { ActivityId = id };
                        state.GirlfriendCoupleActivityBestExamples.Add(best);
                    }
                    best.DisplayName = FirstNonEmpty(item.DisplayName, id);
                    best.WeightedScore = contribution;
                    best.Score = score;
                    best.Confidence = confidence;
                    best.CreatedAt = now;
                    best.PlayerText = CleanMarkerText(request.PlayerText, 900);
                    best.StarResponse = CleanMarkerText(request.StarResponse, 900);
                    best.Summary = CleanMarkerText(request.GirlfriendCoupleActivitySummary, 420);
                }
            }
            state.GirlfriendCoupleActivityScores.Sort((a, b) => b.TotalScore.CompareTo(a.TotalScore));
            state.GirlfriendCoupleActivityBestExamples.Sort((a, b) => b.WeightedScore.CompareTo(a.WeightedScore));
            Trim(state.GirlfriendCoupleActivityScores, 32);
            Trim(state.GirlfriendCoupleActivityBestExamples, 32);
        }

        private static string FormatCoupleActivityVectorSummary(IReadOnlyList<GirlfriendVectorItem> activities)
        {
            List<string> parts = (activities ?? Array.Empty<GirlfriendVectorItem>())
                .Where(x => x != null)
                .Select(x => FirstNonEmpty(x.DisplayName, x.ItemId) + " +" + (x.Score * Math.Max(0, Math.Min(1, x.Confidence))).ToString("0.0", CultureInfo.InvariantCulture))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(5)
                .ToList();
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        private static string FormatMetVectorSummary(IReadOnlyList<GirlfriendVectorItem> needs, IReadOnlyList<GirlfriendVectorItem> wants)
        {
            List<string> parts = new List<string>();
            parts.AddRange((needs ?? Array.Empty<GirlfriendVectorItem>()).Select(x => "need:" + FirstNonEmpty(x.DisplayName, x.ItemId)).Where(x => !string.IsNullOrWhiteSpace(x)).Take(4));
            parts.AddRange((wants ?? Array.Empty<GirlfriendVectorItem>()).Select(x => "want:" + FirstNonEmpty(x.DisplayName, x.ItemId)).Where(x => !string.IsNullOrWhiteSpace(x)).Take(4));
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        private static void AccumulateGirlfriendPerformanceVector(List<GirlfriendVectorItem> accumulator, IReadOnlyList<GirlfriendVectorItem> additions, int maxItems)
        {
            if (accumulator == null || additions == null) return;
            foreach (GirlfriendVectorItem addition in additions.Where(x => x != null && (!string.IsNullOrWhiteSpace(x.ItemId) || !string.IsNullOrWhiteSpace(x.DisplayName))))
            {
                string itemId = string.IsNullOrWhiteSpace(addition.ItemId) ? Slugify(addition.DisplayName) : Slugify(addition.ItemId);
                GirlfriendVectorItem? existing = accumulator.FirstOrDefault(x => string.Equals(Slugify(FirstNonEmpty(x.ItemId, x.DisplayName)), itemId, StringComparison.OrdinalIgnoreCase));
                double contribution = Math.Max(0, Math.Min(100, addition.Score)) * Math.Max(0, Math.Min(1, addition.Confidence <= 0 ? 0.25 : addition.Confidence));
                if (existing == null)
                {
                    accumulator.Add(new GirlfriendVectorItem
                    {
                        ItemId = itemId,
                        DisplayName = FirstNonEmpty(addition.DisplayName, addition.ItemId),
                        Confidence = Math.Max(0, Math.Min(1, addition.Confidence)),
                        Score = Math.Min(100, contribution),
                        Reason = string.Empty
                    });
                }
                else
                {
                    existing.DisplayName = FirstNonEmpty(existing.DisplayName, addition.DisplayName, addition.ItemId);
                    existing.Confidence = Math.Max(existing.Confidence, Math.Max(0, Math.Min(1, addition.Confidence)));
                    existing.Score = Math.Min(100, existing.Score + Math.Max(1, contribution / 4.0));
                    existing.Reason = string.Empty;
                }
            }
            accumulator.Sort((a, b) => b.Score.CompareTo(a.Score));
            Trim(accumulator, maxItems);
        }

        private static List<GirlfriendVectorItem> CleanGirlfriendVector(IEnumerable<GirlfriendVectorItem>? source, int maxItems)
        {
            return (source ?? Enumerable.Empty<GirlfriendVectorItem>())
                .Where(x => x != null && (!string.IsNullOrWhiteSpace(x.ItemId) || !string.IsNullOrWhiteSpace(x.DisplayName)))
                .Select(x => new GirlfriendVectorItem
                {
                    ItemId = Slugify(FirstNonEmpty(x.ItemId, x.DisplayName)),
                    DisplayName = CleanMarkerText(FirstNonEmpty(x.DisplayName, x.ItemId), 160),
                    Confidence = Math.Max(0, Math.Min(1, x.Confidence)),
                    Score = Math.Max(0, Math.Min(100, x.Score)),
                    Reason = string.Empty
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Confidence)
                .Take(Math.Max(1, maxItems))
                .ToList();
        }

        private static List<string> CleanStringList(IEnumerable<string>? source, int maxItems)
        {
            return (source ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => CleanMarkerText(x, 500))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maxItems))
                .ToList();
        }


        private static string FormatGirlfriendVectorString(IEnumerable<GirlfriendVectorItem>? items, int maxItems)
        {
            List<string> parts = (items ?? Enumerable.Empty<GirlfriendVectorItem>())
                .Where(x => x != null && (!string.IsNullOrWhiteSpace(x.ItemId) || !string.IsNullOrWhiteSpace(x.DisplayName)))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Confidence)
                .Take(Math.Max(1, maxItems))
                .Select(x => SanitizeCompactId(FirstNonEmpty(x.ItemId, x.DisplayName)) + ":" + Math.Max(0, Math.Min(100, x.Score)).ToString("0.0", CultureInfo.InvariantCulture) + ":" + Math.Max(0, Math.Min(1, x.Confidence)).ToString("0.00", CultureInfo.InvariantCulture))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            return parts.Count == 0 ? "none" : string.Join(";", parts);
        }

        private static string FormatHistoryCompactLine(DateTimeOffset createdAt, string? slotId, IEnumerable<GirlfriendVectorItem>? items, int maxItems)
        {
            string slot = string.IsNullOrWhiteSpace(slotId) ? string.Empty : SanitizeCompactId(slotId);
            return createdAt.UtcDateTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture) + "|" + slot + "|" + FormatGirlfriendVectorString(items, maxItems);
        }

        private static string SanitizeCompactId(string? value)
        {
            string raw = FirstNonEmpty(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return "unknown";
            string compact = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrWhiteSpace(compact)) return "unknown";
            return char.ToLowerInvariant(compact[0]) + (compact.Length > 1 ? compact.Substring(1) : string.Empty);
        }

        private static void NormalizeGirlfriendHourlyContextJson(StarGirlfriendHourlyStateContext context)
        {
            if (context == null) return;
            context.NeedVector = CleanGirlfriendVector(context.NeedVector, 30);
            context.WantVector = CleanGirlfriendVector(context.WantVector, 30);
            context.ThingsSheWantsToSay = CleanStringList(context.ThingsSheWantsToSay, 8);
            context.CuteThingsSheWantsToSay = CleanStringList(context.CuteThingsSheWantsToSay, 8);
            if (!context.IsAvailable) return;
            context.Summary = CleanMarkerText(context.Summary, 360);
            context.RelationshipPosture = CleanMarkerText(context.RelationshipPosture, 300);
            context.NeedSummary = CleanMarkerText(context.NeedSummary, 240);
            context.WantSummary = CleanMarkerText(context.WantSummary, 240);
            context.CuteModeReason = CleanMarkerText(context.CuteModeReason, 180);
            context.CuteModeSignature = CleanMarkerText(context.CuteModeSignature, 180);
            context.Json = System.Text.Json.JsonSerializer.Serialize(new
            {
                starId = context.StarId ?? string.Empty,
                generatedAt = context.GeneratedAt?.ToString("o") ?? string.Empty,
                slotId = context.SlotId ?? string.Empty,
                summary = context.Summary ?? string.Empty,
                relationshipPosture = context.RelationshipPosture ?? string.Empty,
                needSummary = context.NeedSummary ?? string.Empty,
                wantSummary = context.WantSummary ?? string.Empty,
                needVector = FormatGirlfriendVectorString(context.NeedVector, 12),
                wantVector = FormatGirlfriendVectorString(context.WantVector, 12),
                thingsSheWantsToSay = context.ThingsSheWantsToSay.Take(6).ToList(),
                cuteThingsSheWantsToSay = context.CuteThingsSheWantsToSay.Take(6).ToList(),
                overallConfidence = Math.Max(0, Math.Min(1, context.OverallConfidence)),
                overallScore = Math.Max(0, Math.Min(100, context.OverallScore)),
                cuteModeActive = context.CuteModeActive,
                cuteModeProbability = Math.Max(0, Math.Min(1, context.CuteModeProbability <= 0 ? 0.35 : context.CuteModeProbability)),
                cuteModeSignature = context.CuteModeSignature ?? string.Empty
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
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
            if (state.RelationshipEvents == null)
            {
                state.RelationshipEvents = new List<string>();
                changed = true;
            }
            if (state.MemoryLog == null)
            {
                state.MemoryLog = new List<ConversationLogEntry>();
                changed = true;
            }
            if (state.TenTurnConversationSummaries == null)
            {
                state.TenTurnConversationSummaries = new List<StarConversationSummaryBlock>();
                changed = true;
            }
            if (state.HundredTurnConversationSummaries == null)
            {
                state.HundredTurnConversationSummaries = new List<StarConversationSummaryBlock>();
                changed = true;
            }
            if (state.OldConversationArchives == null)
            {
                state.OldConversationArchives = new List<StarConversationArchiveEntry>();
                changed = true;
            }
            if (state.OtherStars == null)
            {
                state.OtherStars = new List<InterstarRelationshipState>();
                changed = true;
            }
            foreach (InterstarRelationshipState relation in state.OtherStars.Where(x => x != null))
            {
                if (relation.History == null)
                {
                    relation.History = new List<InterstarRelationshipHistoryEntry>();
                    changed = true;
                }
            }
            if (state.ImportantPlayerQuotes == null)
            {
                state.ImportantPlayerQuotes = new List<string>();
                changed = true;
            }
            if (state.MemoryNotes == null)
            {
                state.MemoryNotes = new List<string>();
                changed = true;
            }
            if (state.ShortTermMemory == null)
            {
                state.ShortTermMemory = new List<StarShortTermMemoryEntry>();
                changed = true;
            }
            if (state.ThingsLearnedOrSuggestedByPlayer == null)
            {
                state.ThingsLearnedOrSuggestedByPlayer = new List<string>();
                changed = true;
            }
            if (state.IncomingUnresolvedVectors == null) { state.IncomingUnresolvedVectors = new List<string>(); changed = true; }
            if (state.OutgoingUnresolvedVectors == null) { state.OutgoingUnresolvedVectors = new List<string>(); changed = true; }
            if (state.UnresolvedResolutionHistory == null) { state.UnresolvedResolutionHistory = new List<string>(); changed = true; }
            if (state.GirlfriendHistory == null)
            {
                state.GirlfriendHistory = new List<string>();
                changed = true;
            }
            if (state.PetNamesPlayerUsesForStar == null)
            {
                state.PetNamesPlayerUsesForStar = new List<string>();
                changed = true;
            }
            if (state.PetNamesStarUsesForPlayer == null)
            {
                state.PetNamesStarUsesForPlayer = new List<string>();
                changed = true;
            }
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
            if (state.GirlfriendHourlyState == null)
            {
                state.GirlfriendHourlyState = new StarGirlfriendHourlyStateContext();
                changed = true;
            }
            if (state.GirlfriendDesiredStateHistory == null)
            {
                state.GirlfriendDesiredStateHistory = new List<GirlfriendVectorHistoryEntry>();
                changed = true;
            }
            if (state.GirlfriendNeedsMetHistory == null)
            {
                state.GirlfriendNeedsMetHistory = new List<GirlfriendNeedFulfillmentHistoryEntry>();
                changed = true;
            }
            if (state.GirlfriendDesiredNeedVectorHistory == null) { state.GirlfriendDesiredNeedVectorHistory = new List<string>(); changed = true; }
            if (state.GirlfriendDesiredWantVectorHistory == null) { state.GirlfriendDesiredWantVectorHistory = new List<string>(); changed = true; }
            if (state.GirlfriendMetNeedVectorHistory == null) { state.GirlfriendMetNeedVectorHistory = new List<string>(); changed = true; }
            if (state.GirlfriendMetWantVectorHistory == null) { state.GirlfriendMetWantVectorHistory = new List<string>(); changed = true; }
            if (state.GirlfriendNeedsMetPerformanceVector == null)
            {
                state.GirlfriendNeedsMetPerformanceVector = new List<GirlfriendVectorItem>();
                changed = true;
            }
            if (state.GirlfriendWantsMetPerformanceVector == null)
            {
                state.GirlfriendWantsMetPerformanceVector = new List<GirlfriendVectorItem>();
                changed = true;
            }
            if (state.GirlfriendCoupleActivityScores == null)
            {
                state.GirlfriendCoupleActivityScores = new List<GirlfriendCoupleActivityScore>();
                changed = true;
            }
            if (state.GirlfriendCoupleActivityHistory == null)
            {
                state.GirlfriendCoupleActivityHistory = new List<GirlfriendCoupleActivityHistoryEntry>();
                changed = true;
            }
            if (state.GirlfriendCoupleActivityBestExamples == null)
            {
                state.GirlfriendCoupleActivityBestExamples = new List<GirlfriendCoupleActivityBestExample>();
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
            if (BackfillConversationRollups(state)) changed = true;
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

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return string.Empty;
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

        private bool PruneExpiredShortTermMemory(StarState state, DateTimeOffset now)
        {
            if (state == null) return false;
            state.ShortTermMemory ??= new List<StarShortTermMemoryEntry>();
            int windowMs = Math.Max(1000, _options.ShortTermMemoryWindowMs);
            DateTimeOffset cutoff = now.AddMilliseconds(-windowMs);
            int before = state.ShortTermMemory.Count;
            state.ShortTermMemory = state.ShortTermMemory
                .Where(x => x != null && x.CreatedAt >= cutoff && !string.IsNullOrWhiteSpace(x.Text))
                .OrderByDescending(x => x.CreatedAt)
                .Take(Math.Max(1, _options.MaxShortTermMemoryEntries))
                .ToList();
            return state.ShortTermMemory.Count != before;
        }

        private void AddShortTermMemoryEntries(StarState state, IEnumerable<string>? items, DateTimeOffset now, string? source)
        {
            if (state == null || items == null) return;
            state.ShortTermMemory ??= new List<StarShortTermMemoryEntry>();
            foreach (string raw in items)
            {
                string text = CompactDurableText(raw, 700).Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                StarShortTermMemoryEntry? existing = state.ShortTermMemory
                    .FirstOrDefault(x => x != null && string.Equals(x.Text, text, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    // Preserve the deterministic time this short-term fact was originally added.
                    // Once it ages out of the configured window, the same fact may be added again
                    // as a new short-term memory if it is genuinely current again.
                    continue;
                }
                state.ShortTermMemory.Insert(0, new StarShortTermMemoryEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CreatedAt = now,
                    Text = text,
                    Source = string.IsNullOrWhiteSpace(source) ? "background-review" : source.Trim()
                });
            }
            PruneExpiredShortTermMemory(state, now);
            Trim(state.ShortTermMemory, Math.Max(1, _options.MaxShortTermMemoryEntries));
        }

        private static IEnumerable<string> BuildInternalUpdateMemoryNotes(StarStateUpdateRequest request, RelationshipDelta applied)
        {
            if (request == null) yield break;
            bool changed = applied != null && (applied.Heart != 0 || applied.Trust != 0 || applied.Romance != 0 || applied.BenaveilInfluence != 0 || Math.Abs(applied.EmotionalConnectionPercent) >= 0.001);
            if (!changed) yield break;
            string summary = string.IsNullOrWhiteSpace(request.ChangesSummary) ? request.ConversationSummary : request.ChangesSummary;
            if (string.IsNullOrWhiteSpace(summary)) summary = "Hidden background Star-state update applied.";
            yield return "Internal background update: " + TruncateLocal(summary, 360);
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

        private static void AutoPromoteAllyIfReady(StarState state, DateTimeOffset now)
        {
            if (state == null) return;
            if (state.IsAlly) return;
            if (!state.IsMet) return;

            bool readyByStats = state.Heart >= 3 && state.Trust >= 2;
            bool readyByCombinedBond = state.Heart + state.Trust >= 6;
            bool readyByConnection = state.EmotionalConnectionPercent >= 6.0;
            if (!readyByStats && !readyByCombinedBond && !readyByConnection) return;

            state.IsAlly = true;
            state.RelationshipEvents.Insert(0, "Auto ally unlocked: repeated positive emotional-vector evidence made this met Star willing to join the Last Cat as an ally.");
        }

        private static void UpdateGirlfriendReadiness(StarState state, DateTimeOffset now)
        {
            const double ReadyConnection = 25.0;
            const int ReadyHeart = 6;
            const int ReadyTrust = 4;
            const int ReadyRomance = 3;

            bool ready = state.IsGirlfriend || (state.IsAlly && state.EmotionalConnectionPercent >= ReadyConnection && state.Heart >= ReadyHeart && state.Trust >= ReadyTrust && state.Romance >= ReadyRomance);
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
                ? "Ready for girlfriend path: ask directly, and she can decide based on this bond."
                : "Not ready: requires ally status, EmotionalConnectionPercent >= 25, Heart >= 6, Trust >= 4, and Romance >= 3. Build ally trust, safe affection, and personality-matched emotional vectors.";
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

        private static bool BackfillConversationRollups(StarState state)
        {
            if (state == null || state.InteractionCount < 10) return false;
            state.TenTurnConversationSummaries ??= new List<StarConversationSummaryBlock>();
            state.HundredTurnConversationSummaries ??= new List<StarConversationSummaryBlock>();

            List<ConversationLogEntry> retained = new List<ConversationLogEntry>();
            if (state.MemoryLog != null) retained.AddRange(state.MemoryLog.Where(x => x != null));
            if (state.OldConversationArchives != null)
            {
                retained.AddRange(state.OldConversationArchives.Where(x => x != null).SelectMany(x => x.Entries ?? new List<ConversationLogEntry>()).Where(x => x != null));
            }
            List<ConversationLogEntry> newestFirst = retained.Distinct().OrderByDescending(x => x.CreatedAt).ToList();
            Dictionary<int, ConversationLogEntry> byTurn = new Dictionary<int, ConversationLogEntry>();
            for (int i = 0; i < newestFirst.Count; i++)
            {
                int turn = state.InteractionCount - i;
                if (turn > 0) byTurn[turn] = newestFirst[i];
            }

            bool changed = false;
            int lastCompletedTen = (state.InteractionCount / 10) * 10;
            for (int endTurn = 10; endTurn <= lastCompletedTen; endTurn += 10)
            {
                int startTurn = endTurn - 9;
                if (state.TenTurnConversationSummaries.Any(x => x != null && x.StartTurn == startTurn && x.EndTurn == endTurn)) continue;
                List<ConversationLogEntry> block = Enumerable.Range(startTurn, 10).Where(byTurn.ContainsKey).Select(x => byTurn[x]).ToList();
                if (block.Count != 10) continue;
                string summary = string.Join(" ", block.Select(x => string.IsNullOrWhiteSpace(x.Summary)
                    ? ("The player said " + TruncateLocal(x.PlayerText, 100) + ", and the Star replied " + TruncateLocal(x.StarResponse, 100) + ".")
                    : TruncateLocal(x.Summary, 180)));
                summary = CompactDurableText(summary, 1400);
                if (string.IsNullOrWhiteSpace(summary)) continue;
                state.TenTurnConversationSummaries.Add(new StarConversationSummaryBlock
                {
                    StartTurn = startTurn,
                    EndTurn = endTurn,
                    CreatedAt = block.Last().CreatedAt,
                    Summary = summary
                });
                changed = true;
            }
            state.TenTurnConversationSummaries = state.TenTurnConversationSummaries.OrderByDescending(x => x.EndTurn).ToList();

            int lastCompletedHundred = (state.InteractionCount / 100) * 100;
            for (int endTurn = 100; endTurn <= lastCompletedHundred; endTurn += 100)
            {
                int startTurn = endTurn - 99;
                if (state.HundredTurnConversationSummaries.Any(x => x != null && x.StartTurn == startTurn && x.EndTurn == endTurn)) continue;
                List<StarConversationSummaryBlock> blocks = state.TenTurnConversationSummaries
                    .Where(x => x != null && x.StartTurn >= startTurn && x.EndTurn <= endTurn && !string.IsNullOrWhiteSpace(x.Summary))
                    .OrderBy(x => x.StartTurn)
                    .ToList();
                if (blocks.Count != 10) continue;
                string summary = CompactDurableText(string.Join(" ", blocks.Select(x => x.Summary)), 1800);
                if (string.IsNullOrWhiteSpace(summary)) continue;
                state.HundredTurnConversationSummaries.Add(new StarConversationSummaryBlock
                {
                    StartTurn = startTurn,
                    EndTurn = endTurn,
                    CreatedAt = blocks.Last().CreatedAt,
                    Summary = summary
                });
                changed = true;
            }
            state.HundredTurnConversationSummaries = state.HundredTurnConversationSummaries.OrderByDescending(x => x.EndTurn).ToList();
            return changed;
        }

        private static void StoreConversationRollups(StarState state, StarStateUpdateRequest request, DateTimeOffset now)
        {
            if (state == null || request == null || state.InteractionCount <= 0) return;
            state.TenTurnConversationSummaries ??= new List<StarConversationSummaryBlock>();
            state.HundredTurnConversationSummaries ??= new List<StarConversationSummaryBlock>();

            int endTurn = state.InteractionCount;
            if (endTurn % 10 == 0)
            {
                int startTurn = endTurn - 9;
                string summary = CompactDurableText(request.TenTurnConversationSummary, 1400);
                if (string.IsNullOrWhiteSpace(summary)) summary = BuildTenTurnRollupFallback(state, startTurn, endTurn);
                UpsertConversationRollup(state.TenTurnConversationSummaries, startTurn, endTurn, summary, now);
            }

            if (endTurn % 100 == 0)
            {
                int startTurn = endTurn - 99;
                string summary = CompactDurableText(request.HundredTurnConversationSummary, 1800);
                if (string.IsNullOrWhiteSpace(summary))
                {
                    List<StarConversationSummaryBlock> blocks = state.TenTurnConversationSummaries
                        .Where(x => x != null && x.StartTurn >= startTurn && x.EndTurn <= endTurn && !string.IsNullOrWhiteSpace(x.Summary))
                        .OrderBy(x => x.StartTurn)
                        .Take(10)
                        .ToList();
                    summary = CompactDurableText(string.Join(" ", blocks.Select(x => x.Summary)), 1800);
                }
                UpsertConversationRollup(state.HundredTurnConversationSummaries, startTurn, endTurn, summary, now);
            }
        }

        private static void UpsertConversationRollup(List<StarConversationSummaryBlock> blocks, int startTurn, int endTurn, string summary, DateTimeOffset now)
        {
            if (blocks == null || string.IsNullOrWhiteSpace(summary)) return;
            StarConversationSummaryBlock? existing = blocks.FirstOrDefault(x => x != null && x.StartTurn == startTurn && x.EndTurn == endTurn);
            if (existing == null)
            {
                blocks.Insert(0, new StarConversationSummaryBlock
                {
                    StartTurn = startTurn,
                    EndTurn = endTurn,
                    CreatedAt = now,
                    Summary = summary
                });
            }
            else
            {
                existing.CreatedAt = now;
                existing.Summary = summary;
            }
        }

        private static string BuildTenTurnRollupFallback(StarState state, int startTurn, int endTurn)
        {
            if (state == null) return string.Empty;
            List<ConversationLogEntry> retained = new List<ConversationLogEntry>();
            if (state.MemoryLog != null) retained.AddRange(state.MemoryLog.Where(x => x != null));
            if (state.OldConversationArchives != null)
            {
                retained.AddRange(state.OldConversationArchives.Where(x => x != null).SelectMany(x => x.Entries ?? new List<ConversationLogEntry>()).Where(x => x != null));
            }
            List<ConversationLogEntry> newest = retained.Distinct().OrderByDescending(x => x.CreatedAt).Take(Math.Max(1, endTurn - startTurn + 1)).OrderBy(x => x.CreatedAt).ToList();
            string combined = string.Join(" ", newest.Select(x => string.IsNullOrWhiteSpace(x.Summary)
                ? ("The player said " + TruncateLocal(x.PlayerText, 120) + ", and the Star replied " + TruncateLocal(x.StarResponse, 120) + ".")
                : TruncateLocal(x.Summary, 220)));
            return CompactDurableText(combined, 1400);
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

        private string TryCreateAutoQuote(StarStateUpdateRequest request, RelationshipDelta delta, out string reason)
        {
            reason = string.Empty;
            if (!_options.AutoCaptureHighImpactPlayerQuotes) return string.Empty;
            string text = (request.PlayerText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            List<TopAffectedEmotion> affected = RelationshipDeltaCalculator
                .BuildTopAffectedEmotions(request.EmotionalEffectVector, request.NegativeEmotionVector)
                .Where(x => x != null)
                .OrderByDescending(x => x.WeightedScore)
                .ThenByDescending(x => x.Score)
                .ThenByDescending(x => x.Confidence)
                .ToList();

            double minConfidence = Math.Max(0.0, _options.AutoImportantQuoteMinEmotionConfidence);
            double minScore = Math.Max(0.0, _options.AutoImportantQuoteMinEmotionScore);
            double minWeighted = Math.Max(0.0, _options.AutoImportantQuoteMinWeightedEmotionScore);
            TopAffectedEmotion? qualifying = affected.FirstOrDefault(x =>
                x.Confidence >= minConfidence &&
                x.Score >= minScore &&
                x.WeightedScore >= minWeighted);

            if (qualifying == null) return string.Empty;

            int maxLength = Math.Max(80, _options.AutoImportantQuoteMaxLength);
            string cleaned = CleanMarkerText(text, maxLength);
            if (string.IsNullOrWhiteSpace(cleaned)) return string.Empty;

            reason = "auto-high-emotion-quote:"
                + qualifying.Valence
                + ":"
                + qualifying.EmotionId
                + ":score="
                + qualifying.Score.ToString("0", CultureInfo.InvariantCulture)
                + ":confidence="
                + qualifying.Confidence.ToString("0.00", CultureInfo.InvariantCulture)
                + ":weighted="
                + qualifying.WeightedScore.ToString("0.0", CultureInfo.InvariantCulture);
            return cleaned;
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

        private static string CompactDurableText(string? value, int maxCharacters)
        {
            string text = value ?? string.Empty;
            if (text.Length <= maxCharacters) return text;
            int head = Math.Max(1, (int)(maxCharacters * 0.70));
            int tail = Math.Max(1, maxCharacters - head - 96);
            int omitted = Math.Max(0, text.Length - head - tail);
            return text.Substring(0, head)
                + "\n[... " + omitted + " characters omitted from durable state; complete response was delivered to UI/TTS ...]\n"
                + text.Substring(text.Length - tail);
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

        private static List<string> AddUniqueWithResult(List<string> target, IEnumerable<string> values, int max)
        {
            List<string> added = new List<string>();
            if (target == null || values == null) return added;
            foreach (string raw in values)
            {
                string value = CompactDurableText(raw, 900);
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (target.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))) continue;
                target.Insert(0, value);
                added.Add(value);
            }
            Trim(target, max);
            return added;
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
