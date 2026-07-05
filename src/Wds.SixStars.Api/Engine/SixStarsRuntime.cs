using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Screen;
using Wds.SixStars.Api.Stars;
using Wds.SixStars.Api.State;

namespace Wds.SixStars.Api.Engine
{
    public sealed class SixStarsRuntime
    {
        private readonly IStarProfileProvider _profileProvider;
        private readonly IStarAgent _agent;
        private readonly IRelationshipDeltaCalculator _deltaCalculator;
        private readonly IStarMemoryApi _memoryApi;
        private readonly IScreenContextStore? _screenContextStore;
        private readonly int _recentScreenCount;
        private readonly int _recentHistoryCount;
        private readonly object _backgroundGate = new object();
        private CancellationTokenSource? _backgroundAnalysisCts;
        private CancellationTokenSource? _backgroundStoryGenerationCts;
        private readonly SemaphoreSlim _backgroundAnalysisQueue = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, PendingBackgroundAnalysis> _pendingBackgroundAnalysis = new Dictionary<string, PendingBackgroundAnalysis>(StringComparer.OrdinalIgnoreCase);
        private static readonly object StoryRandomGate = new object();
        private static readonly Random StoryRandom = new Random();

        public event EventHandler<StarInteractionResponse>? BackgroundAnalysisCompleted;
        public event EventHandler<Exception>? BackgroundAnalysisFailed;
        public event EventHandler<StarInteractionResponse>? StoryGenerationCompleted;
        public event EventHandler<Exception>? StoryGenerationFailed;

        public SixStarsRuntime(
            IStarProfileProvider profileProvider,
            IStarAgent agent,
            IRelationshipDeltaCalculator deltaCalculator,
            IStarStateStore stateStore)
            : this(profileProvider, agent, deltaCalculator, new StarMemoryApi(stateStore), null)
        {
        }

        public SixStarsRuntime(
            IStarProfileProvider profileProvider,
            IStarAgent agent,
            IRelationshipDeltaCalculator deltaCalculator,
            IStarStateStore stateStore,
            IScreenContextStore? screenContextStore,
            int recentScreenCount = 5,
            int recentHistoryCount = 10,
            int maxMemoryLogEntries = 160,
            int maxImportantQuotes = 80,
            int maxMemoryNotes = 160)
            : this(
                profileProvider,
                agent,
                deltaCalculator,
                new StarMemoryApi(stateStore, new StarMemoryOptions
                {
                    MaxMemoryLogEntries = maxMemoryLogEntries,
                    MaxImportantQuotes = maxImportantQuotes,
                    MaxMemoryNotes = maxMemoryNotes,
                    MaxRecentHistoryForPrompt = recentHistoryCount
                }),
                screenContextStore,
                recentScreenCount,
                recentHistoryCount)
        {
        }

        public SixStarsRuntime(
            IStarProfileProvider profileProvider,
            IStarAgent agent,
            IRelationshipDeltaCalculator deltaCalculator,
            IStarMemoryApi memoryApi,
            IScreenContextStore? screenContextStore = null,
            int recentScreenCount = 5,
            int recentHistoryCount = 10)
        {
            _profileProvider = profileProvider;
            _agent = agent;
            _deltaCalculator = deltaCalculator;
            _memoryApi = memoryApi;
            _screenContextStore = screenContextStore;
            _recentScreenCount = Math.Max(1, recentScreenCount);
            _recentHistoryCount = Math.Max(1, recentHistoryCount);
        }

        public Task<StarState> GetStarStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            return _memoryApi.GetStarStateAsync(sessionId, starId, cancellationToken);
        }

        public Task<StarInteractionHistoryPage> GetStarInteractionHistoryAsync(string sessionId, string starId, int skip, int take, CancellationToken cancellationToken)
        {
            return _memoryApi.GetStarInteractionHistoryAsync(sessionId, starId, skip, take, cancellationToken);
        }

        public bool HasPendingOrRunningBackgroundAnalysis
        {
            get
            {
                lock (_backgroundGate)
                {
                    return _pendingBackgroundAnalysis.Count > 0 || _backgroundAnalysisQueue.CurrentCount == 0;
                }
            }
        }

        public Task<SixStarsSaveData> ExportSaveDataAsync(string sessionId, CancellationToken cancellationToken)
        {
            return _memoryApi.ExportSaveDataAsync(sessionId, cancellationToken);
        }

        public Task ImportSaveDataAsync(SixStarsSaveData saveData, CancellationToken cancellationToken)
        {
            return _memoryApi.ImportSaveDataAsync(saveData, cancellationToken);
        }


        public Task<StarState> ResetStarMemoryAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            return _memoryApi.ResetStarMemoryAsync(sessionId, starId, cancellationToken);
        }

        public Task<StarState> SetStarAllyAsync(string sessionId, string starId, bool isAlly, CancellationToken cancellationToken)
        {
            return _memoryApi.SetStarAllyAsync(sessionId, starId, isAlly, cancellationToken);
        }

        public Task<StarState> SetStarMetAsync(string sessionId, string starId, bool isMet, CancellationToken cancellationToken)
        {
            return _memoryApi.SetStarMetAsync(sessionId, starId, isMet, cancellationToken);
        }

        public Task<StarState> SetStarWorldStateAsync(StarWorldStateUpdateRequest request, CancellationToken cancellationToken)
        {
            return _memoryApi.SetStarWorldStateAsync(request, cancellationToken);
        }

        public Task<StarState> ApplyStarEffectorAsync(string sessionId, string starId, StarRelationshipEffector effector, CancellationToken cancellationToken)
        {
            return _memoryApi.ApplyStarEffectorAsync(sessionId, starId, effector, cancellationToken);
        }

        public Task<StarState> ClearStarEffectorAsync(string sessionId, string starId, string effectorId, CancellationToken cancellationToken)
        {
            return _memoryApi.ClearStarEffectorAsync(sessionId, starId, effectorId, cancellationToken);
        }

        public Task<StarState> AddStarDialogueEntryAsync(StarDialogueMemoryUpdateRequest request, CancellationToken cancellationToken)
        {
            return _memoryApi.AddStarDialogueEntryAsync(request, cancellationToken);
        }

        public Task<StarState> SetStarDialogueStateAsync(StarDialogueProgressUpdateRequest request, CancellationToken cancellationToken)
        {
            return _memoryApi.SetStarDialogueStateAsync(request, cancellationToken);
        }

        public Task<StarState> ClearStarDialogueStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            return _memoryApi.ClearStarDialogueStateAsync(sessionId, starId, cancellationToken);
        }

        public async Task AddScreenCaptureAsync(string sessionId, ScreenCaptureSummary capture, CancellationToken cancellationToken)
        {
            if (_screenContextStore == null)
            {
                return;
            }

            await _screenContextStore.AddAsync(sessionId, capture, cancellationToken).ConfigureAwait(false);
        }

        public void CancelBackgroundAnalysis(string reason = "Cancel background Star analysis")
        {
            lock (_backgroundGate)
            {
                try
                {
                    _backgroundAnalysisCts?.Cancel();
                    _backgroundAnalysisCts?.Dispose();
                    _backgroundStoryGenerationCts?.Cancel();
                    _backgroundStoryGenerationCts?.Dispose();
                    _pendingBackgroundAnalysis.Clear();
                }
                catch
                {
                }
                _backgroundAnalysisCts = new CancellationTokenSource();
                _backgroundStoryGenerationCts = new CancellationTokenSource();
            }
            System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background analysis cancelled: " + reason);
        }

        public async Task<StarInteractionResponse> ProcessAsync(StarInteractionRequest request, CancellationToken cancellationToken)
        {
            if (_screenContextStore != null && request.RecentScreenCaptures.Count == 0)
            {
                request.RecentScreenCaptures.AddRange(await _screenContextStore.GetRecentAsync(request.SessionId, _recentScreenCount, cancellationToken).ConfigureAwait(false));
            }

            StarProfile profile = _profileProvider.GetProfile(request.StarId);
            await PopulateKnownStarContextAsync(request, cancellationToken).ConfigureAwait(false);
            StarMemorySnapshot snapshot = await _memoryApi.GetStarMemorySnapshotAsync(request.SessionId, request.StarId, _recentHistoryCount, cancellationToken).ConfigureAwait(false);
            StarState state = snapshot.State;

            StarRequestIntentResult? requestIntent = await ClassifyAndPrepareRequestModeAsync(profile, request, state, cancellationToken).ConfigureAwait(false);

            if (request.RequestType == RequestType.Story && !IsStoryGenerationWorkerRequest(request))
            {
                StarInteractionResponse thinkingResponse = CreateStoryThinkingResponse(profile, request, requestIntent);
                ApplyVoiceDefaults(profile, thinkingResponse);
                QueueStoryGenerationAfterThinkingResponse(profile, request, state, requestIntent, cancellationToken);
                return thinkingResponse;
            }

            StarInteractionResponse response = await _agent.GenerateAsync(profile, request, state, cancellationToken).ConfigureAwait(false);
            if (requestIntent != null)
            {
                response.Debug.Notes.Add("Pre-flight story intent check: " + requestIntent.Source + " confidence=" + requestIntent.Confidence.ToString("0.00") + " story=" + requestIntent.IsStoryRequest + " reason=" + requestIntent.Reason);
            }
            response = await TryResolveInformationNeedAsync(profile, request, state, response, cancellationToken).ConfigureAwait(false);
            ApplyVoiceDefaults(profile, response);

            if (_agent is IStarTurnAnalyzer analyzer)
            {
                response.StateDeltas = RelationshipDelta.Zero();
                response.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "BackgroundStarAnalysisQueued",
                    Target = request.StarId,
                    Data =
                    {
                        { "speechParts", response.SpokenParts.Count.ToString() },
                        { "note", "Fast spoken response returned immediately; emotion/memory update will wait until voice generation reports ready." }
                    }
                });
                string backgroundAnalysisId = Guid.NewGuid().ToString("N");
                response.BackgroundAnalysisId = backgroundAnalysisId;
                response.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "BackgroundStarAnalysisWaitingForVoice",
                    Target = request.StarId,
                    Data =
                    {
                        { "backgroundAnalysisId", backgroundAnalysisId },
                        { "note", "Call StartBackgroundAnalysisAfterVoiceReady after foreground voice generation completes." }
                    }
                });
                response.Debug.Notes.Add("Fast foreground response returned before durable memory/emotion update. Background review is waiting for voice generation to finish.");
                StorePendingBackgroundAnalysis(backgroundAnalysisId, analyzer, profile, request, state, response);
                return response;
            }

            await ApplyAnalysisAndMemoryUpdateAsync(request, response, cancellationToken).ConfigureAwait(false);
            return response;
        }


        private async Task<StarInteractionResponse> TryResolveInformationNeedAsync(StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse response, CancellationToken cancellationToken)
        {
            if (request == null || response == null) return response;
            if (request.GameContext.Flags.TryGetValue("informationFollowup", out string? already) && string.Equals(already, "true", StringComparison.OrdinalIgnoreCase))
            {
                return response;
            }

            List<InformationNeedVectorItem> needs = NormalizeInformationNeeds(response, request.PlayerText);
            if (needs.Count == 0) return response;

            string context = BuildRequestedInformationContext(profile, state, needs, request.PlayerText, 180000);
            response.NeedsMoreInformation = true;
            response.InformationNeeds = needs;
            response.InformationFollowupAttempted = true;
            response.InformationFollowupContextSummary = SummarizeInformationNeeds(needs, context);

            if (string.IsNullOrWhiteSpace(context))
            {
                response.Debug.Notes.Add("Information-need vector requested extra data, but no matching archive/marker/quote/lore context was available locally.");
                response.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "InformationNeedNoLocalContext",
                    Target = request.StarId,
                    Data =
                    {
                        { "needs", SummarizeNeedsForEvent(needs) },
                        { "note", "The Star asked for more information, but local archives did not have a matching slice." }
                    }
                });
                return response;
            }

            StarInteractionRequest followupRequest = CloneRequestForInformationFollowup(request, context, needs);
            try
            {
                StarInteractionResponse followup = await _agent.GenerateAsync(profile, followupRequest, state, cancellationToken).ConfigureAwait(false);
                followup.NeedsMoreInformation = false;
                followup.InformationNeeds = needs;
                followup.InformationFollowupAttempted = true;
                followup.InformationFollowupSucceeded = true;
                followup.InformationFollowupContextSummary = response.InformationFollowupContextSummary;
                followup.Debug.Notes.Add("Automatic information follow-up pass used local archive/quote/marker/lore context after the foreground response requested more data.");
                followup.Debug.Notes.Add("Needs: " + SummarizeNeedsForEvent(needs));
                followup.Debug.Notes.Add("Extra context chars: " + context.Length);
                followup.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "InformationFollowupCompleted",
                    Target = request.StarId,
                    Data =
                    {
                        { "needs", SummarizeNeedsForEvent(needs) },
                        { "contextChars", context.Length.ToString() },
                        { "note", "The runtime answered with a second LLM pass using requested local history/data." }
                    }
                });
                return followup;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                response.InformationFollowupSucceeded = false;
                response.Debug.Notes.Add("Information follow-up failed; returning the original trying-to-remember response. " + ex.Message);
                response.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "InformationFollowupFailed",
                    Target = request.StarId,
                    Data =
                    {
                        { "needs", SummarizeNeedsForEvent(needs) },
                        { "error", ex.Message }
                    }
                });
                return response;
            }
        }

        private static StarInteractionRequest CloneRequestForInformationFollowup(StarInteractionRequest request, string context, List<InformationNeedVectorItem> needs)
        {
            StarInteractionRequest clone = new StarInteractionRequest
            {
                SessionId = request.SessionId,
                StarId = request.StarId,
                Speaker = request.Speaker,
                RequestType = request.RequestType,
                PlayerText = request.PlayerText,
                GameContext = CloneGameContext(request.GameContext),
                StoryOptions = CloneStoryOptions(request.StoryOptions),
                RecentScreenCaptures = request.RecentScreenCaptures.Select(CloneScreenCaptureSummary).ToList()
            };
            clone.GameContext.Flags["informationFollowup"] = "true";
            clone.GameContext.Flags["extraInformationContext"] = context;
            clone.GameContext.Flags["informationNeeds"] = SummarizeNeedsForEvent(needs);
            clone.GameContext.RecentEvents.Add("The previous foreground reply requested more information. Use EXTRA REQUESTED INFORMATION to answer the player's same message now. Speak as if you remembered or checked the local archives; if it still does not answer it, say you could not find enough.");
            return clone;
        }

        private static List<InformationNeedVectorItem> NormalizeInformationNeeds(StarInteractionResponse response, string playerText)
        {
            List<InformationNeedVectorItem> needs = (response.InformationNeeds ?? new List<InformationNeedVectorItem>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.DataType) && (x.Confidence >= 0.55 || x.Score >= 55 || response.NeedsMoreInformation))
                .Select(x => new InformationNeedVectorItem
                {
                    DataType = NormalizeInformationDataType(x.DataType),
                    Subject = string.IsNullOrWhiteSpace(x.Subject) ? "self" : x.Subject.Trim(),
                    Confidence = Clamp01(x.Confidence),
                    Score = ClampScore(x.Score),
                    Reason = x.Reason ?? string.Empty
                })
                .Take(6)
                .ToList();

            if (needs.Count == 0 && response.NeedsMoreInformation)
            {
                needs.Add(new InformationNeedVectorItem { DataType = "history", Subject = "self", Confidence = 0.72, Score = 72, Reason = "Star set needsMoreInformation without a vector." });
            }

            if (needs.Count == 0 && LooksLikeOldMemoryRequest(playerText))
            {
                needs.Add(new InformationNeedVectorItem { DataType = "history", Subject = ExtractLikelySubject(playerText), Confidence = 0.68, Score = 68, Reason = "Heuristic detected old-memory language." });
            }

            return needs;
        }

        private static string BuildRequestedInformationContext(StarProfile profile, StarState state, List<InformationNeedVectorItem> needs, string playerText, int maxChars)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("EXTRA REQUESTED INFORMATION");
            sb.AppendLine("Use this only to answer the player's latest message. Mention naturally that you remembered, found an old marker, or could not find enough detail. Do not dump the archive.");
            sb.AppendLine("Need vector: " + SummarizeNeedsForEvent(needs));

            bool wantsHistory = Wants(needs, "history") || Wants(needs, "story");
            bool wantsQuotes = Wants(needs, "quotes") || wantsHistory;
            bool wantsMarkers = Wants(needs, "markers");
            bool wantsLore = Wants(needs, "lore") || Wants(needs, "characters") || Wants(needs, "world") || Wants(needs, "bestiary");

            if (wantsQuotes && state.AllImportantPlayerQuotes != null && state.AllImportantPlayerQuotes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("IMPORTANT PLAYER QUOTE SOURCE ARCHIVE (selected slice):");
                foreach (StarImportantQuoteRecord quote in SelectQuoteRecords(state.AllImportantPlayerQuotes, needs, playerText, 48))
                {
                    sb.AppendLine("- " + quote.CreatedAt.ToString("u") + " | " + quote.Reason + " | " + TruncateForInfo(quote.Quote, 420));
                }
            }

            if (wantsMarkers && state.RememberedMarkers != null && state.RememberedMarkers.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("REMEMBERED MARKER SOURCE ARCHIVE (selected slice):");
                foreach (StarRememberedMarker marker in SelectMarkers(state.RememberedMarkers, needs, playerText, 80))
                {
                    sb.AppendLine("- id=" + marker.Id + " | name=" + TruncateForInfo(marker.MarkerName, 120) + " | coords=" + TruncateForInfo(marker.Coordinates, 80) + " | location=" + TruncateForInfo(FirstNonEmpty(marker.LocationText, marker.Area), 160) + " | " + TruncateForInfo(FirstNonEmpty(marker.Description, marker.ReminderText), 420));
                    if (!string.IsNullOrWhiteSpace(marker.SourcePlayerQuote)) sb.AppendLine("  source quote: " + TruncateForInfo(marker.SourcePlayerQuote, 360));
                }
            }

            if (wantsHistory && state.OldConversationArchives != null && state.OldConversationArchives.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("OLD CONVERSATION ARCHIVES (summaries plus selected source turns):");
                foreach (StarConversationArchiveEntry archive in SelectArchives(state.OldConversationArchives, needs, playerText, 8))
                {
                    sb.AppendLine("- archive=" + archive.Id + " | " + archive.StartedAt.ToString("u") + " -> " + archive.EndedAt.ToString("u") + " | turns=" + archive.EntryCount);
                    sb.AppendLine("  summary: " + TruncateForInfo(archive.Summary, 900));
                    foreach (ConversationLogEntry entry in SelectArchiveEntries(archive, needs, playerText, 16))
                    {
                        sb.AppendLine("  turn " + entry.CreatedAt.ToString("u") + ": player=" + QuoteForInfo(TruncateForInfo(entry.PlayerText, 360)) + " star=" + QuoteForInfo(TruncateForInfo(entry.StarResponse, 360)));
                    }
                }
            }

            if (wantsLore)
            {
                sb.AppendLine();
                sb.AppendLine("CHARACTER / WORLD LORE AVAILABLE LOCALLY:");
                sb.AppendLine("Star: " + profile.DisplayName + " | element=" + profile.Element + " | domain=" + profile.Domain + " | core quote=" + profile.CoreQuote);
                if (!string.IsNullOrWhiteSpace(profile.PersonalityShort)) sb.AppendLine("Personality: " + TruncateForInfo(profile.PersonalityShort, 500));
                if (!string.IsNullOrWhiteSpace(profile.BackstorySummary)) sb.AppendLine("Backstory: " + TruncateForInfo(profile.BackstorySummary, 900));
                foreach (string note in profile.LibraryCanonNotes.Take(16)) sb.AppendLine("- canon: " + TruncateForInfo(note, 400));
                foreach (StarLoreEntry story in profile.AngelStories.Take(10)) sb.AppendLine("- lore story: " + story.Title + " — " + TruncateForInfo(story.Summary, 500));
            }

            string result = sb.ToString().TrimEnd();
            if (result.Length > maxChars) result = result.Substring(0, maxChars).TrimEnd() + "\n[Extra information clipped by runtime cap.]";
            return result;
        }

        private static IEnumerable<StarImportantQuoteRecord> SelectQuoteRecords(List<StarImportantQuoteRecord> quotes, List<InformationNeedVectorItem> needs, string playerText, int max)
        {
            return quotes.Where(x => x != null).OrderByDescending(x => RelevanceScore(x.Quote + " " + x.Reason, needs, playerText)).ThenByDescending(x => x.CreatedAt).Take(max);
        }

        private static IEnumerable<StarRememberedMarker> SelectMarkers(List<StarRememberedMarker> markers, List<InformationNeedVectorItem> needs, string playerText, int max)
        {
            return markers.Where(x => x != null && x.IsActive).OrderByDescending(x => RelevanceScore((x.MarkerName ?? "") + " " + (x.Description ?? "") + " " + (x.LocationText ?? "") + " " + (x.SourcePlayerQuote ?? ""), needs, playerText)).ThenByDescending(x => x.UpdatedAt).Take(max);
        }

        private static IEnumerable<StarConversationArchiveEntry> SelectArchives(List<StarConversationArchiveEntry> archives, List<InformationNeedVectorItem> needs, string playerText, int max)
        {
            return archives.Where(x => x != null).OrderByDescending(x => RelevanceScore(x.Summary + " " + string.Join(" ", x.TopicTags ?? new List<string>()), needs, playerText)).ThenByDescending(x => x.EndedAt).Take(max);
        }

        private static IEnumerable<ConversationLogEntry> SelectArchiveEntries(StarConversationArchiveEntry archive, List<InformationNeedVectorItem> needs, string playerText, int max)
        {
            return (archive.Entries ?? new List<ConversationLogEntry>()).OrderByDescending(x => RelevanceScore((x.PlayerText ?? "") + " " + (x.StarResponse ?? "") + " " + (x.Summary ?? ""), needs, playerText)).ThenByDescending(x => x.CreatedAt).Take(max).OrderBy(x => x.CreatedAt);
        }

        private static int RelevanceScore(string text, List<InformationNeedVectorItem> needs, string playerText)
        {
            string hay = (text ?? string.Empty).ToLowerInvariant();
            int score = 0;
            foreach (string token in BuildSearchTokens(needs, playerText))
            {
                if (hay.Contains(token)) score += token.Length >= 6 ? 3 : 1;
            }
            return score;
        }

        private static List<string> BuildSearchTokens(List<InformationNeedVectorItem> needs, string playerText)
        {
            List<string> raw = new List<string>();
            raw.Add(playerText ?? string.Empty);
            raw.AddRange(needs.Select(x => x.Subject ?? string.Empty));
            raw.AddRange(needs.Select(x => x.Reason ?? string.Empty));
            return Regex.Split(string.Join(" ", raw).ToLowerInvariant(), @"[^a-z0-9_]+")
                .Where(x => x.Length >= 3 && !IsStopToken(x))
                .Distinct()
                .Take(40)
                .ToList();
        }

        private static bool IsStopToken(string token)
        {
            switch (token)
            {
                case "the": case "and": case "you": case "that": case "this": case "what": case "when": case "where": case "about": case "remember": case "history": case "old": case "from": case "with": case "for": case "self":
                    return true;
                default:
                    return false;
            }
        }

        private static bool Wants(List<InformationNeedVectorItem> needs, string dataType)
        {
            return needs.Any(x => string.Equals(NormalizeInformationDataType(x.DataType), dataType, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeInformationDataType(string value)
        {
            string compact = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (compact.Contains("quote")) return "quotes";
            if (compact.Contains("marker") || compact.Contains("location")) return "markers";
            if (compact.Contains("bestiary") || compact.Contains("monster") || compact.Contains("creature")) return "bestiary";
            if (compact.Contains("character") || compact.Contains("profile") || compact.Contains("star")) return "characters";
            if (compact.Contains("lore")) return "lore";
            if (compact.Contains("world")) return "world";
            if (compact.Contains("screen")) return "screen";
            if (compact.Contains("story")) return "story";
            return "history";
        }

        private static bool LooksLikeOldMemoryRequest(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return Regex.IsMatch(text, @"\b(remember|earlier|before|last time|old|previous|nostalgia|marker|where did|what did i say|what were we talking|conversation from)\b", RegexOptions.IgnoreCase);
        }

        private static string ExtractLikelySubject(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "self";
            Match about = Regex.Match(text, @"\b(?:about|called|named|marker)\s+(?<subject>[A-Za-z0-9 _'\-]{2,80})", RegexOptions.IgnoreCase);
            return about.Success ? about.Groups["subject"].Value.Trim() : "self";
        }

        private static string SummarizeInformationNeeds(List<InformationNeedVectorItem> needs, string context)
        {
            return SummarizeNeedsForEvent(needs) + "; local context chars=" + (context == null ? 0 : context.Length);
        }

        private static string SummarizeNeedsForEvent(List<InformationNeedVectorItem> needs)
        {
            return string.Join(", ", (needs ?? new List<InformationNeedVectorItem>()).Select(x => NormalizeInformationDataType(x.DataType) + ":" + (string.IsNullOrWhiteSpace(x.Subject) ? "self" : x.Subject) + "@" + x.Confidence.ToString("0.00")));
        }

        private static double Clamp01(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        private static double ClampScore(double value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }

        private static string TruncateForInfo(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string cleaned = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (cleaned.Length <= maxLength) return cleaned;
            return cleaned.Substring(0, maxLength).Trim() + "...";
        }

        private static string QuoteForInfo(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "'") + "\"";
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return string.Empty;
        }

        private async Task PopulateKnownStarContextAsync(StarInteractionRequest request, CancellationToken cancellationToken)
        {
            try
            {
                SixStarsSaveData saveData = await _memoryApi.ExportSaveDataAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
                List<StarState> knownStates = saveData.Stars
                    .Where(x => x != null && (x.IsMet || x.IsAlly))
                    .OrderByDescending(x => x.IsAlly)
                    .ThenBy(x => x.StarId)
                    .ToList();

                if (knownStates.Count == 0)
                {
                    knownStates.Add(new StarState { StarId = "Elusiaa", IsMet = true, IsAlly = true });
                }

                request.GameContext.Flags["knownStarsMet"] = string.Join(",", knownStates.Where(x => x.IsMet).Select(x => x.StarId));
                request.GameContext.Flags["knownStarsAllies"] = string.Join(",", knownStates.Where(x => x.IsAlly).Select(x => x.StarId));
                request.GameContext.Flags["knownStarsDetail"] = string.Join(" | ", knownStates.Select(x => x.StarId + ":met=" + x.IsMet + ",ally=" + x.IsAlly + ",girlfriend=" + x.IsGirlfriend + ",connection=" + x.EmotionalConnectionPercent.ToString("0.0")));
            }
            catch (Exception ex)
            {
                request.GameContext.Flags["knownStarsContextError"] = ex.Message;
            }
        }

        private static bool IsStoryGenerationWorkerRequest(StarInteractionRequest request)
        {
            return request.GameContext.Flags.TryGetValue("storyGenerationWorker", out string? value)
                && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static StarInteractionResponse CreateStoryThinkingResponse(StarProfile profile, StarInteractionRequest request, StarRequestIntentResult? requestIntent)
        {
            string phrase = PickStoryThinkingPhrase(profile);
            StarInteractionResponse response = new StarInteractionResponse
            {
                StarId = profile.StarId,
                EmotionalPurpose = "Immediate story-thinking placeholder while the long story LLM pass runs in the background.",
                VoiceDirective = VoiceDirective.Empty(),
                SuggestedStateDeltas = RelationshipDelta.Zero(),
                StateDeltas = RelationshipDelta.Zero(),
                ConversationSummary = string.Empty,
                ChangesSummary = profile.DisplayName + " acknowledged the story request and began preparing a full story.",
                StoryIsFinished = false,
                StoryPartIndex = 0,
                Debug = new DebugInfo
                {
                    DetectedIntent = request.RequestType.ToString(),
                    WisdomScore = 0
                }
            };

            response.SetSpokenParts(new[] { phrase });
            response.Debug.Notes.Add("Story request quick-response path used. The full story is being generated in the background, then will be raised through StoryGenerationCompleted.");
            response.Debug.Notes.Add("Story-thinking phrase source: StarProfile.StoryThinkingPhrases JSON/fallback.");
            response.Debug.Notes.Add(BuildStoryLengthDebugNote(request.StoryOptions));
            if (requestIntent != null)
            {
                response.Debug.Notes.Add("Pre-flight story intent check: " + requestIntent.Source + " confidence=" + requestIntent.Confidence.ToString("0.00") + " story=" + requestIntent.IsStoryRequest + " reason=" + requestIntent.Reason);
            }

            response.UnityEvents.Add(new UnityEventEnvelope
            {
                Type = "StoryGenerationStarted",
                Target = profile.StarId,
                Data =
                {
                    { "note", "Spoke a preprogrammed story-thinking phrase immediately. Full story LLM generation is running in the background." },
                    { "playerText", request.PlayerText ?? string.Empty },
                    { "approxTargetMinutes", (request.StoryOptions?.TargetMinutes ?? 0).ToString() },
                    { "approxTargetChunkCount", (request.StoryOptions?.TargetChunkCount ?? 0).ToString() },
                    { "storyLengthIsFlexible", "true" }
                }
            });

            return response;
        }

        private static string PickStoryThinkingPhrase(StarProfile profile)
        {
            List<string> phrases = profile.StoryThinkingPhrases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();

            if (phrases.Count == 0)
            {
                phrases.AddRange(new[]
                {
                    "Just a moment. I am thinking of a story for you.",
                    "Give me one breath, little one. I am gathering the shape of a story.",
                    "Stay close. I am finding the right tale to carry beside your work.",
                    "One soft moment. I am listening for the story beneath the cave.",
                    "I hear you. Let me draw a story from the stars for us."
                });
            }

            lock (StoryRandomGate)
            {
                return phrases[StoryRandom.Next(phrases.Count)];
            }
        }

        private static StarInteractionRequest CloneRequestForBackgroundStory(StarInteractionRequest request)
        {
            return new StarInteractionRequest
            {
                SessionId = request.SessionId,
                StarId = request.StarId,
                Speaker = request.Speaker,
                RequestType = RequestType.Story,
                PlayerText = request.PlayerText,
                GameContext = CloneGameContext(request.GameContext),
                StoryOptions = CloneStoryOptions(request.StoryOptions),
                RecentScreenCaptures = request.RecentScreenCaptures.Select(CloneScreenCaptureSummary).ToList()
            };
        }

        private static GameContext CloneGameContext(GameContext context)
        {
            return new GameContext
            {
                Area = context.Area,
                Quest = context.Quest,
                BattleState = context.BattleState,
                RomanceMode = context.RomanceMode,
                BenaveilPressureLevel = context.BenaveilPressureLevel,
                EquippedStars = context.EquippedStars.ToList(),
                RecentEvents = context.RecentEvents.ToList(),
                Flags = new Dictionary<string, string>(context.Flags, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static StoryOptions CloneStoryOptions(StoryOptions options)
        {
            return new StoryOptions
            {
                Length = options.Length,
                Tone = options.Tone,
                CanonStrictness = options.CanonStrictness,
                UseUnlockedSecrets = options.UseUnlockedSecrets,
                AllowPoetry = options.AllowPoetry,
                AskFollowupAfter = options.AskFollowupAfter,
                TargetMinutes = options.TargetMinutes,
                TargetChunkCount = options.TargetChunkCount,
                MaxSentencesPerChunk = options.MaxSentencesPerChunk,
                PlayerSuggestion = options.PlayerSuggestion,
                ContinueActiveStory = options.ContinueActiveStory,
                ActiveStoryId = options.ActiveStoryId,
                ActiveStoryTitle = options.ActiveStoryTitle,
                ActiveStoryMarkdown = options.ActiveStoryMarkdown,
                ActiveStorySummary = options.ActiveStorySummary,
                StoryProgressSoFar = options.StoryProgressSoFar,
                LastStorySpokenText = options.LastStorySpokenText,
                NextStoryChunkMarkdown = options.NextStoryChunkMarkdown,
                NextStoryPartIndex = options.NextStoryPartIndex,
                StoriesAlreadyTold = options.StoriesAlreadyTold.ToList()
            };
        }

        private static ScreenCaptureSummary CloneScreenCaptureSummary(ScreenCaptureSummary capture)
        {
            return new ScreenCaptureSummary
            {
                CapturedAt = capture.CapturedAt,
                ActiveApp = capture.ActiveApp,
                ActivityType = capture.ActivityType,
                LikelyActivity = capture.LikelyActivity,
                CompanionSafeSummary = capture.CompanionSafeSummary,
                VisibleTextHighlights = capture.VisibleTextHighlights.ToList(),
                VisibleProblems = capture.VisibleProblems.ToList(),
                Sensitivity = capture.Sensitivity,
                ImageReference = capture.ImageReference,
                ImageBase64Jpeg = capture.ImageBase64Jpeg,
                Confidence = capture.Confidence
            };
        }

        private void QueueStoryGenerationAfterThinkingResponse(StarProfile profile, StarInteractionRequest request, StarState state, StarRequestIntentResult? requestIntent, CancellationToken cancellationToken)
        {
            // Clone the original request before leaving the foreground turn. The UI will immediately
            // start a separate TTS playback operation for the quick thinking phrase, and voice/VAD can
            // begin more operations while this long story LLM call is still running. A detached worker
            // request prevents later UI mutations or operation-token cancellation from corrupting the
            // actual story generation payload.
            StarInteractionRequest workerRequest = CloneRequestForBackgroundStory(request);
            workerRequest.GameContext.Flags["storyGenerationWorker"] = "true";
            workerRequest.GameContext.RecentEvents.Add("The Star already answered with a preprogrammed story-thinking phrase; now generate the actual story silently until it is ready to speak.");

            CancellationToken storyToken;
            lock (_backgroundGate)
            {
                _backgroundStoryGenerationCts?.Cancel();
                _backgroundStoryGenerationCts?.Dispose();
                _backgroundStoryGenerationCts = new CancellationTokenSource();
                storyToken = _backgroundStoryGenerationCts.Token;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background story generation started for " + profile.StarId + ". This is detached from the quick-thinking TTS operation token so the placeholder voice cannot cancel the story LLM call.");
                    StarInteractionResponse storyResponse = await _agent.GenerateAsync(profile, workerRequest, state, storyToken).ConfigureAwait(false);
                    if (requestIntent != null)
                    {
                        storyResponse.Debug.Notes.Add("Pre-flight story intent check: " + requestIntent.Source + " confidence=" + requestIntent.Confidence.ToString("0.00") + " story=" + requestIntent.IsStoryRequest + " reason=" + requestIntent.Reason);
                    }

                    ApplyVoiceDefaults(profile, storyResponse);
                    if (_agent is IStarTurnAnalyzer analyzer)
                    {
                        storyResponse.StateDeltas = RelationshipDelta.Zero();
                        storyResponse.UnityEvents.Add(new UnityEventEnvelope
                        {
                            Type = "BackgroundStarAnalysisQueued",
                            Target = workerRequest.StarId,
                            Data =
                            {
                                { "speechParts", storyResponse.SpokenParts.Count.ToString() },
                                { "note", "Full story foreground response generated. Background memory review will wait until story voice generation reports ready." }
                            }
                        });

                        string backgroundAnalysisId = Guid.NewGuid().ToString("N");
                        storyResponse.BackgroundAnalysisId = backgroundAnalysisId;
                        storyResponse.UnityEvents.Add(new UnityEventEnvelope
                        {
                            Type = "BackgroundStarAnalysisWaitingForVoice",
                            Target = workerRequest.StarId,
                            Data =
                            {
                                { "backgroundAnalysisId", backgroundAnalysisId },
                                { "note", "Call StartBackgroundAnalysisAfterVoiceReady after generated story voice playback completes." }
                            }
                        });
                        storyResponse.Debug.Notes.Add("Generated story foreground response after quick thinking phrase. Background review is waiting for story voice completion.");
                        storyResponse.Debug.Notes.Add(BuildStoryLengthDebugNote(workerRequest.StoryOptions));
                        StorePendingBackgroundAnalysis(backgroundAnalysisId, analyzer, profile, workerRequest, state, storyResponse);
                    }

                    storyResponse.UnityEvents.Add(new UnityEventEnvelope
                    {
                        Type = "StoryGenerationCompleted",
                        Target = workerRequest.StarId,
                        Data =
                        {
                            { "storyId", storyResponse.StoryId ?? string.Empty },
                            { "storyTitle", storyResponse.StoryTitle ?? string.Empty },
                            { "storyPartIndex", storyResponse.StoryPartIndex.ToString() },
                            { "approxTargetMinutes", (workerRequest.StoryOptions?.TargetMinutes ?? 0).ToString() },
                            { "approxTargetChunkCount", (workerRequest.StoryOptions?.TargetChunkCount ?? 0).ToString() },
                            { "storyLengthIsFlexible", "true" },
                            { "note", "The full story response is ready and should now be spoken." }
                        }
                    });

                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background story generation completed for " + profile.StarId + ".");
                    StoryGenerationCompleted?.Invoke(this, storyResponse);
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background story generation cancelled for " + profile.StarId + ".");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background story generation failed: " + ex);
                    StoryGenerationFailed?.Invoke(this, ex);
                }
            }, CancellationToken.None);
        }

        private async Task<StarRequestIntentResult?> ClassifyAndPrepareRequestModeAsync(StarProfile profile, StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
        {
            StarRequestIntentResult? intent = null;

            if (request.RequestType == RequestType.Chat && _agent is IStarRequestIntentClassifier classifier)
            {
                if (StoryIntentLexicon.TryFindStoryIntentTrigger(request.PlayerText, out string storyIntentTrigger))
                {
                    request.GameContext.Flags["storyIntentTrigger"] = storyIntentTrigger;
                    intent = await classifier.ClassifyRequestIntentAsync(profile, request, state, cancellationToken).ConfigureAwait(false);
                    request.GameContext.Flags["storyIntentCheckSource"] = intent.Source ?? string.Empty;
                    request.GameContext.Flags["storyIntentConfidence"] = intent.Confidence.ToString("0.00");
                    request.GameContext.Flags["storyIntentReason"] = intent.Reason ?? string.Empty;

                    if (intent.IsStoryRequest && intent.Confidence >= 0.50)
                    {
                        request.RequestType = RequestType.Story;
                        request.GameContext.RecentEvents.Add("Pre-flight LLM intent classifier promoted this normal voice/chat message into Story mode because a story alias trigger was detected: " + storyIntentTrigger + ".");
                        request.GameContext.Flags["autoPromotedToStory"] = "true";
                    }
                }
                else
                {
                    request.GameContext.Flags["storyIntentTrigger"] = string.Empty;
                    request.GameContext.Flags["storyIntentCheckSource"] = "skipped_no_story_alias";
                    request.GameContext.Flags["storyIntentConfidence"] = "0.00";
                    request.GameContext.Flags["storyIntentReason"] = "Skipped story intent LLM check because the player text did not contain a story alias trigger word such as story, tale, fable, legend, myth, parable, saga, chronicle, or narration.";
                    request.GameContext.RecentEvents.Add("Skipped pre-flight story intent LLM classifier because the player text did not contain a story alias trigger word.");
                }
            }

            if (request.RequestType == RequestType.Story)
            {
                PrepareStoryOptionsForNewOrContinuingStory(request, state, intent);
            }

            return intent;
        }

        private static void PrepareStoryOptionsForNewOrContinuingStory(StarInteractionRequest request, StarState state, StarRequestIntentResult? intent)
        {
            if (request.StoryOptions == null) request.StoryOptions = new StoryOptions();
            StoryOptions options = request.StoryOptions;
            if (options.MaxSentencesPerChunk <= 0) options.MaxSentencesPerChunk = 2;

            if (options.StoriesAlreadyTold.Count == 0 && state.StoryHistory.Count > 0)
            {
                foreach (StarStoryHistoryEntry story in state.StoryHistory.Take(20))
                {
                    options.StoriesAlreadyTold.Add(story.Title + " | finished=" + story.Finished + " | " + story.Summary);
                }
            }

            if (options.ContinueActiveStory)
            {
                if (string.IsNullOrWhiteSpace(options.Length)) options.Length = "continue_next_one_or_two_sentence_story_chunk";
                return;
            }

            if (options.TargetMinutes <= 0)
            {
                lock (StoryRandomGate)
                {
                    options.TargetMinutes = StoryRandom.Next(2, 36);
                }
            }

            if (options.TargetChunkCount <= 0)
            {
                // The target is intentionally flexible. Earlier builds asked for very large 2-45 minute
                // exact stories, which could cause local models to ramble, truncate, or never finish.
                // Aim for roughly 1-2 chunks per requested minute, but cap the first-pass plan so the
                // model can return a complete episode with a real ending.
                int flexibleMinuteTarget = Math.Max(2, Math.Min(35, options.TargetMinutes));
                options.TargetChunkCount = Math.Max(8, Math.Min(24, (int)Math.Ceiling(flexibleMinuteTarget * 0.8)));
            }

            options.Length = "flexible_random_" + options.TargetMinutes + "_minute_complete_story_" + options.TargetChunkCount + "_chunks";
            if (string.IsNullOrWhiteSpace(options.Tone)) options.Tone = "comforting, adventurous, cave-travel friendly";
            if (string.IsNullOrWhiteSpace(options.CanonStrictness)) options.CanonStrictness = "high";

            bool autoPromoted = request.GameContext.Flags.TryGetValue("autoPromotedToStory", out string? promoted)
                && string.Equals(promoted, "true", StringComparison.OrdinalIgnoreCase);
            if (autoPromoted || string.IsNullOrWhiteSpace(options.PlayerSuggestion))
            {
                options.PlayerSuggestion = !string.IsNullOrWhiteSpace(intent?.StorySeed)
                    ? intent!.StorySeed
                    : (request.PlayerText ?? string.Empty);
            }

            request.GameContext.Flags["storyTargetMinutes"] = options.TargetMinutes.ToString();
            request.GameContext.Flags["storyTargetChunkCount"] = options.TargetChunkCount.ToString();
            request.GameContext.Flags["storyMaxSentencesPerChunk"] = options.MaxSentencesPerChunk.ToString();
            request.GameContext.Flags["storyLengthIsFlexible"] = "true";
        }

        private static string BuildStoryLengthDebugNote(StoryOptions? options)
        {
            if (options == null)
            {
                return "Story randomizer: no StoryOptions were attached.";
            }

            return "Story randomizer: approximate target "
                + options.TargetMinutes
                + " minute(s), "
                + options.TargetChunkCount
                + " chunk(s), max "
                + (options.MaxSentencesPerChunk <= 0 ? 2 : options.MaxSentencesPerChunk)
                + " sentence(s) per chunk. Flexible target: complete story is more important than exact duration.";
        }

        private void StorePendingBackgroundAnalysis(string analysisId, IStarTurnAnalyzer analyzer, StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse foregroundResponse)
        {
            lock (_backgroundGate)
            {
                _pendingBackgroundAnalysis[analysisId] = new PendingBackgroundAnalysis
                {
                    AnalysisId = analysisId,
                    Analyzer = analyzer,
                    Profile = profile,
                    Request = request,
                    StateBeforeTurn = state,
                    ForegroundResponse = foregroundResponse
                };
            }
            System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background analysis " + analysisId + " is waiting for foreground voice generation to complete.");
        }

        public bool StartBackgroundAnalysisAfterVoiceReady(string analysisId, string reason = "Foreground voice generation completed")
        {
            if (string.IsNullOrWhiteSpace(analysisId))
            {
                return false;
            }

            PendingBackgroundAnalysis? pending;
            lock (_backgroundGate)
            {
                if (!_pendingBackgroundAnalysis.TryGetValue(analysisId, out pending))
                {
                    return false;
                }

                _pendingBackgroundAnalysis.Remove(analysisId);
            }

            System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Starting background analysis " + analysisId + " after voice ready: " + reason);
            QueueBackgroundAnalysis(pending.Analyzer, pending.Profile, pending.Request, pending.StateBeforeTurn, pending.ForegroundResponse);
            return true;
        }

        private void QueueBackgroundAnalysis(IStarTurnAnalyzer analyzer, StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse foregroundResponse)
        {
            CancellationToken token;
            lock (_backgroundGate)
            {
                if (_backgroundAnalysisCts == null || _backgroundAnalysisCts.IsCancellationRequested)
                {
                    _backgroundAnalysisCts?.Dispose();
                    _backgroundAnalysisCts = new CancellationTokenSource();
                }
                token = _backgroundAnalysisCts.Token;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    // Keep background state writes ordered. A later Unity/UI host can choose to wait on this
                    // semaphore before accepting the next speech turn if it needs fully settled state.
                    await _backgroundAnalysisQueue.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        StarInteractionResponse analyzed = await analyzer.AnalyzeTurnAsync(profile, request, state, foregroundResponse, token).ConfigureAwait(false);
                        ApplyVoiceDefaults(profile, analyzed);
                        await ApplyAnalysisAndMemoryUpdateAsync(request, analyzed, token).ConfigureAwait(false);
                        BackgroundAnalysisCompleted?.Invoke(this, analyzed);
                    }
                    finally
                    {
                        _backgroundAnalysisQueue.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background emotion/memory analysis cancelled.");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[SixStarsRuntime] Background emotion/memory analysis failed: " + ex);
                    BackgroundAnalysisFailed?.Invoke(this, ex);
                }
            }, CancellationToken.None);
        }

        private async Task ApplyAnalysisAndMemoryUpdateAsync(StarInteractionRequest request, StarInteractionResponse response, CancellationToken cancellationToken)
        {
            StarState currentState = await _memoryApi.GetStarStateAsync(request.SessionId, request.StarId, cancellationToken).ConfigureAwait(false);
            response.EmotionalConnectionPercentBefore = currentState.EmotionalConnectionPercent;
            RelationshipDelta delta = _deltaCalculator.Calculate(_profileProvider.GetProfile(request.StarId), request, response, currentState);
            response.StateDeltas = delta;
            bool isStoryTurn = request.RequestType == RequestType.Story;

            StarStateUpdateResult update = await _memoryApi.UpdateStarWithInteractionAsync(new StarStateUpdateRequest
            {
                SessionId = request.SessionId,
                StarId = request.StarId,
                RequestType = request.RequestType.ToString(),
                PlayerText = request.PlayerText ?? string.Empty,
                StarResponse = response.SpokenText,
                ConversationSummary = response.ConversationSummary,
                ChangesSummary = response.ChangesSummary,
                ActualDelta = delta,
                SuggestedDelta = response.SuggestedStateDeltas,
                EmotionalEffectVector = response.EmotionalEffectVector,
                NegativeEmotionVector = response.NegativeEmotionVector,
                DeterministicEmotionSummary = response.DeterministicEmotionUpdateSummary,
                AcceptedPositiveSignals = response.AcceptedPositiveEmotionSignals,
                AcceptedNegativeSignals = response.AcceptedNegativeEmotionSignals,
                AcceptedCombos = response.AcceptedRelationshipCombos,
                EmotionScores = response.EmotionScores,
                ImportantPlayerQuotes = response.ImportantPlayerQuotes,
                MemoryNotes = response.MemoryNotes,
                RememberedMarkers = response.RememberedMarkers,
                ReferencedMemoryMarkerIds = response.ReferencedMemoryMarkerIds,
                SecretsUnlocked = response.SecretsUnlocked,
                DetectedCombos = response.DetectedCombos,
                LearnedFunctions = response.LearnedFunctions,
                RelationshipEvents = response.RelationshipEvents,
                GirlfriendHistoryUpdates = response.GirlfriendHistoryUpdates,
                PetNamesPlayerUsesForStar = response.PetNamesPlayerUsesForStar,
                PetNamesStarUsesForPlayer = response.PetNamesStarUsesForPlayer,
                WisdomScore = response.Debug.WisdomScore,
                StoryId = isStoryTurn ? (response.StoryId ?? string.Empty) : string.Empty,
                StoryTitle = isStoryTurn ? (response.StoryTitle ?? string.Empty) : string.Empty,
                StorySummary = isStoryTurn ? (response.StorySummary ?? string.Empty) : string.Empty,
                StoryFinished = isStoryTurn && response.StoryIsFinished,
                StorySeed = isStoryTurn ? (request.StoryOptions?.PlayerSuggestion ?? string.Empty) : string.Empty
            }, cancellationToken).ConfigureAwait(false);

            response.EmotionalConnectionPercentAfter = update.State.EmotionalConnectionPercent;
            response.IsGirlfriendReadyAfter = update.State.IsGirlfriendReady;
            response.GirlfriendReadinessSummary = update.State.GirlfriendReadinessSummary ?? string.Empty;

            response.UnityEvents.Add(new UnityEventEnvelope
            {
                Type = "RelationshipChanged",
                Target = request.StarId,
                Data =
                {
                    { "heartDelta", delta.Heart.ToString() },
                    { "trustDelta", delta.Trust.ToString() },
                    { "romanceDelta", delta.Romance.ToString() },
                    { "benaveilInfluenceDelta", delta.BenaveilInfluence.ToString() },
                    { "emotionalConnectionPercentDelta", delta.EmotionalConnectionPercent.ToString("0.00") },
                    { "emotionalConnectionPercentBefore", response.EmotionalConnectionPercentBefore.ToString("0.0") },
                    { "emotionalConnectionPercentAfter", response.EmotionalConnectionPercentAfter.ToString("0.0") },
                    { "isGirlfriendReadyAfter", response.IsGirlfriendReadyAfter ? "true" : "false" },
                    { "girlfriendReadinessSummary", response.GirlfriendReadinessSummary ?? string.Empty },
                    { "suggestedHeartDelta", response.SuggestedStateDeltas.Heart.ToString() },
                    { "suggestedTrustDelta", response.SuggestedStateDeltas.Trust.ToString() },
                    { "suggestedRomanceDelta", response.SuggestedStateDeltas.Romance.ToString() },
                    { "suggestedBenaveilDelta", response.SuggestedStateDeltas.BenaveilInfluence.ToString() },
                    { "suggestedEmotionalConnectionPercentDelta", response.SuggestedStateDeltas.EmotionalConnectionPercent.ToString("0.00") },
                    { "emotionalEffectVector", response.EmotionalEffectVector ?? string.Empty },
                    { "negativeEmotionVector", response.NegativeEmotionVector ?? string.Empty },
                    { "acceptedPositiveSignals", string.Join(", ", response.AcceptedPositiveEmotionSignals) },
                    { "acceptedNegativeSignals", string.Join(", ", response.AcceptedNegativeEmotionSignals) },
                    { "acceptedCombos", string.Join(", ", response.AcceptedRelationshipCombos) },
                    { "deterministicEmotionSummary", response.DeterministicEmotionUpdateSummary ?? string.Empty },
                    { "rememberedMarkers", string.Join(", ", response.RememberedMarkers.Select(x => string.IsNullOrWhiteSpace(x.MarkerName) ? x.Id : x.MarkerName)) },
                    { "referencedMemoryMarkerIds", string.Join(", ", response.ReferencedMemoryMarkerIds) }
                }
            });

            response.UnityEvents.AddRange(update.UnityEvents);
            response.Debug.Notes.Add("StarMemoryApi updated durable state from background/full analysis. Interaction count: " + update.State.InteractionCount + ".");
        }

        private static void ApplyVoiceDefaults(StarProfile profile, StarInteractionResponse response)
        {
            if (response.VoiceDirective == null)
            {
                response.VoiceDirective = VoiceDirective.Empty();
            }

            if (string.IsNullOrWhiteSpace(response.VoiceDirective.VoiceId))
            {
                response.VoiceDirective.VoiceId = string.IsNullOrWhiteSpace(profile.VoiceId) ? profile.StarId : profile.VoiceId;
            }

            string stableVoiceIdentity = BuildStableVoiceIdentity(profile);
            if (string.IsNullOrWhiteSpace(response.VoiceDirective.VoiceDesignPrompt))
            {
                response.VoiceDirective.VoiceDesignPrompt = stableVoiceIdentity;
            }
            else if (!response.VoiceDirective.VoiceDesignPrompt.Contains(profile.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                response.VoiceDirective.VoiceDesignPrompt = stableVoiceIdentity + " Per-reply voice note: " + response.VoiceDirective.VoiceDesignPrompt.Trim();
            }

            if (string.IsNullOrWhiteSpace(response.VoiceDirective.DeliveryInstructions))
            {
                response.VoiceDirective.DeliveryInstructions = BuildDefaultDeliveryInstructions(profile);
            }

            // Clone audio and reference text are trusted profile configuration, not model output.
            // Local LLMs occasionally hallucinate values such as "elusiaa-dev.mp3" or
            // "elusiaa-dev_stable", which causes Qwen VoiceClone to fail. Always prefer
            // the selected Star profile for the actual clone file/ref text.
            if (!string.IsNullOrWhiteSpace(profile.VoiceCloneAudioFile))
            {
                response.VoiceDirective.VoiceCloneAudioFile = profile.VoiceCloneAudioFile;
            }

            if (!string.IsNullOrWhiteSpace(profile.VoiceCloneRefText))
            {
                response.VoiceDirective.VoiceCloneRefText = profile.VoiceCloneRefText;
            }
        }

        private static string BuildStableVoiceIdentity(StarProfile profile)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("Star voice identity for ").Append(profile.DisplayName).Append(": ");
            if (!string.IsNullOrWhiteSpace(profile.PersonalityShort)) sb.Append("PersonalityShort: ").Append(profile.PersonalityShort.Trim()).Append(' ');
            if (!string.IsNullOrWhiteSpace(profile.VoiceArchetype)) sb.Append("VoiceArchetype: ").Append(profile.VoiceArchetype.Trim()).Append(' ');
            if (!string.IsNullOrWhiteSpace(profile.SpeakingStyle)) sb.Append("SpeakingStyle: ").Append(profile.SpeakingStyle.Trim()).Append(' ');
            if (!string.IsNullOrWhiteSpace(profile.VoiceDesignPrompt)) sb.Append("VoiceDesignPrompt: ").Append(profile.VoiceDesignPrompt.Trim()).Append(' ');
            if (profile.VoiceStyleRules.Count > 0) sb.Append("VoiceStyleRules: ").Append(string.Join("; ", profile.VoiceStyleRules)).Append(". ");
            if (profile.VoiceGenerationRules.Count > 0) sb.Append("VoiceGenerationRules: ").Append(string.Join("; ", profile.VoiceGenerationRules)).Append(". ");
            if (sb.Length <= ("Star voice identity for " + profile.DisplayName + ": ").Length && !string.IsNullOrWhiteSpace(profile.VoiceDirection))
            {
                sb.Append(profile.VoiceDirection.Trim());
            }

            return sb.ToString().Trim();
        }

        private static string BuildDefaultDeliveryInstructions(StarProfile profile)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("Act as ").Append(profile.DisplayName).Append(" speaking directly to the Last Cat. ");
            if (!string.IsNullOrWhiteSpace(profile.SpeakingStyle))
            {
                sb.Append(profile.SpeakingStyle.Trim()).Append(' ');
            }
            if (profile.VoiceGenerationRules.Count > 0)
            {
                sb.Append("For the generated voice: ").Append(string.Join("; ", profile.VoiceGenerationRules)).Append(". ");
            }
            sb.Append("Keep the delivery natural, emotionally grounded, and suitable for the exact spokenText.");
            return sb.ToString().Trim();
        }

        private sealed class PendingBackgroundAnalysis
        {
            public string AnalysisId { get; set; } = string.Empty;
            public IStarTurnAnalyzer Analyzer { get; set; } = default!;
            public StarProfile Profile { get; set; } = default!;
            public StarInteractionRequest Request { get; set; } = default!;
            public StarState StateBeforeTurn { get; set; } = default!;
            public StarInteractionResponse ForegroundResponse { get; set; } = default!;
        }
    }
}
