using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Background;
using Wds.SixStars.Api.Stars;
using Wds.SixStars.Api.State;

namespace Wds.SixStars.Api.DailyState
{
    /// <summary>
    /// Maintains a small generated JSON file for each Star's current day/slot.
    /// The file is regenerated at configured local-time windows and injected into normal Star prompts.
    /// </summary>
    public sealed class StarDailyStateService : IDisposable
    {
        private readonly IStarProfileProvider _profileProvider;
        private readonly IStarMemoryApi _memoryApi;
        private readonly IStarDailyStateGenerator _generator;
        private readonly StarDailyStateFileStore _store;
        private readonly DailyStarStateOptions _options;
        private readonly BackgroundAiRequestDispatcher? _dispatcher;
        private readonly Dictionary<string, SemaphoreSlim> _starGates = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pendingBackgroundRefreshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();
        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;

        public event EventHandler<StarDailyStateUpdatedEventArgs>? DailyStateUpdated;
        public event EventHandler<Exception>? DailyStateUpdateFailed;

        public StarDailyStateService(
            IStarProfileProvider profileProvider,
            IStarMemoryApi memoryApi,
            IStarDailyStateGenerator generator,
            StarDailyStateFileStore store,
            DailyStarStateOptions options,
            BackgroundAiRequestDispatcher? dispatcher = null)
        {
            _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
            _memoryApi = memoryApi ?? throw new ArgumentNullException(nameof(memoryApi));
            _generator = generator ?? throw new ArgumentNullException(nameof(generator));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _options = options ?? new DailyStarStateOptions();
            _dispatcher = dispatcher;
            if (_options.RefreshSlots == null || _options.RefreshSlots.Count == 0) _options.RefreshSlots = DailyStarStateOptions.CreateDefaultSlots();
        }

        public void Start(string sessionId)
        {
            if (!_options.Enabled) return;
            lock (_gate)
            {
                if (_loopTask != null && !_loopTask.IsCompleted) return;
                _loopCts = new CancellationTokenSource();
                CancellationToken token = _loopCts.Token;
                _loopTask = Task.Run(() => RunLoopAsync(string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId, token), CancellationToken.None);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                try
                {
                    _loopCts?.Cancel();
                    _loopCts?.Dispose();
                }
                catch
                {
                }
                _loopCts = null;
                _loopTask = null;
                lock (_pendingBackgroundRefreshes)
                {
                    _pendingBackgroundRefreshes.Clear();
                }
            }
        }

        public async Task<StarDailyStateRecord?> EnsureCurrentAsync(string sessionId, string starId, StarState state, string reason, CancellationToken cancellationToken)
        {
            if (!_options.Enabled) return await _store.ReadCurrentAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            DailyStarStateScheduleSlot? slot = GetCurrentSlot(DateTimeOffset.Now);
            StarDailyStateRecord? current = await _store.ReadCurrentAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            if (slot == null)
            {
                return current;
            }

            string localDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (IsCurrentRecordUsableForState(current, localDate, slot.SlotId, state))
            {
                return current;
            }

            return await RefreshAsync(sessionId, starId, state, slot, reason, cancellationToken).ConfigureAwait(false);
        }

        public async Task<StarDailyStateRecord?> ReadCurrentAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            return await _store.ReadCurrentAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Foreground conversations must never wait for a daily-state LLM generation. Return the
        /// newest cached record immediately, even when it is stale, and queue any needed refresh
        /// behind foreground work. The refreshed record will be used by the next turn.
        /// </summary>
        public async Task<StarDailyStateRecord?> ReadCurrentAndQueueRefreshAsync(
            string sessionId,
            string starId,
            StarState state,
            string reason,
            CancellationToken cancellationToken)
        {
            StarDailyStateRecord? current = await _store.ReadCurrentAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
            if (!_options.Enabled) return current;

            DailyStarStateScheduleSlot? slot = GetCurrentSlot(DateTimeOffset.Now);
            if (slot == null) return current;

            string localDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!IsCurrentRecordUsableForState(current, localDate, slot.SlotId, state))
            {
                string refreshReason = string.IsNullOrWhiteSpace(reason) ? "foreground prompt cache refresh" : reason;
                if (_dispatcher == null)
                {
                    // Test/minimal hosts may not provide the shared dispatcher. Preserve the
                    // foreground guarantee anyway by scheduling generation rather than awaiting it.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RefreshAsync(sessionId, starId, state, slot, refreshReason, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            DailyStateUpdateFailed?.Invoke(this, ex);
                        }
                    });
                }
                else
                {
                    await QueueBackgroundRefreshAsync(
                        sessionId,
                        starId,
                        state,
                        slot,
                        refreshReason,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            return current;
        }

        public async Task ClearStarAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            await _store.ClearStarAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
        }

        public async Task TouchSourceStateUpdatedAsync(string sessionId, string starId, StarState state, CancellationToken cancellationToken)
        {
            if (state == null) return;
            state.LastDailyStateSourceUpdateAt = DateTimeOffset.UtcNow;
            if (state.TodayState == null) state.TodayState = new StarDailyStateContext();
            await _memoryApi.SetStarDailyStateContextAsync(sessionId, starId, state.TodayState, cancellationToken).ConfigureAwait(false);
        }

        private async Task RunLoopAsync(string sessionId, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshDueStarsAsync(sessionId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DailyStateUpdateFailed?.Invoke(this, ex);
                }

                int seconds = Math.Max(30, _options.PollSeconds <= 0 ? 180 : _options.PollSeconds);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task RefreshDueStarsAsync(string sessionId, CancellationToken cancellationToken)
        {
            if (!_options.Enabled) return;
            DailyStarStateScheduleSlot? slot = GetCurrentSlot(DateTimeOffset.Now);
            if (slot == null) return;

            IEnumerable<StarProfile> profiles = ResolveProfilesForBackgroundRefresh();
            string localDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (StarProfile profile in profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StarDailyStateRecord? current = await _store.ReadCurrentAsync(sessionId, profile.StarId, cancellationToken).ConfigureAwait(false);
                StarState state = await _memoryApi.GetStarStateAsync(sessionId, profile.StarId, cancellationToken).ConfigureAwait(false);
                if (IsCurrentRecordUsableForState(current, localDate, slot.SlotId, state))
                {
                    continue;
                }

                await QueueBackgroundRefreshAsync(sessionId, profile.StarId, state, slot, "scheduled daily Star state refresh", cancellationToken).ConfigureAwait(false);
            }
        }

        private IEnumerable<StarProfile> ResolveProfilesForBackgroundRefresh()
        {
            List<StarProfile> all = _profileProvider.GetAllProfiles().ToList();
            if (!_options.GenerateForAllStars)
            {
                if (_options.StarIds != null && _options.StarIds.Count > 0)
                {
                    return all.Where(x => _options.StarIds.Any(id => string.Equals(id, x.StarId, StringComparison.OrdinalIgnoreCase))).ToList();
                }
                return Enumerable.Empty<StarProfile>();
            }

            if (_options.StarIds != null && _options.StarIds.Count > 0)
            {
                return all.Where(x => _options.StarIds.Any(id => string.Equals(id, x.StarId, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            return all;
        }


        private async Task QueueBackgroundRefreshAsync(string sessionId, string starId, StarState state, DailyStarStateScheduleSlot slot, string reason, CancellationToken cancellationToken)
        {
            if (_dispatcher == null)
            {
                await RefreshAsync(sessionId, starId, state, slot, reason, cancellationToken).ConfigureAwait(false);
                return;
            }

            string normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId;
            string normalizedStarId = string.IsNullOrWhiteSpace(starId) ? "Elusiaa" : starId;
            string pendingKey = normalizedSessionId + ":" + normalizedStarId + ":" + (slot.SlotId ?? string.Empty);
            lock (_pendingBackgroundRefreshes)
            {
                if (_pendingBackgroundRefreshes.Contains(pendingKey)) return;
                _pendingBackgroundRefreshes.Add(pendingKey);
            }

            _dispatcher.Enqueue(
                "DailyStarState",
                "Refresh daily Star state for " + normalizedStarId + " (" + reason + ")",
                BackgroundAiRequestPriority.Low,
                async workToken =>
                {
                    try
                    {
                        DailyStarStateScheduleSlot currentSlot = GetCurrentSlot(DateTimeOffset.Now) ?? slot;
                        StarState latest = await _memoryApi.GetStarStateAsync(normalizedSessionId, normalizedStarId, workToken).ConfigureAwait(false);
                        await RefreshAsync(normalizedSessionId, normalizedStarId, latest, currentSlot, reason, workToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_pendingBackgroundRefreshes)
                        {
                            _pendingBackgroundRefreshes.Remove(pendingKey);
                        }
                    }
                },
                cancellationToken);
        }

        private async Task<StarDailyStateRecord> RefreshAsync(string sessionId, string starId, StarState state, DailyStarStateScheduleSlot slot, string reason, CancellationToken cancellationToken)
        {
            SemaphoreSlim starGate = GetStarGate(sessionId, starId);
            await starGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Check again after acquiring the per-star gate; another request may have generated it.
                string localDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                StarDailyStateRecord? current = await _store.ReadCurrentAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                if (IsCurrentRecordUsableForState(current, localDate, slot.SlotId, state))
                {
                    return current;
                }

                StarProfile profile = _profileProvider.GetProfile(starId);
                StarDailyStateModelResponse model = await _generator.GenerateDailyStateAsync(new StarDailyStateGenerationRequest
                {
                    SessionId = sessionId,
                    Profile = profile,
                    State = state,
                    LocalDate = localDate,
                    SlotId = slot.SlotId,
                    LocalNow = DateTimeOffset.Now,
                    PreviousRecord = current
                }, cancellationToken).ConfigureAwait(false);

                string json = BuildJson(profile, state, model, sessionId, localDate, slot.SlotId, DateTimeOffset.Now, reason);
                StarDailyStateRecord record = new StarDailyStateRecord
                {
                    SessionId = sessionId,
                    StarId = profile.StarId,
                    LocalDate = localDate,
                    SlotId = slot.SlotId,
                    GeneratedAt = DateTimeOffset.Now,
                    Summary = FirstNonEmpty(model.PromptUseSummary, model.SituationSummary, model.WhatSheIsDoingToday),
                    JsonContent = json
                };

                record = await _store.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                await _memoryApi.SetStarDailyStateContextAsync(sessionId, profile.StarId, ToContext(record, (_options.MaxJsonCharsForPrompt > 0 ? _options.MaxJsonCharsForPrompt : _options.MaxXmlCharsForPrompt)), cancellationToken).ConfigureAwait(false);
                await ClearFreshGirlfriendSpeechHooksAsync(sessionId, profile.StarId, state, cancellationToken).ConfigureAwait(false);
                DailyStateUpdated?.Invoke(this, new StarDailyStateUpdatedEventArgs { Record = record, Generated = true, Reason = reason });
                return record;
            }
            finally
            {
                starGate.Release();
            }
        }

        private async Task ClearFreshGirlfriendSpeechHooksAsync(string sessionId, string starId, StarState state, CancellationToken cancellationToken)
        {
            if (state == null || !state.IsGirlfriend || state.GirlfriendHourlyState == null || !state.GirlfriendHourlyState.IsAvailable) return;

            StarGirlfriendHourlyStateContext current = state.GirlfriendHourlyState;
            bool hasSpeechHooks = (current.ThingsSheWantsToSay != null && current.ThingsSheWantsToSay.Any(x => !string.IsNullOrWhiteSpace(x)))
                || (current.CuteThingsSheWantsToSay != null && current.CuteThingsSheWantsToSay.Any(x => !string.IsNullOrWhiteSpace(x)));
            if (!hasSpeechHooks) return;

            // First re-submit the current state so its hooks are durable before this daily refresh clears the live queue.
            await _memoryApi.SetStarGirlfriendHourlyStateContextAsync(sessionId, starId, current, cancellationToken).ConfigureAwait(false);

            StarGirlfriendHourlyStateContext cleared = new StarGirlfriendHourlyStateContext
            {
                StarId = string.IsNullOrWhiteSpace(current.StarId) ? starId : current.StarId,
                GeneratedAt = current.GeneratedAt,
                SlotId = current.SlotId,
                Summary = current.Summary,
                RelationshipPosture = current.RelationshipPosture,
                NeedSummary = current.NeedSummary,
                WantSummary = current.WantSummary,
                OverallConfidence = current.OverallConfidence,
                OverallScore = current.OverallScore,
                NeedVector = current.NeedVector == null ? new List<GirlfriendVectorItem>() : current.NeedVector.ToList(),
                WantVector = current.WantVector == null ? new List<GirlfriendVectorItem>() : current.WantVector.ToList(),
                ThingsSheWantsToSay = new List<string>(),
                CuteThingsSheWantsToSay = new List<string>()
            };

            await _memoryApi.SetStarGirlfriendHourlyStateContextAsync(sessionId, starId, cleared, cancellationToken).ConfigureAwait(false);
        }

        private SemaphoreSlim GetStarGate(string sessionId, string starId)
        {
            string key = (sessionId ?? string.Empty) + ":" + (starId ?? string.Empty);
            lock (_starGates)
            {
                if (!_starGates.TryGetValue(key, out SemaphoreSlim? gate))
                {
                    gate = new SemaphoreSlim(1, 1);
                    _starGates[key] = gate;
                }
                return gate;
            }
        }

        private DailyStarStateScheduleSlot? GetCurrentSlot(DateTimeOffset now)
        {
            TimeSpan t = now.LocalDateTime.TimeOfDay;
            foreach (DailyStarStateScheduleSlot slot in _options.RefreshSlots ?? DailyStarStateOptions.CreateDefaultSlots())
            {
                if (TryParseTime(slot.StartsAt, out TimeSpan start) && TryParseTime(slot.RefreshBefore, out TimeSpan end))
                {
                    if (start <= end)
                    {
                        if (t >= start && t < end) return slot;
                    }
                    else
                    {
                        if (t >= start || t < end) return slot;
                    }
                }
            }
            return null;
        }

        private static bool TryParseTime(string? value, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (TimeSpan.TryParse(value.Trim(), CultureInfo.InvariantCulture, out result)) return true;
            if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out DateTime dt))
            {
                result = dt.TimeOfDay;
                return true;
            }
            return false;
        }


        private static bool IsCurrentRecordUsableForState(StarDailyStateRecord? current, string localDate, string slotId, StarState state)
        {
            if (current == null || (string.IsNullOrWhiteSpace(current.JsonContent) && string.IsNullOrWhiteSpace(current.XmlContent))) return false;
            if (!string.Equals(current.LocalDate, localDate, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(current.SlotId, slotId, StringComparison.OrdinalIgnoreCase)) return false;

            // A clear/reset or relationship update should invalidate the current-slot daily state even if
            // the date/slot did not change. Otherwise prompts can keep receiving stale relationship
            // numbers from today's generated daily state after the user presses Clear All Star Data.
            if (state != null && state.LastDailyStateSourceUpdateAt.HasValue && current.GeneratedAt < state.LastDailyStateSourceUpdateAt.Value)
            {
                return false;
            }

            return true;
        }

        private static StarDailyStateContext ToContext(StarDailyStateRecord record, int maxChars)
        {
            int max = Math.Max(1000, maxChars <= 0 ? 12000 : maxChars);
            string json = !string.IsNullOrWhiteSpace(record.JsonContent) ? record.JsonContent : (record.XmlContent ?? string.Empty);
            if (json.Length > max) json = json.Substring(0, max) + "\n/* daily-star-state truncated for prompt */";
            return new StarDailyStateContext
            {
                IsAvailable = !string.IsNullOrWhiteSpace(record.JsonContent) || !string.IsNullOrWhiteSpace(record.XmlContent),
                SessionId = record.SessionId,
                StarId = record.StarId,
                LocalDate = record.LocalDate,
                SlotId = record.SlotId,
                GeneratedAt = record.GeneratedAt,
                FilePath = record.FilePath,
                Summary = record.Summary ?? string.Empty,
                Json = json,
                Xml = string.Empty
            };
        }


        private static string BuildJson(StarProfile profile, StarState state, StarDailyStateModelResponse model, string sessionId, string localDate, string slotId, DateTimeOffset generatedAt, string reason)
        {
            var payload = new
            {
                version = 1,
                sessionId = sessionId ?? string.Empty,
                starId = profile.StarId ?? string.Empty,
                starName = profile.DisplayName ?? profile.StarId ?? string.Empty,
                localDate = localDate ?? string.Empty,
                slotId = slotId ?? string.Empty,
                generatedAt = generatedAt.ToString("o"),
                generationReason = reason ?? string.Empty,
                identity = new
                {
                    element = profile.Element ?? string.Empty,
                    domain = profile.Domain ?? string.Empty,
                    coreQuote = profile.CoreQuote ?? string.Empty,
                    personality = profile.PersonalityShort ?? string.Empty
                },
                relationship = new
                {
                    heart = state.Heart,
                    trust = state.Trust,
                    romance = state.Romance,
                    benaveilInfluence = state.BenaveilInfluence,
                    connectionPercent = Math.Round(state.EmotionalConnectionPercent, 1),
                    isMet = state.IsMet,
                    isAlly = state.IsAlly,
                    isGirlfriendReady = state.IsGirlfriendReady,
                    isGirlfriend = state.IsGirlfriend
                },
                currentInnerLife = new
                {
                    situationSummary = model.SituationSummary ?? string.Empty,
                    // Include both canonical runtime names and LLM-friendly aliases so future prompt consumers
                    // cannot lose the rich daily state if a model says feelingsSummary/currentDesires.
                    feelings = model.FeelingsSummary ?? string.Empty,
                    feelingsSummary = model.FeelingsSummary ?? string.Empty,
                    relationshipPosture = model.RelationshipPosture ?? string.Empty,
                    desires = model.CurrentDesires ?? string.Empty,
                    currentDesires = model.CurrentDesires ?? string.Empty,
                    privateSecretsOrHooks = model.PrivateSecretsOrHooks ?? string.Empty
                },
                today = new
                {
                    whatSheIsDoing = model.WhatSheIsDoingToday ?? string.Empty,
                    intentions = (model.TodayIntentions ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(12).ToList(),
                    wantsToTalkAbout = (model.ThingsSheWantsToTalkAbout ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(12).ToList(),
                    moodTags = (model.MoodTags ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(12).ToList(),
                    conversationHooks = (model.ConversationHooks ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(12).ToList()
                },
                recentAwareness = new
                {
                    peopleSeenSummary = model.RecentPeopleSeenSummary ?? string.Empty,
                    peopleSeen = (model.RecentPeopleSeen ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(12).ToList(),
                    eventsSummary = model.RecentEventsSummary ?? string.Empty,
                    events = (model.RecentEvents ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(12).ToList()
                },
                promptUse = new
                {
                    summary = model.PromptUseSummary ?? string.Empty
                }
            };

            return System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        private static string BuildXml(StarProfile profile, StarState state, StarDailyStateModelResponse model, string sessionId, string localDate, string slotId, DateTimeOffset generatedAt, string reason)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<starTodayState version=\"1\" sessionId=\"" + Xml(sessionId) + "\" starId=\"" + Xml(profile.StarId) + "\" starName=\"" + Xml(profile.DisplayName) + "\" localDate=\"" + Xml(localDate) + "\" slotId=\"" + Xml(slotId) + "\" generatedAt=\"" + Xml(generatedAt.ToString("o")) + "\">");
            Element(sb, 1, "generationReason", reason);
            sb.AppendLine("  <identity element=\"" + Xml(profile.Element) + "\" domain=\"" + Xml(profile.Domain) + "\">");
            Element(sb, 2, "coreQuote", profile.CoreQuote);
            Element(sb, 2, "personality", profile.PersonalityShort);
            sb.AppendLine("  </identity>");
            sb.AppendLine("  <relationship heart=\"" + state.Heart + "\" trust=\"" + state.Trust + "\" romance=\"" + state.Romance + "\" benaveilInfluence=\"" + state.BenaveilInfluence + "\" connectionPercent=\"" + state.EmotionalConnectionPercent.ToString("0.0", CultureInfo.InvariantCulture) + "\" isMet=\"" + state.IsMet.ToString().ToLowerInvariant() + "\" isAlly=\"" + state.IsAlly.ToString().ToLowerInvariant() + "\" isGirlfriendReady=\"" + state.IsGirlfriendReady.ToString().ToLowerInvariant() + "\" />");
            sb.AppendLine("  <currentInnerLife>");
            Element(sb, 2, "situationSummary", model.SituationSummary);
            Element(sb, 2, "feelings", model.FeelingsSummary);
            Element(sb, 2, "relationshipPosture", model.RelationshipPosture);
            Element(sb, 2, "desires", model.CurrentDesires);
            Element(sb, 2, "privateSecretsOrHooks", model.PrivateSecretsOrHooks);
            sb.AppendLine("  </currentInnerLife>");
            sb.AppendLine("  <today>");
            Element(sb, 2, "whatSheIsDoing", model.WhatSheIsDoingToday);
            ListElement(sb, 2, "intentions", "intention", model.TodayIntentions);
            ListElement(sb, 2, "moodTags", "tag", model.MoodTags);
            ListElement(sb, 2, "conversationHooks", "hook", model.ConversationHooks);
            sb.AppendLine("  </today>");
            sb.AppendLine("  <recentAwareness>");
            Element(sb, 2, "peopleSeenSummary", model.RecentPeopleSeenSummary);
            ListElement(sb, 2, "peopleSeen", "person", model.RecentPeopleSeen);
            Element(sb, 2, "eventsSummary", model.RecentEventsSummary);
            ListElement(sb, 2, "events", "event", model.RecentEvents);
            sb.AppendLine("  </recentAwareness>");
            sb.AppendLine("  <promptUse>");
            Element(sb, 2, "summary", model.PromptUseSummary);
            sb.AppendLine("  </promptUse>");
            sb.AppendLine("</starTodayState>");
            return sb.ToString();
        }

        private static void Element(StringBuilder sb, int indent, string name, string? value)
        {
            string pad = new string(' ', indent * 2);
            sb.Append(pad).Append('<').Append(name).Append('>')
                .Append(Xml(value ?? string.Empty))
                .Append("</").Append(name).AppendLine(">");
        }

        private static void ListElement(StringBuilder sb, int indent, string outerName, string itemName, IEnumerable<string>? values)
        {
            string pad = new string(' ', indent * 2);
            sb.Append(pad).Append('<').Append(outerName).AppendLine(">");
            foreach (string value in (values ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(12))
            {
                Element(sb, indent + 1, itemName, value.Trim());
            }
            sb.Append(pad).Append("</").Append(outerName).AppendLine(">");
        }

        private static string Xml(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return string.Empty;
        }

        public void Dispose()
        {
            Stop();
            lock (_starGates)
            {
                foreach (SemaphoreSlim gate in _starGates.Values) gate.Dispose();
                _starGates.Clear();
            }
        }
    }
}
