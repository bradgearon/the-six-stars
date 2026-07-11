using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.GirlfriendState;
using Wds.SixStars.Api.DailyState;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Ai
{
    public sealed class StructuredStarAgent : IStarAgent, IStarTurnAnalyzer, IStarInformationNeedAnalyzer, IStarDailyStateGenerator, IStarGirlfriendHourlyStateGenerator, IInterstarUpdateAgent
    {
        private readonly ILocalChatModelClient _client;
        private readonly StarPromptBuilder _promptBuilder;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly int _speechMaxTokens;
        private readonly int _initiativeMaxTokens;
        private readonly int _storyPlanMaxTokens;
        private readonly int _reviewMaxTokens;

        public StructuredStarAgent(ILocalChatModelClient client, MainInstructionSet mainInstructions)
            : this(client, mainInstructions, new StarPromptOptions())
        {
        }

        public StructuredStarAgent(ILocalChatModelClient client, MainInstructionSet mainInstructions, StarPromptOptions promptOptions)
            : this(client, mainInstructions, promptOptions, 0)
        {
        }

        public StructuredStarAgent(ILocalChatModelClient client, MainInstructionSet mainInstructions, StarPromptOptions promptOptions, int speechMaxTokens, int reviewMaxTokens = 0)
        {
            _client = client;
            _promptBuilder = new StarPromptBuilder(mainInstructions, promptOptions);
            _speechMaxTokens = ResolveSpeechMaxTokens(speechMaxTokens);
            _initiativeMaxTokens = ResolveInitiativeMaxTokens();
            _storyPlanMaxTokens = ResolveStoryPlanMaxTokens();
            _reviewMaxTokens = ResolveReviewMaxTokens(reviewMaxTokens);
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
            _jsonOptions.Converters.Add(new FlexibleStringJsonConverter());
            _jsonOptions.Converters.Add(new RelationshipDeltaJsonConverter());
            _jsonOptions.Converters.Add(new LenientStoryKindJsonConverter());
            _jsonOptions.Converters.Add(new JsonStringEnumConverter());
            _jsonOptions.Converters.Add(new FlexibleListJsonConverterFactory());
        }


        public async Task<InformationNeedSelectionResult> AnalyzeInformationNeedAsync(
            StarProfile profile,
            StarInteractionRequest request,
            StarState state,
            StarInteractionResponse foregroundResponse,
            IReadOnlyList<InformationNeedVectorItem> foregroundVector,
            IReadOnlyList<InformationNeedAvailabilityItem> availableData,
            CancellationToken cancellationToken)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[StructuredStarAgent] Sending QUICK information-need selector request...");
                string json = await _client.CompleteJsonAsync(
                    _promptBuilder.BuildInformationNeedSelectorMessages(profile, request, state, foregroundResponse, foregroundVector, availableData),
                    StarResponseJsonSchema.CreateInformationNeedSelectionResponseFormat(),
                    0,
                    "information-need-selector",
                    cancellationToken).ConfigureAwait(false);

                InformationNeedSelectionResult? model = JsonSerializer.Deserialize<InformationNeedSelectionResult>(ExtractJson(json), _jsonOptions);
                if (model == null) throw new InvalidOperationException("Information-need selector returned empty JSON.");
                model.Source = "llm";
                model.RankedNeeds = (model.RankedNeeds ?? new List<InformationNeedVectorItem>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.DataType))
                    .Select(x => new InformationNeedVectorItem
                    {
                        DataType = x.DataType,
                        Subject = string.IsNullOrWhiteSpace(x.Subject) ? "self" : x.Subject.Trim(),
                        Confidence = Clamp01(x.Confidence),
                        Score = ClampScore(x.Score),
                        Reason = x.Reason ?? string.Empty
                    })
                    .Take(9)
                    .ToList();
                return model;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                InformationNeedVectorItem? best = foregroundVector
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.DataType))
                    .OrderByDescending(x => Math.Max(x.Score / 100.0, x.Confidence))
                    .FirstOrDefault();
                return new InformationNeedSelectionResult
                {
                    ShouldRequestMoreInformation = best != null && (best.Confidence >= 0.55 || best.Score >= 55),
                    SelectedDataType = best?.DataType ?? "history",
                    Subject = string.IsNullOrWhiteSpace(best?.Subject) ? "self" : best!.Subject,
                    Confidence = best?.Confidence ?? 0,
                    Score = best?.Score ?? 0,
                    Reason = "Information-need selector LLM failed; foreground-vector fallback used: " + ex.Message,
                    Source = "heuristic-fallback",
                    RankedNeeds = foregroundVector == null ? new List<InformationNeedVectorItem>() : foregroundVector.Take(9).ToList()
                };
            }
        }

        public async Task<StarDailyStateModelResponse> GenerateDailyStateAsync(StarDailyStateGenerationRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            StarProfile profile = request.Profile ?? throw new ArgumentNullException(nameof(request.Profile));
            StarState state = request.State ?? new StarState { StarId = profile.StarId };

            System.Diagnostics.Debug.WriteLine("[StructuredStarAgent] Sending DAILY Star state refresh request for " + profile.StarId + " slot " + request.SlotId + "...");
            string json = await _client.CompleteJsonAsync(
                _promptBuilder.BuildDailyStarStateMessages(request),
                StarResponseJsonSchema.CreateDailyStarStateResponseFormat(),
                _reviewMaxTokens,
                "daily-star-state-refresh",
                cancellationToken).ConfigureAwait(false);

            string extractedJson = ExtractJson(json);
            StarDailyStateModelResponse? model = JsonSerializer.Deserialize<StarDailyStateModelResponse>(extractedJson, _jsonOptions);
            if (model == null) throw new InvalidOperationException("Daily Star state generator returned empty JSON.");
            ApplyDailyStateJsonAliases(extractedJson, model);
            model.StarId = string.IsNullOrWhiteSpace(model.StarId) ? profile.StarId : model.StarId;
            model.SituationSummary = CleanOneLine(model.SituationSummary, 1100);
            model.FeelingsSummary = CleanOneLine(model.FeelingsSummary, 900);
            model.WhatSheIsDoingToday = CleanOneLine(model.WhatSheIsDoingToday, 900);
            model.CurrentDesires = CleanOneLine(model.CurrentDesires, 700);
            model.RelationshipPosture = CleanOneLine(model.RelationshipPosture, 700);
            model.RecentPeopleSeenSummary = CleanOneLine(model.RecentPeopleSeenSummary, 700);
            model.RecentEventsSummary = CleanOneLine(model.RecentEventsSummary, 900);
            model.PrivateSecretsOrHooks = CleanOneLine(model.PrivateSecretsOrHooks, 900);
            model.PromptUseSummary = CleanOneLine(model.PromptUseSummary, 600);
            model.MoodTags = CleanList(model.MoodTags, 10);
            model.RecentPeopleSeen = CleanList(model.RecentPeopleSeen, 12);
            model.RecentEvents = CleanList(model.RecentEvents, 12);
            model.TodayIntentions = CleanList(model.TodayIntentions, 12);
            model.ThingsSheWantsToTalkAbout = CleanList(model.ThingsSheWantsToTalkAbout, 12);
            model.ConversationHooks = CleanList(model.ConversationHooks, 12);
            return model;
        }


        public async Task<StarGirlfriendHourlyStateModelResponse> GenerateGirlfriendHourlyStateAsync(StarGirlfriendHourlyStateGenerationRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            StarProfile profile = request.Profile ?? throw new ArgumentNullException(nameof(request.Profile));

            System.Diagnostics.Debug.WriteLine("[StructuredStarAgent] Sending GIRLFRIEND hourly state refresh request for " + profile.StarId + " slot " + request.SlotId + "...");
            string json = await _client.CompleteJsonAsync(
                _promptBuilder.BuildGirlfriendHourlyStateMessages(request),
                StarResponseJsonSchema.CreateGirlfriendHourlyStateResponseFormat(),
                _reviewMaxTokens,
                "girlfriend-hourly-state-refresh",
                cancellationToken).ConfigureAwait(false);

            StarGirlfriendHourlyStateModelResponse? model = JsonSerializer.Deserialize<StarGirlfriendHourlyStateModelResponse>(ExtractJson(json), _jsonOptions);
            if (model == null) throw new InvalidOperationException("Girlfriend hourly state generator returned empty JSON.");
            model.StarId = string.IsNullOrWhiteSpace(model.StarId) ? profile.StarId : model.StarId;
            model.Summary = CleanOneLine(model.Summary, 900);
            model.RelationshipPosture = CleanOneLine(model.RelationshipPosture, 700);
            model.NeedSummary = CleanOneLine(model.NeedSummary, 700);
            model.WantSummary = CleanOneLine(model.WantSummary, 700);
            model.NeedVector = NormalizeGirlfriendVector(model.NeedVector, 30);
            model.WantVector = NormalizeGirlfriendVector(model.WantVector, 30);
            model.ThingsSheWantsToSay = CleanList(model.ThingsSheWantsToSay, 16);
            model.CuteThingsSheWantsToSay = CleanList(model.CuteThingsSheWantsToSay, 16);
            model.OverallConfidence = Clamp01(model.OverallConfidence);
            model.OverallScore = ClampScore(model.OverallScore);
            return model;
        }


        public async Task<InterstarRelayMessage> GenerateInterstarRelayAsync(
            StarProfile originProfile,
            StarProfile targetProfile,
            StarInteractionRequest originalRequest,
            StarInteractionResponse originResponse,
            StarState originState,
            StarState targetState,
            CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine("[StructuredStarAgent] Sending INTERSTAR relay request from " + originProfile.StarId + " to " + targetProfile.StarId + "...");
            string json = await _client.CompleteJsonAsync(
                _promptBuilder.BuildInterstarRelayMessages(originProfile, targetProfile, originalRequest, originResponse, originState, targetState),
                StarResponseJsonSchema.CreateInterstarRelayResponseFormat(),
                _reviewMaxTokens,
                "interstar-origin-relay",
                cancellationToken).ConfigureAwait(false);

            InterstarRelayMessage? relay = JsonSerializer.Deserialize<InterstarRelayMessage>(ExtractJson(json), _jsonOptions);
            if (relay == null) throw new InvalidOperationException("Interstar relay returned empty JSON.");
            relay.OriginStarId = string.IsNullOrWhiteSpace(relay.OriginStarId) ? originProfile.StarId : relay.OriginStarId;
            relay.TargetStarId = string.IsNullOrWhiteSpace(relay.TargetStarId) ? targetProfile.StarId : relay.TargetStarId;
            relay.Priority = Clamp01(relay.Priority);
            relay.Summary = CleanOneLine(relay.Summary, 360);
            relay.WhatOriginWouldTellTarget = CleanOneLine(relay.WhatOriginWouldTellTarget, 900);
            relay.Reason = CleanOneLine(relay.Reason, 360);
            relay.CooldownKey = CleanOneLine(relay.CooldownKey, 120);
            NormalizeInterstarRelayFields(relay, originProfile, targetProfile, originalRequest, originResponse);
            return relay;
        }

        public async Task<StarInteractionResponse> AnalyzeInterstarReactionAsync(
            StarProfile targetProfile,
            InterstarRelayMessage relay,
            StarInteractionRequest originalRequest,
            StarInteractionResponse originResponse,
            StarState targetState,
            CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine("[StructuredStarAgent] Sending INTERSTAR reaction vector request for " + targetProfile.StarId + " from " + relay.OriginStarId + "...");
            string json = await _client.CompleteJsonAsync(
                _promptBuilder.BuildInterstarReactionMessages(targetProfile, relay, originalRequest, originResponse, targetState),
                StarResponseJsonSchema.CreateInterstarReactionVectorResponseFormat(relay.OriginStarId, targetProfile.StarId, "six_stars_interstar_reaction_vector"),
                _reviewMaxTokens,
                "interstar-target-reaction-vector",
                cancellationToken).ConfigureAwait(false);

            StarStructuredModelResponse model = ParseInterstarVector(json, targetProfile, "interstar reaction vector");
            StarInteractionResponse response = BuildInteractionResponseFromModel(targetProfile, originalRequest, targetState, model);
            EnsureSourceStarVectorsFromInterstarAliases(response, relay.OriginStarId);
            response.SetSpokenParts(Array.Empty<string>());
            response.SpokenText = string.Empty;
            response.EmotionalPurpose = "Interstar micro-update: " + targetProfile.DisplayName + " reacted privately to what " + relay.OriginStarId + " told her about the Last Cat.";
            response.ConversationSummary = BuildInterstarReactionSummary(targetProfile, relay);
            response.ChangesSummary = targetProfile.DisplayName + " privately heard " + relay.OriginStarId + "'s interstar relay about the Last Cat and updated hidden feelings only.";
            response.InterstarReplyToSourceStar = NormalizeInterstarReplyToSource(response.InterstarReplyToSourceStar, relay.OriginStarId);
            response.Debug.Notes.Add("StructuredStarAgent used REAL LLM interstar target reaction vector pass.");
            return response;
        }

        public async Task<StarInteractionResponse> AnalyzeInterstarFeedbackAsync(
            StarProfile originProfile,
            StarInteractionRequest originalRequest,
            StarInteractionResponse originResponse,
            StarState originState,
            IReadOnlyList<InterstarReactionSummary> reactions,
            CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine("[StructuredStarAgent] Sending INTERSTAR feedback vector request for origin " + originProfile.StarId + "...");
            string json = await _client.CompleteJsonAsync(
                _promptBuilder.BuildInterstarFeedbackMessages(originProfile, originalRequest, originResponse, originState, reactions),
                StarResponseJsonSchema.CreateInterstarFeedbackVectorResponseFormat((reactions ?? Array.Empty<InterstarReactionSummary>()).Select(x => x.StarId), "six_stars_interstar_origin_feedback_vector"),
                _reviewMaxTokens,
                "interstar-origin-feedback-vector",
                cancellationToken).ConfigureAwait(false);

            StarStructuredModelResponse model = ParseInterstarVector(json, originProfile, "interstar origin feedback vector");
            StarInteractionResponse response = BuildInteractionResponseFromModel(originProfile, originalRequest, originState, model);
            response.SetSpokenParts(Array.Empty<string>());
            response.SpokenText = string.Empty;
            response.EmotionalPurpose = "Interstar micro-update: " + originProfile.DisplayName + " reacted privately to the other Stars' emotional responses about the Last Cat.";
            response.ConversationSummary = BuildInterstarFeedbackSummary(originProfile, reactions);
            response.ChangesSummary = originProfile.DisplayName + " privately processed interstar feedback about the Last Cat; hidden state only.";
            response.Debug.Notes.Add("StructuredStarAgent used REAL LLM interstar origin feedback vector pass.");
            return response;
        }

        public async Task<StarInteractionResponse> GenerateAsync(StarProfile profile, StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine($"[StructuredStarAgent] Sending FAST spoken response request to LLM for {request.StarId}...");
            System.Diagnostics.Debug.WriteLine($"[StructuredStarAgent] Player text: {request.PlayerText?.Substring(0, Math.Min(60, request.PlayerText?.Length ?? 0))}...");

            bool isStory = request.RequestType == RequestType.Story;
            bool isStarInitiative = request.RequestType == RequestType.StarInitiative;
            bool isStoryContinuation = isStory && request.StoryOptions != null && request.StoryOptions.ContinueActiveStory;
            object responseFormat = isStory
                ? StarResponseJsonSchema.CreateStorySpeechResponseFormat(isStoryContinuation)
                : (isStarInitiative ? StarResponseJsonSchema.CreateStarInitiativeResponseFormat() : StarResponseJsonSchema.CreateSpeechResponseFormat());
            int maxTokens = isStory && !isStoryContinuation
                ? _storyPlanMaxTokens
                : (isStarInitiative ? _initiativeMaxTokens : _speechMaxTokens);
            string purpose = isStory
                ? (isStoryContinuation ? "story-continuation-spoken-response" : "story-plan-and-first-spoken-response")
                : (isStarInitiative ? "star-initiative-check-response" : "fast-spoken-response");

            string json = await _client.CompleteJsonAsync(
                _promptBuilder.BuildSpeechMessages(profile, request, state),
                responseFormat,
                maxTokens,
                purpose,
                cancellationToken).ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine($"[StructuredStarAgent] Received fast LLM speech response ({json.Length} chars)");

            StarSpeechModelResponse model = ParseSpeech(json, profile, request);
            List<GirlfriendVectorItem> initiativeSpeechDecisionVector = NormalizeGirlfriendVector(model.SpeechDecisionVector, 1);
            bool initiativeVectorWantsSpeech = initiativeSpeechDecisionVector.Any(x => x != null && (x.Score > 0 || x.Confidence > 0));
            StarInteractionResponse response = new StarInteractionResponse
            {
                StarId = string.IsNullOrWhiteSpace(model.StarId) ? profile.StarId : model.StarId,
                EmotionalPurpose = isStarInitiative
                    ? "At-rest Star initiative check; no player emotional scoring or relationship update should run."
                    : "Foreground spoken reply only; background emotion/vector/memory review pending.",
                InitiativeShouldSpeak = !isStarInitiative || model.HasSomethingToSay || model.ShouldSpeak || initiativeVectorWantsSpeech,
                InitiativePriority = Clamp01(model.Priority),
                InitiativeSpeechDecisionVector = initiativeSpeechDecisionVector,
                InitiativeTopic = model.SelectedTopic ?? string.Empty,
                InitiativeReason = model.Reason ?? string.Empty,
                EmotionalEffectVector = string.Empty,
                NegativeEmotionVector = string.Empty,
                VoiceDirective = BuildDefaultVoiceDirective(profile, request),
                SuggestedStateDeltas = RelationshipDelta.Zero(),
                ConversationSummary = BuildFallbackConversationSummary(state, request, model.SpokenText),
                ChangesSummary = BuildFallbackChangesSummary(request, model.SpokenText),
                StoryTitle = string.IsNullOrWhiteSpace(model.StoryTitle) && request.StoryOptions != null ? request.StoryOptions.ActiveStoryTitle : model.StoryTitle,
                StoryId = string.IsNullOrWhiteSpace(model.StoryId) && request.StoryOptions != null ? request.StoryOptions.ActiveStoryId : model.StoryId ?? string.Empty,
                StoryMarkdown = string.IsNullOrWhiteSpace(model.StoryMarkdown) && request.StoryOptions != null ? request.StoryOptions.ActiveStoryMarkdown : model.StoryMarkdown ?? string.Empty,
                StorySummary = string.IsNullOrWhiteSpace(model.StorySummary) && request.StoryOptions != null ? request.StoryOptions.ActiveStorySummary : model.StorySummary ?? string.Empty,
                StoryIsFinished = request.RequestType == RequestType.Story ? model.StoryIsFinished : true,
                StoryPartIndex = request.StoryOptions?.NextStoryPartIndex ?? model.StoryPartIndex,
                NeedsMoreInformation = model.NeedsMoreInformation,
                InformationNeeds = model.InformationNeeds ?? new List<InformationNeedVectorItem>(),
                Debug = new DebugInfo
                {
                    DetectedIntent = request.RequestType.ToString(),
                    WisdomScore = 0
                }
            };
            response.SetSpokenParts(model.SpokenParts);
            if (isStarInitiative && !response.InitiativeShouldSpeak)
            {
                response.SetSpokenParts(Array.Empty<string>());
            }
            // Do not ask the foreground LLM pass for heavy emotion objects.
            // It returns only the compact emotionalEffectVector; the background review computes impactful emotion scores and deterministic relationship updates.
            response.Debug.Notes.Add(isStarInitiative
                ? "StructuredStarAgent used REAL LLM at-rest Star initiative check pass."
                : "StructuredStarAgent used REAL LLM fast foreground speech pass.");
            if (isStarInitiative)
            {
                response.Debug.Notes.Add("Star initiative result: shouldSpeak=" + response.InitiativeShouldSpeak + ", priority=" + response.InitiativePriority.ToString("0.00") + ", topic=" + (response.InitiativeTopic ?? string.Empty) + ".");
            }
            response.Debug.Notes.Add("Heavy emotion/memory analysis and compact emotion vectors wait until the voice pipeline reports foreground voice generation ready.");
            response.Debug.Notes.Add(maxTokens > 0
                ? $"Per-pass max_tokens cap used: {maxTokens}."
                : "Per-pass max_tokens cap not sent; backend/model stops on EOS/context.");
            response.Debug.Notes.Add($"Speech parts: {response.SpokenParts.Count}; joined length: {response.SpokenText.Length} characters.");
            response.Debug.Notes.Add("Foreground pass does not request emotionalEffectVector or negativeEmotionVector; background review computes them.");
            if (response.NeedsMoreInformation || response.InformationNeeds.Count > 0)
            {
                response.Debug.Notes.Add("Foreground pass requested extra information: " + string.Join(", ", response.InformationNeeds.Select(x => x.DataType + ":" + x.Subject + "@" + x.Confidence.ToString("0.00"))));
            }
            if (isStory)
            {
                int storyChunks = ExtractAllMarkdownChunks(response.StoryMarkdown).Count;
                response.Debug.Notes.Add("Story plan debug: approximate target "
                    + (request.StoryOptions?.TargetMinutes ?? 0)
                    + " minute(s), "
                    + (request.StoryOptions?.TargetChunkCount ?? 0)
                    + " requested/capped chunk(s), actual markdown chunks="
                    + storyChunks
                    + ", markdown chars="
                    + (string.IsNullOrWhiteSpace(response.StoryMarkdown) ? 0 : response.StoryMarkdown.Length)
                    + ". Target is flexible; complete story arc is preferred over exact duration.");
            }
            return response;
        }

        public async Task<StarInteractionResponse> AnalyzeTurnAsync(StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse foregroundResponse, CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine($"[StructuredStarAgent] Sending BACKGROUND emotion/memory review request for {request.StarId}...");
            string json = await _client.CompleteJsonAsync(
                _promptBuilder.BuildReviewMessages(profile, request, state, foregroundResponse),
                StarResponseJsonSchema.CreateBackgroundReviewResponseFormat("six_stars_background_memory_review_compact"),
                _reviewMaxTokens,
                "background-memory-review",
                cancellationToken).ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine($"[StructuredStarAgent] Received background review response ({json.Length} chars)");
            StarStructuredModelResponse model = Parse(json, profile, request, state, requireSpokenText: false);
            NormalizeModelCollections(model);
            if (model.SpokenParts.Count == 0)
            {
                model.SpokenParts.AddRange(foregroundResponse.SpokenParts.Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            StarInteractionResponse response = BuildInteractionResponseFromModel(profile, request, state, model);
            response.EmotionalEffectVector = string.IsNullOrWhiteSpace(model.EmotionalEffectVector) ? (foregroundResponse.EmotionalEffectVector ?? string.Empty) : model.EmotionalEffectVector;
            response.NegativeEmotionVector = string.IsNullOrWhiteSpace(model.NegativeEmotionVector) ? (foregroundResponse.NegativeEmotionVector ?? string.Empty) : model.NegativeEmotionVector;
            PreserveForegroundStoryFields(foregroundResponse, response);
            response.Debug.Notes.Add("StructuredStarAgent used REAL LLM background emotion/memory review pass.");
            return response;
        }

        private static void NormalizeInterstarRelayFields(InterstarRelayMessage relay, StarProfile originProfile, StarProfile targetProfile, StarInteractionRequest originalRequest, StarInteractionResponse originResponse)
        {
            if (relay == null) return;

            relay.WhatOriginWouldTellTarget = NormalizeInterstarStarToStarText(relay.WhatOriginWouldTellTarget, targetProfile?.DisplayName, originProfile?.DisplayName);
            relay.Reason = NormalizeInterstarStarToStarText(relay.Reason, targetProfile?.DisplayName, originProfile?.DisplayName);
            relay.Summary = NormalizeInterstarStarToStarText(relay.Summary, targetProfile?.DisplayName, originProfile?.DisplayName);

            if (relay.ShouldTell && string.IsNullOrWhiteSpace(relay.Summary))
            {
                relay.Summary = CleanOneLine((originProfile?.DisplayName ?? "Origin Star") + " wanted " + (targetProfile?.DisplayName ?? "the target Star") + " to know the recent Last Cat conversation affected her.", 360);
            }
            if (relay.ShouldTell && string.IsNullOrWhiteSpace(relay.Reason))
            {
                relay.Reason = CleanOneLine("Recent Last Cat conversation had emotional or practical meaning for " + (targetProfile?.DisplayName ?? "the target Star") + ".", 360);
            }
            if (relay.ShouldTell && string.IsNullOrWhiteSpace(relay.CooldownKey))
            {
                string seed = FirstNonEmpty(relay.Summary, relay.WhatOriginWouldTellTarget, originalRequest?.PlayerText, "interstar");
                relay.CooldownKey = BuildStableCooldownKey(seed, 96);
            }
        }

        private static string NormalizeInterstarReplyToSource(string? text, string? sourceStarId)
        {
            return NormalizeInterstarStarToStarText(CleanOneLine(text, 500), sourceStarId, null);
        }

        private static string NormalizeInterstarStarToStarText(string? text, string? addressedStarName, string? originStarName)
        {
            string value = CleanOneLine(text, 900);
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            string target = string.IsNullOrWhiteSpace(addressedStarName) ? "sister" : addressedStarName.Trim();

            // Player-facing Elusiaa language should not leak into Star-to-Star speech.
            value = System.Text.RegularExpressions.Regex.Replace(value, "^\\s*little one\\s*,", target + ",", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            value = System.Text.RegularExpressions.Regex.Replace(value, "\\bwith little one\\b", "with the Last Cat", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            value = System.Text.RegularExpressions.Regex.Replace(value, "\\babout little one\\b", "about the Last Cat", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            value = System.Text.RegularExpressions.Regex.Replace(value, "\\bof little one\\b", "of the Last Cat", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            value = System.Text.RegularExpressions.Regex.Replace(value, "\\blittle one's\\b", "the Last Cat's", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Common local-model slip: make the player female after naming the Last Cat as the sentence subject.
            value = System.Text.RegularExpressions.Regex.Replace(value, "(\\bThe Last Cat\\b[^.!?]*[.!?]\\s*)She\\b", "$1He", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            value = System.Text.RegularExpressions.Regex.Replace(value, "(\\bthe Last Cat\\b[^.!?]*[.!?]\\s*)she\\b", "$1he", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            value = System.Text.RegularExpressions.Regex.Replace(value, "(\\bThe Last Cat\\b[^.!?]*[.!?]\\s*)Her\\b", "$1His", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            value = System.Text.RegularExpressions.Regex.Replace(value, "(\\bthe Last Cat\\b[^.!?]*[.!?]\\s*)her\\b", "$1his", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return CleanOneLine(value, 900);
        }

        private static string BuildStableCooldownKey(string? seed, int maxLength)
        {
            string value = CleanOneLine(seed, maxLength).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value)) return "interstar";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            bool lastDash = false;
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                    lastDash = false;
                }
                else if (!lastDash)
                {
                    sb.Append('-');
                    lastDash = true;
                }
                if (sb.Length >= maxLength) break;
            }
            return sb.ToString().Trim('-');
        }

        private static string BuildInterstarReactionSummary(StarProfile targetProfile, InterstarRelayMessage relay)
        {
            string relayText = FirstNonEmpty(relay?.Summary, relay?.WhatOriginWouldTellTarget, "a private interstar relay");
            return CleanOneLine((targetProfile?.DisplayName ?? "The Star") + " privately heard " + (relay?.OriginStarId ?? "another Star") + "'s relay about the Last Cat and considered how it affected her feelings toward him: " + relayText, 900);
        }

        private static string BuildInterstarFeedbackSummary(StarProfile originProfile, IReadOnlyList<InterstarReactionSummary> reactions)
        {
            int count = reactions == null ? 0 : reactions.Count;
            string names = reactions == null ? string.Empty : string.Join(", ", reactions.Select(x => x.StarId).Where(x => !string.IsNullOrWhiteSpace(x)).Take(6));
            return CleanOneLine((originProfile?.DisplayName ?? "The Star") + " privately processed " + count + " interstar reaction(s) about the Last Cat" + (string.IsNullOrWhiteSpace(names) ? "." : " from " + names + "."), 900);
        }

        private StarInteractionResponse BuildInteractionResponseFromModel(StarProfile profile, StarInteractionRequest request, StarState state, StarStructuredModelResponse model)
        {
            StarInteractionResponse response = new StarInteractionResponse
            {
                StarId = string.IsNullOrWhiteSpace(model.StarId) ? profile.StarId : model.StarId,
                StoryTitle = model.StoryTitle,
                StoryKind = model.StoryKind,
                StoryId = model.StoryId ?? string.Empty,
                StoryMarkdown = model.StoryMarkdown ?? string.Empty,
                StorySummary = model.StorySummary ?? string.Empty,
                StoryIsFinished = model.StoryIsFinished,
                StoryPartIndex = model.StoryPartIndex,
                EmotionalPurpose = model.EmotionalPurpose ?? string.Empty,
                EmotionalEffectVector = model.EmotionalEffectVector ?? string.Empty,
                NegativeEmotionVector = model.NegativeEmotionVector ?? string.Empty,
                VoiceDirective = model.VoiceDirective ?? VoiceDirective.Empty(),
                SuggestedStateDeltas = model.SuggestedStateDeltas ?? RelationshipDelta.Zero(),
                ConversationSummary = string.IsNullOrWhiteSpace(model.ConversationSummary)
                    ? BuildFallbackConversationSummary(state, request, model.SpokenText)
                    : model.ConversationSummary,
                ChangesSummary = string.IsNullOrWhiteSpace(model.ChangesSummary)
                    ? BuildFallbackChangesSummary(request, model.SpokenText)
                    : model.ChangesSummary,
                SourceStarEmotionalEffectVector = model.SourceStarEmotionalEffectVector ?? string.Empty,
                SourceStarNegativeEmotionVector = model.SourceStarNegativeEmotionVector ?? string.Empty,
                InterstarReplyToSourceStar = CleanOneLine(model.ReplyToSourceStar, 500),
                Debug = new DebugInfo
                {
                    DetectedIntent = request.RequestType.ToString(),
                    WisdomScore = model.WisdomScore
                }
            };
            response.SetSpokenParts(model.SpokenParts);
            response.CanonSourcesUsed.AddRange(model.CanonSourcesUsed);
            response.EmotionScores = model.EmotionScores;
            response.DetectedCombos.AddRange(model.DetectedCombos);
            // Important quote capture is deterministic now. Ignore model-provided quote choices.
            response.MemoryNotes.AddRange(model.MemoryNotes);
            response.RememberedMarkers.AddRange(model.RememberedMarkers);
            response.ReferencedMemoryMarkerIds.AddRange(model.ReferencedMemoryMarkerIds);
            response.SecretsUnlocked.AddRange(model.SecretsUnlocked);
            response.LearnedFunctions.AddRange(model.LearnedFunctions);
            response.RelationshipEvents.AddRange(model.RelationshipEvents);
            response.GirlfriendHistoryUpdates.AddRange(model.GirlfriendHistoryUpdates);
            response.PetNamesPlayerUsesForStar.AddRange(model.PetNamesPlayerUsesForStar);
            response.PetNamesStarUsesForPlayer.AddRange(model.PetNamesStarUsesForPlayer);
            response.GirlfriendNeedsMetSummary = CleanOneLine(model.GirlfriendNeedsMetSummary, 420);
            response.GirlfriendNeedsMetVector.AddRange(NormalizeGirlfriendVector(model.GirlfriendNeedsMetVector, 12));
            response.GirlfriendWantsMetVector.AddRange(NormalizeGirlfriendVector(model.GirlfriendWantsMetVector, 12));
            response.GirlfriendCoupleActivitySummary = CleanOneLine(model.GirlfriendCoupleActivitySummary, 520);
            response.GirlfriendCoupleActivityVector = NormalizeGirlfriendCoupleActivityVectorText(model.GirlfriendCoupleActivityVector, 16);
            response.GirlfriendCoupleActivityItems.AddRange(ParseGirlfriendCoupleActivityVector(response.GirlfriendCoupleActivityVector, 16));
            response.InterstarEmotionVectors.AddRange(NormalizeInterstarEmotionVectors(model.InterstarEmotionVectors));
            response.Debug.LoreUsed.AddRange(model.LoreUsed);
            response.Debug.RiskFlags.AddRange(model.RiskFlags);
            TrimEmotionScoresToImpactfulSet(response, 9);
            response.Debug.Notes.Add("StructuredStarAgent used REAL LLM compact background emotion/memory review.");
            response.Debug.Notes.Add($"Impact emotions evaluated: {response.EmotionScores.Count} emotions");
            response.Debug.Notes.Add($"Background computed emotionalEffectVector length: {response.EmotionalEffectVector.Length}; negativeEmotionVector length: {response.NegativeEmotionVector.Length}.");
            if (response.InterstarEmotionVectors.Count > 0) response.Debug.Notes.Add("Interstar relationship vectors returned: " + response.InterstarEmotionVectors.Count + ".");
            response.Debug.Notes.Add($"Remembered markers proposed: {response.RememberedMarkers.Count}; referenced marker ids: {response.ReferencedMemoryMarkerIds.Count}.");
            response.Debug.Notes.Add($"Response length: {response.SpokenText.Length} characters");
            return response;
        }

        private static List<InterstarEmotionVectorResult> NormalizeInterstarEmotionVectors(IEnumerable<InterstarEmotionVectorResult>? values)
        {
            return (values ?? Enumerable.Empty<InterstarEmotionVectorResult>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.StarId))
                .Select(x => new InterstarEmotionVectorResult
                {
                    StarId = CleanOneLine(x.StarId, 80),
                    PositiveEmotionVector = NormalizeCompactEmotionVector(x.PositiveEmotionVector, StarEmotionMatrix.StandardEmotionIds.Length),
                    NegativeEmotionVector = NormalizeCompactEmotionVector(x.NegativeEmotionVector, RelationshipDeltaCalculator.NegativeEmotionIds.Length),
                    Reason = string.Empty
                })
                .GroupBy(x => x.StarId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .Take(8)
                .ToList();
        }

        private static int ResolveSpeechMaxTokens(int configuredValue)
        {
            // Do not send per-request max_tokens caps. Compact prompts and bounded response schemas
            // are safer for the local backends used by this runtime.
            return 0;
        }

        private static int ResolveInitiativeMaxTokens()
        {
            // Initiative returns at most one short line. A small cap prevents a local model from
            // spending normal-chat time elaborating on a yes/no at-rest decision. This cap is
            // isolated from ordinary speech/story requests because some local backends are less
            // reliable when those larger responses are capped.
            string? configured = Environment.GetEnvironmentVariable("SIX_STARS_LLM_INITIATIVE_MAX_TOKENS");
            if (int.TryParse(configured, out int value))
            {
                if (value <= 0) return 0;
                return Math.Max(96, Math.Min(512, value));
            }

            return 256;
        }

        private static int ResolveStoryPlanMaxTokens()
        {
            // Do not send per-request max_tokens for story generation. Some local backends silently
            // fail or truncate when an output cap is present. The story prompt is kept compact instead.
            return 0;
        }

        private static int ResolveReviewMaxTokens(int configuredValue)
        {
            // Do not send per-request max_tokens caps. Review prompts must stay compact instead.
            return 0;
        }

        private static void PreserveForegroundStoryFields(StarInteractionResponse foreground, StarInteractionResponse analysis)
        {
            if (!string.IsNullOrWhiteSpace(foreground.StoryId) && string.IsNullOrWhiteSpace(analysis.StoryId)) analysis.StoryId = foreground.StoryId;
            if (!string.IsNullOrWhiteSpace(foreground.StoryTitle) && string.IsNullOrWhiteSpace(analysis.StoryTitle)) analysis.StoryTitle = foreground.StoryTitle;
            if (!string.IsNullOrWhiteSpace(foreground.StoryMarkdown) && string.IsNullOrWhiteSpace(analysis.StoryMarkdown)) analysis.StoryMarkdown = foreground.StoryMarkdown;
            if (!string.IsNullOrWhiteSpace(foreground.StorySummary) && string.IsNullOrWhiteSpace(analysis.StorySummary)) analysis.StorySummary = foreground.StorySummary;
            if (!string.IsNullOrWhiteSpace(foreground.EmotionalEffectVector) && string.IsNullOrWhiteSpace(analysis.EmotionalEffectVector)) analysis.EmotionalEffectVector = foreground.EmotionalEffectVector;
            if (foreground.StoryPartIndex > 0 && analysis.StoryPartIndex <= 0) analysis.StoryPartIndex = foreground.StoryPartIndex;
            analysis.StoryIsFinished = foreground.StoryIsFinished;
        }

        private static VoiceDirective BuildDefaultVoiceDirective(StarProfile profile, StarInteractionRequest request)
        {
            string delivery = request.RequestType == RequestType.Story
                ? "Tell the story in character with the Star's stable voice identity, natural breath, clear pacing, and gentle expressive continuity. Use the exact spokenText chunks without adding words."
                : "Speak in character with the Star's stable voice identity, natural timing, and emotionally grounded delivery. Use the exact spokenText chunks without adding words.";

            VoiceDirective directive = new VoiceDirective
            {
                VoiceId = string.IsNullOrWhiteSpace(profile.VoiceId) ? profile.StarId : profile.VoiceId,
                VoiceDesignPrompt = string.IsNullOrWhiteSpace(profile.VoiceDesignPrompt)
                    ? string.Join(" ", new[] { profile.PersonalityShort, profile.VoiceArchetype, profile.SpeakingStyle }.Where(x => !string.IsNullOrWhiteSpace(x)))
                    : profile.VoiceDesignPrompt,
                VoiceCloneAudioFile = profile.VoiceCloneAudioFile ?? string.Empty,
                VoiceCloneRefText = profile.VoiceCloneRefText ?? string.Empty,
                DeliveryInstructions = delivery,
                PrimaryEmotion = "in-character",
                EmotionIntensity = 0.55,
                Pace = "natural",
                Pitch = "natural",
                Volume = "normal",
                Prosody = string.IsNullOrWhiteSpace(profile.SpeakingStyle) ? "natural, character-consistent" : profile.SpeakingStyle,
                Language = "English"
            };

            directive.AudioTags.Add("character voice");
            foreach (string rule in profile.VoiceGenerationRules.Take(3))
            {
                if (!string.IsNullOrWhiteSpace(rule)) directive.Avoid.Add("Do not violate voice rule: " + rule);
            }
            return directive;
        }


        private StarStructuredModelResponse ParseInterstarVector(string rawContent, StarProfile profile, string purpose)
        {
            string json = ExtractJson(rawContent);
            try
            {
                StarStructuredModelResponse? model = JsonSerializer.Deserialize<StarStructuredModelResponse>(json, _jsonOptions);
                if (model == null) throw new InvalidOperationException(purpose + " response could not be deserialized.");
                ApplyCommonJsonAliases(json, model);
                NormalizeModelCollections(model);
                model.StarId = string.IsNullOrWhiteSpace(model.StarId) ? profile.StarId : model.StarId;
                return model;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine("[StructuredStarAgent] " + purpose + " JSON parsing failed; returning zero-vector micro update: " + ex.Message);
                StarStructuredModelResponse model = new StarStructuredModelResponse
                {
                    StarId = profile.StarId,
                    EmotionalEffectVector = BuildZeroVector(StarEmotionMatrix.StandardEmotionIds.Length),
                    NegativeEmotionVector = BuildZeroVector(RelationshipDeltaCalculator.NegativeEmotionIds.Length),
                    SourceStarEmotionalEffectVector = BuildZeroVector(StarEmotionMatrix.StandardEmotionIds.Length),
                    SourceStarNegativeEmotionVector = BuildZeroVector(RelationshipDeltaCalculator.NegativeEmotionIds.Length),
                    SuggestedStateDeltas = RelationshipDelta.Zero(),
                    ConversationSummary = profile.DisplayName + " received an interstar update, but the vector response could not be read.",
                    ChangesSummary = purpose + " parse failed: " + ex.Message,
                    WisdomScore = 0
                };
                model.RiskFlags.Add("interstar_vector_parse_failed");
                NormalizeModelCollections(model);
                return model;
            }
        }

        private static string BuildZeroVector(int count)
        {
            if (count <= 0) return string.Empty;
            return string.Join(";", Enumerable.Range(0, count).Select(_ => "0.00:0"));
        }

        private static string CleanOneLine(string? value, int maxLength)
        {
            string text = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (maxLength > 0 && text.Length > maxLength) text = text.Substring(0, maxLength).Trim();
            return text;
        }

        private StarSpeechModelResponse ParseSpeech(string rawContent, StarProfile profile, StarInteractionRequest request)
        {
            string json = ExtractJson(rawContent);
            try
            {
                StarSpeechModelResponse? model = JsonSerializer.Deserialize<StarSpeechModelResponse>(json, _jsonOptions);
                if (model == null) throw new InvalidOperationException("Fast speech model response could not be deserialized.");
                ApplySpeechJsonAliases(json, model);
                model.SpokenParts = CleanSpeechParts(model.SpokenParts);
                bool allowEmptyStarInitiative = request.RequestType == RequestType.StarInitiative && !model.HasSomethingToSay && !model.ShouldSpeak;
                if (model.SpokenParts.Count == 0 && !allowEmptyStarInitiative)
                {
                    model.SpokenParts.AddRange(ExtractLikelySpokenParts(rawContent));
                }
                if (model.SpokenParts.Count == 0 && !allowEmptyStarInitiative)
                {
                    model.SpokenParts.Add(profile.DisplayName + " heard you, little one, but my answer came back in a strange shape. Stay with me and try again.");
                }
                ApplyStorySpeechGuards(model, request);
                return model;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine($"[StructuredStarAgent] Fast speech JSON parsing failed; salvaging real LLM speech: {ex.Message}");
                StarSpeechModelResponse model = new StarSpeechModelResponse
                {
                    StarId = profile.StarId
                };
                if (request.RequestType == RequestType.Story)
                {
                    MarkStoryJsonParseFailed(request);
                    SalvageMalformedStoryFields(rawContent, model, profile);
                }
                model.SpokenParts.AddRange(ExtractLikelySpokenParts(rawContent));
                if (model.SpokenParts.Count == 0)
                {
                    model.SpokenParts.Add(profile.DisplayName + " heard you, little one, but my answer came back in a strange shape. Stay with me and try again.");
                }
                ApplyStorySpeechGuards(model, request);
                return model;
            }
        }

        private static void MarkStoryJsonParseFailed(StarInteractionRequest request)
        {
            try
            {
                if (request.GameContext != null && request.GameContext.Flags != null)
                {
                    request.GameContext.Flags["storyJsonParseFailed"] = "true";
                }
            }
            catch
            {
            }
        }

        private static void SalvageMalformedStoryFields(string rawContent, StarSpeechModelResponse model, StarProfile profile)
        {
            if (model == null) return;
            model.StoryTitle = FirstNonEmpty(model.StoryTitle, ExtractJsonStringProperty(rawContent, "storyTitle"), "Interrupted Story");
            model.StoryId = FirstNonEmpty(model.StoryId, ExtractJsonStringProperty(rawContent, "storyId"), "interrupted_story_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            model.StoryMarkdown = FirstNonEmpty(model.StoryMarkdown, ExtractJsonStringProperty(rawContent, "storyMarkdown"));
            model.StorySummary = FirstNonEmpty(model.StorySummary, ExtractJsonStringProperty(rawContent, "storySummary"), profile.DisplayName + " began a story, but the local model response was cut off before valid JSON closed.");
        }

        private static void ApplyStorySpeechGuards(StarSpeechModelResponse model, StarInteractionRequest request)
        {
            if (request.RequestType != RequestType.Story || model == null)
            {
                return;
            }

            StoryOptions options = request.StoryOptions ?? new StoryOptions();
            int maxSentences = Math.Max(1, options.MaxSentencesPerChunk <= 0 ? 2 : options.MaxSentencesPerChunk);

            if (!string.IsNullOrWhiteSpace(model.StoryMarkdown))
            {
                model.StoryMarkdown = NormalizeStoryMarkdownIntoShortChunks(model.StoryMarkdown, maxSentences);
                model.StoryMarkdown = RepairStoryMarkdownIfTruncated(model.StoryMarkdown, maxSentences);
            }

            bool continuation = options.ContinueActiveStory;
            bool storyJsonParseFailed = request.GameContext != null
                && request.GameContext.Flags != null
                && request.GameContext.Flags.TryGetValue("storyJsonParseFailed", out string? parseFailedFlag)
                && string.Equals(parseFailedFlag, "true", StringComparison.OrdinalIgnoreCase);

            if (!continuation && !storyJsonParseFailed && string.IsNullOrWhiteSpace(model.StoryMarkdown) && model.SpokenParts.Count > 0)
            {
                // Some local OpenAI-compatible backends may ignore the storyMarkdown field under
                // response_format pressure. Preserve a valid local-autoplay shape rather than
                // allowing story mode to collapse into an intro or empty story. Do not do this
                // when JSON parsing failed, because that usually means the model was cut off and
                // only Chunk 0 survived as spokenText. In that case, retry/fix the prompt rather
                // than cache a fake one-chunk story.
                model.StoryMarkdown = NormalizeStoryMarkdownIntoShortChunks(string.Join(" ", model.SpokenParts), maxSentences);
                model.StoryIsFinished = true;
            }

            int expectedIndex = continuation ? Math.Max(0, options.NextStoryPartIndex) : 0;
            string expectedChunk = continuation
                ? FirstNonEmpty(options.NextStoryChunkMarkdown, ExtractMarkdownChunk(options.ActiveStoryMarkdown, expectedIndex))
                : ExtractMarkdownChunk(model.StoryMarkdown, 0);

            if (!string.IsNullOrWhiteSpace(expectedChunk) && ShouldReplaceStorySpeechWithMarkdownChunk(model.SpokenParts, expectedChunk))
            {
                model.SpokenParts = CleanSpeechParts(new[] { CleanMarkdownChunkForSpeech(expectedChunk) });
                System.Diagnostics.Debug.WriteLine("[StructuredStarAgent] Story speech guard replaced intro/mismatched spokenText with markdown chunk " + expectedIndex + ".");
            }

            model.SpokenParts = SplitSpokenPartsIntoSentenceChunks(model.SpokenParts, maxSentences);

            if (continuation)
            {
                if (!string.IsNullOrWhiteSpace(expectedChunk) || model.StoryPartIndex < expectedIndex)
                {
                    model.StoryPartIndex = expectedIndex;
                }

                if (string.IsNullOrWhiteSpace(model.StoryId)) model.StoryId = options.ActiveStoryId;
                if (string.IsNullOrWhiteSpace(model.StoryTitle)) model.StoryTitle = options.ActiveStoryTitle;
                if (string.IsNullOrWhiteSpace(model.StorySummary)) model.StorySummary = options.ActiveStorySummary;
            }
            else
            {
                // A new story always speaks Chunk 0 first. The desktop host auto-plays
                // Chunk 1+ locally from storyMarkdown. Do not force StoryIsFinished=false
                // merely because cached chunks remain; StoryIsFinished now means whether
                // the generated markdown is a complete episode, not whether local autoplay
                // has finished speaking every chunk.
                model.StoryPartIndex = 0;
                if (!string.IsNullOrWhiteSpace(model.StoryMarkdown) && model.StoryIsFinished == false && !LooksLikeIntentionallyUnfinishedStory(model.StoryMarkdown))
                {
                    model.StoryIsFinished = true;
                }
            }
        }

        private static string RepairStoryMarkdownIfTruncated(string markdown, int maxSentencesPerChunk)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
            string trimmed = markdown.Trim();
            if (!LooksLikeTruncatedStoryTail(trimmed)) return trimmed;

            List<string> chunks = ExtractAllMarkdownChunks(trimmed);
            if (chunks.Count == 0) return trimmed;

            // If the last chunk is an obvious half-sentence caused by local context truncation,
            // remove it and append a deliberate soft-cliffhanger closure so the desktop host
            // does not autoplay a story that dies mid-thought.
            if (chunks.Count > 1 && LooksLikeTruncatedStoryTail(chunks[chunks.Count - 1]))
            {
                chunks.RemoveAt(chunks.Count - 1);
            }
            if (chunks.Count == 0) return trimmed;

            string closure = "The path did not give up all of its secrets at once, but the sign was clear: this was not the old story repeating itself. It was a doorway waiting for the Last Cat and the Star who dared to follow.";
            chunks.Add(closure);

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < chunks.Count; i++)
            {
                if (i > 0) sb.AppendLine().AppendLine("---").AppendLine();
                sb.Append("## Chunk ").Append(i).AppendLine();
                sb.AppendLine(chunks[i].Trim());
            }
            return sb.ToString().Trim();
        }

        private static bool LooksLikeTruncatedStoryTail(string markdownOrChunk)
        {
            if (string.IsNullOrWhiteSpace(markdownOrChunk)) return false;
            string tail = markdownOrChunk.Trim();
            if (tail.Length > 260) tail = tail.Substring(tail.Length - 260).Trim();
            if (tail.EndsWith("...", StringComparison.Ordinal)) return true;
            char last = tail[tail.Length - 1];
            if (".!?…\"'”’)]}".IndexOf(last) >= 0) return false;
            if (",;:-–—".IndexOf(last) >= 0) return true;
            string lowered = tail.ToLowerInvariant();
            string[] weakEndings = { " and", " as", " when", " while", " because", " but", " then", " toward", " into", " through", " with", " without", " seemed to", " began to" };
            return weakEndings.Any(x => lowered.EndsWith(x, StringComparison.Ordinal));
        }

        private static bool LooksLikeIntentionallyUnfinishedStory(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return false;
            string tail = markdown.Trim();
            if (tail.Length > 600) tail = tail.Substring(tail.Length - 600);
            return tail.IndexOf("to be continued", StringComparison.OrdinalIgnoreCase) >= 0
                || tail.IndexOf("continued", StringComparison.OrdinalIgnoreCase) >= 0 && tail.IndexOf("next", StringComparison.OrdinalIgnoreCase) >= 0
                || tail.EndsWith("...", StringComparison.Ordinal);
        }

        private static string NormalizeStoryMarkdownIntoShortChunks(string markdown, int maxSentencesPerChunk)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
            List<string> originalChunks = ExtractAllMarkdownChunks(markdown);
            if (originalChunks.Count == 0)
            {
                originalChunks.Add(CleanMarkdownChunkForSpeech(markdown));
            }

            List<string> normalizedChunks = new List<string>();
            foreach (string chunk in originalChunks)
            {
                foreach (string shortChunk in SplitTextIntoSentenceGroups(chunk, maxSentencesPerChunk))
                {
                    if (!string.IsNullOrWhiteSpace(shortChunk)) normalizedChunks.Add(shortChunk.Trim());
                }
            }

            if (normalizedChunks.Count == 0) return markdown.Trim();

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < normalizedChunks.Count; i++)
            {
                if (i > 0) sb.AppendLine().AppendLine("---").AppendLine();
                sb.Append("## Chunk ").Append(i).AppendLine();
                sb.AppendLine(normalizedChunks[i]);
            }
            return sb.ToString().Trim();
        }

        private static List<string> ExtractAllMarkdownChunks(string? markdown)
        {
            List<string> chunks = new List<string>();
            if (string.IsNullOrWhiteSpace(markdown)) return chunks;
            string text = markdown.Replace("\r\n", "\n");
            MatchCollection matches = Regex.Matches(
                text,
                @"(?ims)^\s*#{1,6}\s*Chunk\s+\d+\b[^\n]*\n(?<body>.*?)(?=^\s*---+\s*$|^\s*#{1,6}\s*Chunk\s+\d+\b|\z)",
                RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                string cleaned = CleanMarkdownChunkForSpeech(match.Groups["body"].Value);
                if (!string.IsNullOrWhiteSpace(cleaned)) chunks.Add(cleaned);
            }

            if (chunks.Count == 0 && Regex.IsMatch(text, @"(?m)^\s*---+\s*$"))
            {
                chunks.AddRange(Regex.Split(text, @"(?m)^\s*---+\s*$")
                    .Select(CleanMarkdownChunkForSpeech)
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            return chunks;
        }

        private static List<string> SplitSpokenPartsIntoSentenceChunks(IEnumerable<string>? parts, int maxSentencesPerChunk)
        {
            List<string> result = new List<string>();
            foreach (string part in parts ?? Array.Empty<string>())
            {
                result.AddRange(SplitTextIntoSentenceGroups(part, maxSentencesPerChunk));
            }
            return CleanSpeechParts(result);
        }

        private static List<string> SplitTextIntoSentenceGroups(string? text, int maxSentencesPerChunk)
        {
            List<string> groups = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return groups;
            int maxSentences = Math.Max(1, maxSentencesPerChunk);
            string cleaned = CleanMarkdownChunkForSpeech(text);
            if (string.IsNullOrWhiteSpace(cleaned)) return groups;

            string[] sentences = Regex.Split(cleaned.Trim(), @"(?<=[.!?…])\s+")
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            if (sentences.Length == 0)
            {
                groups.Add(cleaned.Trim());
                return groups;
            }

            List<string> current = new List<string>();
            foreach (string sentence in sentences)
            {
                current.Add(sentence);
                if (current.Count >= maxSentences)
                {
                    groups.Add(string.Join(" ", current).Trim());
                    current.Clear();
                }
            }
            if (current.Count > 0) groups.Add(string.Join(" ", current).Trim());
            return groups;
        }

        private static bool ShouldReplaceStorySpeechWithMarkdownChunk(IReadOnlyList<string>? spokenParts, string markdownChunk)
        {
            string chunkSpeech = CleanMarkdownChunkForSpeech(markdownChunk);
            if (string.IsNullOrWhiteSpace(chunkSpeech)) return false;

            string spoken = NormalizeForStoryCompare(string.Join(" ", spokenParts ?? Array.Empty<string>()));
            string chunk = NormalizeForStoryCompare(chunkSpeech);
            if (string.IsNullOrWhiteSpace(spoken)) return true;

            bool speechAppearsInChunk = chunk.Contains(spoken, StringComparison.OrdinalIgnoreCase)
                || spoken.Contains(chunk, StringComparison.OrdinalIgnoreCase);
            if (speechAppearsInChunk) return false;

            if (LooksLikeStoryIntroInsteadOfNarration(spoken)) return true;

            // Story mode is supposed to speak exact prose from the planned markdown chunk. If the
            // LLM planned a good markdown story but gave a preface or unrelated text aloud, favor the
            // chunk so the player actually hears the story.
            return true;
        }

        private static bool LooksLikeStoryIntroInsteadOfNarration(string normalizedSpeech)
        {
            if (string.IsNullOrWhiteSpace(normalizedSpeech)) return false;
            string value = normalizedSpeech.ToLowerInvariant();
            string[] introPhrases =
            {
                "let me tell",
                "i will tell",
                "i'll tell",
                "shall i begin",
                "shall we begin",
                "the story begins",
                "our story begins",
                "listen closely",
                "where we left off",
                "let us continue",
                "i will continue",
                "i'll continue",
                "continue the story",
                "a story for you",
                "this story is about"
            };
            return introPhrases.Any(value.Contains);
        }

        private static string ExtractMarkdownChunk(string? markdown, int chunkIndex)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
            int wanted = Math.Max(0, chunkIndex);
            string text = markdown.Replace("\r\n", "\n");

            MatchCollection matches = Regex.Matches(
                text,
                @"(?ims)^\s*#{1,6}\s*Chunk\s+(?<index>\d+)\b[^\n]*\n(?<body>.*?)(?=^\s*---+\s*$|^\s*#{1,6}\s*Chunk\s+\d+\b|\z)",
                RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                if (int.TryParse(match.Groups["index"].Value, out int index) && index == wanted)
                {
                    return match.Groups["body"].Value.Trim();
                }
            }

            if (!Regex.IsMatch(text, @"(?m)^\s*---+\s*$"))
            {
                return string.Empty;
            }

            string[] pieces = Regex.Split(text, @"(?m)^\s*---+\s*$")
                .Select(CleanMarkdownChunkForSpeech)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            if (pieces.Length == 0) return string.Empty;
            return wanted < pieces.Length ? pieces[wanted] : string.Empty;
        }

        private static string CleanMarkdownChunkForSpeech(string? markdownChunk)
        {
            if (string.IsNullOrWhiteSpace(markdownChunk)) return string.Empty;
            string text = markdownChunk.Replace("\r\n", "\n").Trim();
            text = Regex.Replace(text, @"(?im)^\s*#{1,6}\s*Chunk\s+\d+\b[^\n]*$", string.Empty);
            text = Regex.Replace(text, @"(?im)^\s*#\s+.+$", string.Empty);
            text = Regex.Replace(text, @"(?m)^\s*---+\s*$", string.Empty);
            text = Regex.Replace(text, @"(?m)\s+---+\s*$", string.Empty);
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            return text.Trim();
        }

        private static string NormalizeForStoryCompare(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string text = value.ToLowerInvariant();
            text = Regex.Replace(text, @"[`*_>#\-–—\[\](){}]", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }

        private static void ApplySpeechJsonAliases(string json, StarSpeechModelResponse model)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (model.SpokenParts.Count == 0)
                {
                    model.SpokenParts.AddRange(GetStringOrArray(root, "spokenText"));
                    model.SpokenParts.AddRange(GetStringOrArray(root, "spoken_text"));
                    model.SpokenParts.AddRange(GetStringOrArray(root, "reply"));
                    model.SpokenParts.AddRange(GetStringOrArray(root, "response"));
                }
                model.StoryId = string.IsNullOrWhiteSpace(model.StoryId) ? (GetString(root, "storyId") ?? GetString(root, "story_id") ?? string.Empty) : model.StoryId;
                model.StoryTitle = string.IsNullOrWhiteSpace(model.StoryTitle) ? (GetString(root, "storyTitle") ?? GetString(root, "story_title")) : model.StoryTitle;
                model.StoryMarkdown = string.IsNullOrWhiteSpace(model.StoryMarkdown) ? (GetString(root, "storyMarkdown") ?? GetString(root, "story_markdown") ?? string.Empty) : model.StoryMarkdown;
                model.StorySummary = string.IsNullOrWhiteSpace(model.StorySummary) ? (GetString(root, "storySummary") ?? GetString(root, "story_summary") ?? string.Empty) : model.StorySummary;
                if (root.TryGetProperty("storyIsFinished", out JsonElement finished) || root.TryGetProperty("story_is_finished", out finished))
                {
                    if (finished.ValueKind == JsonValueKind.True || finished.ValueKind == JsonValueKind.False) model.StoryIsFinished = finished.GetBoolean();
                }
                if (root.TryGetProperty("storyPartIndex", out JsonElement part) || root.TryGetProperty("story_part_index", out part))
                {
                    if (part.ValueKind == JsonValueKind.Number && part.TryGetInt32(out int index)) model.StoryPartIndex = index;
                }
            }
            catch
            {
            }
        }

        private static void NormalizeModelCollections(StarStructuredModelResponse model)
        {
            model.SpokenParts = CleanSpeechParts(model.SpokenParts);
            model.CanonSourcesUsed ??= new List<string>();
            model.VoiceDirective ??= VoiceDirective.Empty();
            model.EmotionScores ??= new Dictionary<string, EmotionScore>();
            model.DetectedCombos ??= new List<string>();
            model.SuggestedStateDeltas ??= RelationshipDelta.Zero();
            model.ImportantPlayerQuotes ??= new List<string>();
            model.MemoryNotes ??= new List<string>();
            model.RememberedMarkers ??= new List<StarRememberedMarker>();
            model.ReferencedMemoryMarkerIds ??= new List<string>();
            model.SecretsUnlocked ??= new List<string>();
            model.LearnedFunctions ??= new List<StarLearnedFunction>();
            model.RelationshipEvents ??= new List<string>();
            model.GirlfriendHistoryUpdates ??= new List<string>();
            model.PetNamesPlayerUsesForStar ??= new List<string>();
            model.PetNamesStarUsesForPlayer ??= new List<string>();
            model.GirlfriendNeedsMetVector = NormalizeGirlfriendVector(model.GirlfriendNeedsMetVector, 12);
            model.GirlfriendWantsMetVector = NormalizeGirlfriendVector(model.GirlfriendWantsMetVector, 12);
            model.GirlfriendCoupleActivityVector = NormalizeGirlfriendCoupleActivityVectorText(model.GirlfriendCoupleActivityVector, 16);
            model.InterstarEmotionVectors = NormalizeInterstarEmotionVectors(model.InterstarEmotionVectors);
            model.LoreUsed ??= new List<string>();
            model.RiskFlags ??= new List<string>();
            model.EmotionalEffectVector = NormalizeCompactEmotionVector(model.EmotionalEffectVector, StarEmotionMatrix.StandardEmotionIds.Length);
            model.NegativeEmotionVector = NormalizeCompactEmotionVector(model.NegativeEmotionVector, RelationshipDeltaCalculator.NegativeEmotionIds.Length);
            model.SourceStarEmotionalEffectVector = NormalizeCompactEmotionVector(model.SourceStarEmotionalEffectVector, StarEmotionMatrix.StandardEmotionIds.Length);
            model.SourceStarNegativeEmotionVector = NormalizeCompactEmotionVector(model.SourceStarNegativeEmotionVector, RelationshipDeltaCalculator.NegativeEmotionIds.Length);
        }

        private static string NormalizeCompactEmotionVector(string? vector, int expectedPairs)
        {
            if (string.IsNullOrWhiteSpace(vector)) return string.Empty;
            string[] rawParts = vector.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> parts = new List<string>();
            for (int i = 0; i < Math.Max(0, expectedPairs); i++)
            {
                double confidence = 0;
                double score = 0;
                if (i < rawParts.Length)
                {
                    string[] pair = rawParts[i].Trim().Split(':');
                    if (pair.Length >= 2)
                    {
                        double.TryParse(pair[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out confidence);
                        double.TryParse(pair[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out score);
                    }
                }
                confidence = Math.Max(0, Math.Min(1, confidence));
                score = Math.Max(0, Math.Min(100, score));
                parts.Add(confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ":" + score.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            }
            return string.Join(";", parts);
        }

        private static List<string> CleanSpeechParts(IEnumerable<string>? parts)
        {
            // Never discard foreground speech. Long Chat answers are intentionally represented as
            // an ordered spokenText array, and every item must survive for UI display and TTS.
            // The prompt asks the model to make natural one-or-two-sentence chunks; this cleanup
            // only normalizes whitespace and does not impose a character cap or append ellipses.
            return (parts ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => Regex.Replace(x.Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        private static string BuildFallbackConversationSummary(StarState state, StarInteractionRequest request, string? spokenText)
        {
            string prior = string.IsNullOrWhiteSpace(state.ConversationSummary) ? "" : state.ConversationSummary.Trim();
            string player = TruncateForSummary(request.PlayerText ?? string.Empty, 220);
            string star = TruncateForSummary(spokenText ?? string.Empty, 220);
            string turn = "The Last Cat just said: \"" + player + "\". The Star replied: \"" + star + "\".";
            if (string.IsNullOrWhiteSpace(prior)) return turn;
            return TruncateForSummary(prior + " " + turn, 1400);
        }

        private static string BuildFallbackChangesSummary(StarInteractionRequest request, string? spokenText)
        {
            return "Player request type " + request.RequestType + ": \""
                + TruncateForSummary(request.PlayerText ?? string.Empty, 180)
                + "\". Star answered: \""
                + TruncateForSummary(spokenText ?? string.Empty, 180)
                + "\".";
        }

        private static string TruncateForSummary(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string trimmed = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (trimmed.Length <= maxLength) return trimmed;
            return trimmed.Substring(0, maxLength).Trim() + "...";
        }

        private StarStructuredModelResponse Parse(string rawContent, StarProfile profile, StarInteractionRequest request, StarState state, bool requireSpokenText = true)
        {
            string json = ExtractJson(rawContent);
            if (!json.TrimEnd().EndsWith("}"))
            {
                System.Diagnostics.Debug.WriteLine("[StructuredStarAgent] WARNING: JSON response appears truncated; attempting real-LLM salvage.");
            }

            try
            {
                StarStructuredModelResponse? model = JsonSerializer.Deserialize<StarStructuredModelResponse>(json, _jsonOptions);
                if (model == null) throw new InvalidOperationException("Structured model response could not be deserialized.");
                ApplyCommonJsonAliases(json, model);
                model.SpokenParts = CleanSpeechParts(model.SpokenParts);
                if (!requireSpokenText)
                {
                    // Background emotion/memory review intentionally does not have to return spokenText.
                    // The foreground reply has already gone to TTS, and AnalyzeTurnAsync will copy those
                    // foreground speech chunks back onto the background response after parsing.
                    return model;
                }
                if (model.SpokenParts.Count == 0)
                {
                    model.SpokenParts.AddRange(ExtractLikelySpokenParts(rawContent));
                    model.RiskFlags.Add("llm_spoken_text_salvaged");
                }
                if (model.SpokenParts.Count == 0)
                {
                    return BuildSalvagedModelResponse(rawContent, profile, request, state, "Structured JSON parsed, but spokenText was missing or empty.");
                }
                return model;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine($"[StructuredStarAgent] JSON parsing failed but raw LLM content will be used instead of mock data: {ex.Message}");
                StarStructuredModelResponse salvaged = BuildSalvagedModelResponse(rawContent, profile, request, state, ex.Message);
                if (!requireSpokenText)
                {
                    // Background review failures should not invent a new spoken reply. The foreground
                    // speech already exists and AnalyzeTurnAsync will copy it back after parsing.
                    salvaged.SpokenParts.Clear();
                }
                ApplyCommonJsonAliases(json, salvaged);
                NormalizeModelCollections(salvaged);
                return salvaged;
            }
        }

        private static List<string> CleanList(IEnumerable<string>? values, int maxItems)
        {
            return (values ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maxItems))
                .ToList();
        }



        private static string Slugify(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string cleaned = Regex.Replace(value.Trim(), "[^A-Za-z0-9]+", " ");
            string[] parts = cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return string.Empty;
            string first = parts[0].ToLowerInvariant();
            return first + string.Concat(parts.Skip(1).Select(x => char.ToUpperInvariant(x[0]) + x.Substring(1).ToLowerInvariant()));
        }

        private static string NormalizeGirlfriendCoupleActivityVectorText(string raw, int maxItems)
        {
            List<GirlfriendVectorItem> parsed = ParseGirlfriendCoupleActivityVector(raw, maxItems);
            if (parsed.Count == 0) return string.Empty;
            return string.Join(";", parsed.Select(x => (x.ItemId ?? string.Empty) + ":" + x.Score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ":" + x.Confidence.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
        }


        private static readonly string[] GirlfriendCoupleActivityIds = new[]
        {
            "holdingHands", "hugging", "cuddling", "foreheadTouch", "booping", "petting", "stroking", "purringClose",
            "cheekKiss", "mouthKiss", "faceKissing", "lapHolding", "verbalDevotion", "possessiveClaim", "heatedRomance", "deepSexting"
        };

        private static string NormalizeGirlfriendCoupleActivityId(string value)
        {
            string compact = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (compact.Length == 0) return string.Empty;
            foreach (string key in GirlfriendCoupleActivityIds)
            {
                string keyCompact = new string(key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                if (string.Equals(compact, keyCompact, StringComparison.OrdinalIgnoreCase)) return key;
            }
            return compact;
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
                if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double score)) score = 0;
                if (!double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double confidence)) confidence = 0;
                if (confidence > 1) confidence /= 100.0;
                score = Math.Max(0, Math.Min(100, score));
                confidence = Math.Max(0, Math.Min(1, confidence));
                if (score <= 0 && confidence <= 0) continue;
                result.Add(new GirlfriendVectorItem
                {
                    ItemId = id,
                    DisplayName = id,
                    Score = score,
                    Confidence = confidence,
                    Reason = parts.Length > 3 ? CleanOneLine(string.Join(":", parts.Skip(3)), 260) : string.Empty
                });
            }
            return result
                .OrderByDescending(x => x.Score * Math.Max(0.01, x.Confidence))
                .ThenByDescending(x => x.Score)
                .Take(Math.Max(1, maxItems))
                .ToList();
        }

        private static List<GirlfriendVectorItem> NormalizeGirlfriendVector(IEnumerable<GirlfriendVectorItem>? values, int maxItems)
        {
            return (values ?? Enumerable.Empty<GirlfriendVectorItem>())
                .Where(x => x != null && (!string.IsNullOrWhiteSpace(x.ItemId) || !string.IsNullOrWhiteSpace(x.DisplayName)))
                .Select(x => new GirlfriendVectorItem
                {
                    ItemId = string.IsNullOrWhiteSpace(x.ItemId) ? (x.DisplayName ?? string.Empty).Trim() : x.ItemId.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(x.DisplayName) ? (x.ItemId ?? string.Empty).Trim() : x.DisplayName.Trim(),
                    Confidence = Clamp01(x.Confidence),
                    Score = ClampScore(x.Score),
                    Reason = CleanOneLine(x.Reason, 260)
                })
                .GroupBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.Score).ThenByDescending(x => x.Confidence).First())
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Confidence)
                .Take(Math.Max(1, maxItems))
                .ToList();
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value)) return 0;
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        private static double ClampScore(double value)
        {
            if (double.IsNaN(value)) return 0;
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }

        private static string ExtractJson(string rawContent)
        {
            if (string.IsNullOrWhiteSpace(rawContent)) return string.Empty;
            string text = rawContent.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNewLine = text.IndexOf('\n');
                int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (firstNewLine >= 0 && lastFence > firstNewLine)
                {
                    text = text.Substring(firstNewLine + 1, lastFence - firstNewLine - 1).Trim();
                }
            }
            if ((text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
                || (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal))) return text;
            int start = text.IndexOf('{');
            if (start < 0) return text;
            bool inString = false;
            bool escaped = false;
            int depth = 0;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && inString) { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1).Trim();
                }
            }
            return text.Substring(start).Trim();
        }

        private StarStructuredModelResponse BuildSalvagedModelResponse(string rawContent, StarProfile profile, StarInteractionRequest request, StarState state, string reason)
        {
            List<string> spokenParts = ExtractLikelySpokenParts(rawContent);
            if (spokenParts.Count == 0)
            {
                spokenParts.Add(profile.DisplayName + " received your words, little one, but my thoughts came back in a shape the system could not fully read. Stay with me and try again; I am still here.");
            }

            StarStructuredModelResponse model = new StarStructuredModelResponse
            {
                StarId = profile.StarId,
                EmotionalPurpose = "Recovered from a real local LLM response that was not valid structured JSON.",
                VoiceDirective = VoiceDirective.Empty(),
                SuggestedStateDeltas = RelationshipDelta.Zero(),
                ConversationSummary = BuildFallbackConversationSummary(state, request, string.Join(" ", spokenParts)),
                ChangesSummary = BuildFallbackChangesSummary(request, string.Join(" ", spokenParts)),
                WisdomScore = 0
            };
            model.SpokenParts.AddRange(CleanSpeechParts(spokenParts));
            // Do not salvage player text into importantPlayerQuotes; deterministic high-impact emotion scoring owns quote capture.
            model.RiskFlags.Add("llm_structured_json_parse_failed_salvaged");
            model.RiskFlags.Add("llm_raw_response_used_not_mock");
            model.LoreUsed.Add("local-llm-raw-content");
            return model;
        }

        private static void ApplyDailyStateJsonAliases(string json, StarDailyStateModelResponse model)
        {
            if (string.IsNullOrWhiteSpace(json) || model == null) return;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                if (string.IsNullOrWhiteSpace(model.StarId)) model.StarId = GetString(root, "starId") ?? GetString(root, "star_id") ?? string.Empty;

                if (string.IsNullOrWhiteSpace(model.SituationSummary))
                {
                    model.SituationSummary = FirstJsonString(root,
                        new[] { "situationSummary" },
                        new[] { "currentInnerLife", "situationSummary" },
                        new[] { "current_inner_life", "situation_summary" },
                        new[] { "currentInnerLife", "summary" });
                }
                if (string.IsNullOrWhiteSpace(model.FeelingsSummary))
                {
                    model.FeelingsSummary = FirstJsonString(root,
                        new[] { "feelingsSummary" },
                        new[] { "feelings" },
                        new[] { "currentFeelings" },
                        new[] { "currentInnerLife", "feelingsSummary" },
                        new[] { "currentInnerLife", "feelings" },
                        new[] { "current_inner_life", "feelings_summary" },
                        new[] { "current_inner_life", "feelings" });
                }
                if (string.IsNullOrWhiteSpace(model.WhatSheIsDoingToday))
                {
                    model.WhatSheIsDoingToday = FirstJsonString(root,
                        new[] { "whatSheIsDoingToday" },
                        new[] { "whatSheIsDoing" },
                        new[] { "today", "whatSheIsDoing" },
                        new[] { "today", "whatSheIsDoingToday" },
                        new[] { "today", "activity" });
                }
                if (string.IsNullOrWhiteSpace(model.CurrentDesires))
                {
                    model.CurrentDesires = FirstJsonString(root,
                        new[] { "currentDesires" },
                        new[] { "desires" },
                        new[] { "currentInnerLife", "currentDesires" },
                        new[] { "currentInnerLife", "desires" },
                        new[] { "current_inner_life", "current_desires" },
                        new[] { "current_inner_life", "desires" });
                }
                if (string.IsNullOrWhiteSpace(model.RelationshipPosture))
                {
                    model.RelationshipPosture = FirstJsonString(root,
                        new[] { "relationshipPosture" },
                        new[] { "posture" },
                        new[] { "currentInnerLife", "relationshipPosture" },
                        new[] { "current_inner_life", "relationship_posture" });
                }
                if (string.IsNullOrWhiteSpace(model.RecentPeopleSeenSummary))
                {
                    model.RecentPeopleSeenSummary = FirstJsonString(root,
                        new[] { "recentPeopleSeenSummary" },
                        new[] { "peopleSeenSummary" },
                        new[] { "recentAwareness", "peopleSeenSummary" },
                        new[] { "recent_awareness", "people_seen_summary" });
                }
                if (string.IsNullOrWhiteSpace(model.RecentEventsSummary))
                {
                    model.RecentEventsSummary = FirstJsonString(root,
                        new[] { "recentEventsSummary" },
                        new[] { "eventsSummary" },
                        new[] { "recentAwareness", "eventsSummary" },
                        new[] { "recent_awareness", "events_summary" });
                }
                if (string.IsNullOrWhiteSpace(model.PrivateSecretsOrHooks))
                {
                    model.PrivateSecretsOrHooks = FirstJsonString(root,
                        new[] { "privateSecretsOrHooks" },
                        new[] { "privateHooks" },
                        new[] { "currentInnerLife", "privateSecretsOrHooks" },
                        new[] { "current_inner_life", "private_secrets_or_hooks" });
                }
                if (string.IsNullOrWhiteSpace(model.PromptUseSummary))
                {
                    model.PromptUseSummary = FirstJsonString(root,
                        new[] { "promptUseSummary" },
                        new[] { "promptUse", "summary" },
                        new[] { "prompt_use", "summary" });
                }

                if (model.MoodTags == null || model.MoodTags.Count == 0)
                {
                    model.MoodTags = FirstJsonStringList(root,
                        new[] { "moodTags" },
                        new[] { "today", "moodTags" },
                        new[] { "today", "mood_tags" });
                }
                if (model.RecentPeopleSeen == null || model.RecentPeopleSeen.Count == 0)
                {
                    model.RecentPeopleSeen = FirstJsonStringList(root,
                        new[] { "recentPeopleSeen" },
                        new[] { "peopleSeen" },
                        new[] { "recentAwareness", "peopleSeen" },
                        new[] { "recentAwareness", "recentPeopleSeen" },
                        new[] { "recent_awareness", "people_seen" });
                }
                if (model.RecentEvents == null || model.RecentEvents.Count == 0)
                {
                    model.RecentEvents = FirstJsonStringList(root,
                        new[] { "recentEvents" },
                        new[] { "events" },
                        new[] { "recentAwareness", "events" },
                        new[] { "recentAwareness", "recentEvents" },
                        new[] { "recent_awareness", "events" });
                }
                if (model.TodayIntentions == null || model.TodayIntentions.Count == 0)
                {
                    model.TodayIntentions = FirstJsonStringList(root,
                        new[] { "todayIntentions" },
                        new[] { "intentions" },
                        new[] { "today", "intentions" },
                        new[] { "today", "todayIntentions" },
                        new[] { "today", "today_intentions" });
                }
                if (model.ThingsSheWantsToTalkAbout == null || model.ThingsSheWantsToTalkAbout.Count == 0)
                {
                    model.ThingsSheWantsToTalkAbout = FirstJsonStringList(root,
                        new[] { "thingsSheWantsToTalkAbout" },
                        new[] { "wantsToTalkAbout" },
                        new[] { "today", "thingsSheWantsToTalkAbout" },
                        new[] { "today", "wantsToTalkAbout" },
                        new[] { "today", "wants_to_talk_about" });
                }
                if (model.ConversationHooks == null || model.ConversationHooks.Count == 0)
                {
                    model.ConversationHooks = FirstJsonStringList(root,
                        new[] { "conversationHooks" },
                        new[] { "today", "conversationHooks" },
                        new[] { "today", "conversation_hooks" });
                }
            }
            catch
            {
            }
        }

        private static void ApplyCommonJsonAliases(string json, StarStructuredModelResponse model)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (model.SpokenParts.Count == 0)
                {
                    model.SpokenParts.AddRange(GetStringOrArray(root, "spokenText"));
                    model.SpokenParts.AddRange(GetStringOrArray(root, "spoken_text"));
                    model.SpokenParts.AddRange(GetStringOrArray(root, "reply"));
                    model.SpokenParts.AddRange(GetStringOrArray(root, "response"));
                    model.SpokenParts.AddRange(GetStringOrArray(root, "message"));
                }
                if (string.IsNullOrWhiteSpace(model.ConversationSummary))
                {
                    model.ConversationSummary = GetString(root, "conversationSummary") ?? GetString(root, "conversation_summary") ?? GetString(root, "summary") ?? string.Empty;
                }
                if (string.IsNullOrWhiteSpace(model.ChangesSummary))
                {
                    model.ChangesSummary = GetString(root, "changesSummary") ?? GetString(root, "changes_summary") ?? GetString(root, "turnSummary") ?? GetString(root, "turn_summary") ?? string.Empty;
                }
                if (string.IsNullOrWhiteSpace(model.EmotionalEffectVector))
                {
                    model.EmotionalEffectVector = GetCompactVectorString(root, "lastCatEmotionalEffectVector", "last_cat_emotional_effect_vector", "emotionalEffectVector", "emotional_effect_vector", "positiveEmotionVector", "positive_emotion_vector") ?? string.Empty;
                }
                if (string.IsNullOrWhiteSpace(model.NegativeEmotionVector))
                {
                    model.NegativeEmotionVector = GetCompactVectorString(root, "lastCatNegativeEmotionVector", "last_cat_negative_emotion_vector", "negativeEmotionVector", "negative_emotion_vector", "boundaryEmotionVector", "boundary_emotion_vector") ?? string.Empty;
                }
                if (string.IsNullOrWhiteSpace(model.SourceStarEmotionalEffectVector))
                {
                    model.SourceStarEmotionalEffectVector = GetCompactVectorString(root, "sourceStarEmotionalEffectVector", "sourceStarPositiveEmotionVector", "source_star_positive_emotion_vector") ?? string.Empty;
                }
                if (string.IsNullOrWhiteSpace(model.SourceStarNegativeEmotionVector))
                {
                    model.SourceStarNegativeEmotionVector = GetCompactVectorString(root, "sourceStarNegativeEmotionVector", "sourceStarNegativeEmotionalEffectVector", "source_star_negative_emotion_vector", "source_star_negative_emotional_effect_vector") ?? string.Empty;
                }
                AddDynamicInterstarEmotionVectorAliases(root, model);
            }
            catch
            {
            }
        }

        private static void EnsureSourceStarVectorsFromInterstarAliases(StarInteractionResponse response, string sourceStarId)
        {
            if (response == null || string.IsNullOrWhiteSpace(sourceStarId)) return;
            response.InterstarEmotionVectors ??= new List<InterstarEmotionVectorResult>();
            InterstarEmotionVectorResult? item = (response.InterstarEmotionVectors ?? new List<InterstarEmotionVectorResult>())
                .LastOrDefault(x => x != null && string.Equals(x.StarId, sourceStarId, StringComparison.OrdinalIgnoreCase));
            if (item == null && response.InterstarEmotionVectors != null)
            {
                // Some local models name the flat vector after the reacting/owning Star (for example
                // kaeliaEmotionalEffectVector) even though the relationship target is the source Star.
                // If that is the only usable Star-to-Star vector, treat it as the source-Star vector so
                // the ripple still becomes durable instead of disappearing.
                List<InterstarEmotionVectorResult> usable = response.InterstarEmotionVectors
                    .Where(x => x != null && (!string.IsNullOrWhiteSpace(x.PositiveEmotionVector) || !string.IsNullOrWhiteSpace(x.NegativeEmotionVector)))
                    .ToList();
                if (usable.Count == 1) item = usable[0];
            }

            if ((string.IsNullOrWhiteSpace(response.SourceStarEmotionalEffectVector) || IsZeroCompactVector(response.SourceStarEmotionalEffectVector)) && !string.IsNullOrWhiteSpace(item?.PositiveEmotionVector) && !IsZeroCompactVector(item.PositiveEmotionVector))
            {
                response.SourceStarEmotionalEffectVector = item.PositiveEmotionVector;
            }
            if ((string.IsNullOrWhiteSpace(response.SourceStarNegativeEmotionVector) || IsZeroCompactVector(response.SourceStarNegativeEmotionVector)) && !string.IsNullOrWhiteSpace(item?.NegativeEmotionVector) && !IsZeroCompactVector(item.NegativeEmotionVector))
            {
                response.SourceStarNegativeEmotionVector = item.NegativeEmotionVector;
            }

            if (!string.IsNullOrWhiteSpace(response.SourceStarEmotionalEffectVector) || !string.IsNullOrWhiteSpace(response.SourceStarNegativeEmotionVector))
            {
                if (item == null)
                {
                    item = new InterstarEmotionVectorResult { StarId = sourceStarId, Reason = "source Star vector fields normalized into durable interstar vector" };
                    response.InterstarEmotionVectors.Add(item);
                }
                if (string.IsNullOrWhiteSpace(item.PositiveEmotionVector) || IsZeroCompactVector(item.PositiveEmotionVector)) item.PositiveEmotionVector = response.SourceStarEmotionalEffectVector ?? string.Empty;
                if (string.IsNullOrWhiteSpace(item.NegativeEmotionVector) || IsZeroCompactVector(item.NegativeEmotionVector)) item.NegativeEmotionVector = response.SourceStarNegativeEmotionVector ?? string.Empty;
            }
        }

        private static bool IsZeroCompactVector(string? vector)
        {
            if (string.IsNullOrWhiteSpace(vector)) return true;
            string[] parts = vector.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return true;
            foreach (string raw in parts)
            {
                string[] pair = raw.Trim().Split(':');
                if (pair.Length < 2) continue;
                double.TryParse(pair[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double confidence);
                double.TryParse(pair[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double score);
                if (confidence > 0 || score > 0) return false;
            }
            return true;
        }

        private static string? GetCompactVectorString(JsonElement root, params string[] names)
        {
            foreach (string name in names ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!root.TryGetProperty(name, out JsonElement value)) continue;
                string text = ExtractVectorText(value);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            return null;
        }

        private static string ExtractVectorText(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (string name in new[] { "vector", "value", "text", "emotionalEffectVector", "positiveEmotionVector", "negativeEmotionVector" })
                {
                    if (value.TryGetProperty(name, out JsonElement nested) && nested.ValueKind == JsonValueKind.String)
                    {
                        string text = nested.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
            }
            return string.Empty;
        }

        private static void AddDynamicInterstarEmotionVectorAliases(JsonElement root, StarStructuredModelResponse model)
        {
            if (root.ValueKind != JsonValueKind.Object) return;
            model.InterstarEmotionVectors ??= new List<InterstarEmotionVectorResult>();
            Dictionary<string, InterstarEmotionVectorResult> byStar = model.InterstarEmotionVectors
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.StarId))
                .GroupBy(x => x.StarId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

            foreach (JsonProperty property in root.EnumerateObject())
            {
                string name = property.Name ?? string.Empty;
                if (ShouldIgnoreVectorAliasName(name)) continue;

                string starId = string.Empty;
                bool isPositive = false;
                bool isNegative = false;

                if (TryStripVectorSuffix(name, "PositiveEmotionVector", out starId)) isPositive = true;
                else if (TryStripVectorSuffix(name, "EmotionalEffectVector", out starId)) isPositive = true;
                else if (TryStripVectorSuffix(name, "NegativeEmotionalEffectVector", out starId)) isNegative = true;
                else if (TryStripVectorSuffix(name, "NegativeEmotionVector", out starId)) isNegative = true;

                if (!isPositive && !isNegative) continue;
                starId = CleanOneLine(starId, 80);
                if (string.IsNullOrWhiteSpace(starId)) continue;

                if (!byStar.TryGetValue(starId, out InterstarEmotionVectorResult? item))
                {
                    item = new InterstarEmotionVectorResult { StarId = starId, Reason = "flat per-Star vector alias from JSON property " + name };
                    byStar[starId] = item;
                    model.InterstarEmotionVectors.Add(item);
                }

                string vector = ExtractVectorText(property.Value);
                if (isPositive) item.PositiveEmotionVector = vector;
                else item.NegativeEmotionVector = vector;
            }
        }

        private static bool ShouldIgnoreVectorAliasName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            if (name.Equals("positiveEmotionVector", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals("negativeEmotionVector", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals("emotionalEffectVector", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals("negativeEmotionalEffectVector", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("sourceStar", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("lastCat", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool TryStripVectorSuffix(string name, string suffix, out string prefix)
        {
            prefix = string.Empty;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(suffix)) return false;
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
            prefix = name.Substring(0, name.Length - suffix.Length);
            return !string.IsNullOrWhiteSpace(prefix);
        }

        private static string FirstJsonString(JsonElement root, params string[][] paths)
        {
            foreach (string[] path in paths ?? Array.Empty<string[]>())
            {
                string? value = GetStringPath(root, path);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return string.Empty;
        }

        private static List<string> FirstJsonStringList(JsonElement root, params string[][] paths)
        {
            foreach (string[] path in paths ?? Array.Empty<string[]>())
            {
                List<string> values = GetStringListPath(root, path);
                if (values.Count > 0) return values;
            }
            return new List<string>();
        }

        private static string? GetStringPath(JsonElement root, params string[] path)
        {
            if (path == null || path.Length == 0) return null;
            JsonElement current = root;
            foreach (string name in path)
            {
                if (current.ValueKind != JsonValueKind.Object) return null;
                bool found = false;
                foreach (JsonProperty property in current.EnumerateObject())
                {
                    if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    current = property.Value;
                    found = true;
                    break;
                }
                if (!found) return null;
            }
            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.ToString(),
                _ => null
            };
        }

        private static List<string> GetStringListPath(JsonElement root, params string[] path)
        {
            List<string> values = new List<string>();
            if (path == null || path.Length == 0) return values;
            JsonElement current = root;
            foreach (string name in path)
            {
                if (current.ValueKind != JsonValueKind.Object) return values;
                bool found = false;
                foreach (JsonProperty property in current.EnumerateObject())
                {
                    if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    current = property.Value;
                    found = true;
                    break;
                }
                if (!found) return values;
            }
            if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in current.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) AddClean(values, item.GetString());
                    else if (item.ValueKind == JsonValueKind.Number || item.ValueKind == JsonValueKind.True || item.ValueKind == JsonValueKind.False) AddClean(values, item.ToString());
                }
            }
            else if (current.ValueKind == JsonValueKind.String)
            {
                AddClean(values, current.GetString());
            }
            return values;
        }

        private static string? GetString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
                _ => null
            };
        }

        private static List<string> GetStringOrArray(JsonElement root, string propertyName)
        {
            List<string> parts = new List<string>();
            if (!root.TryGetProperty(propertyName, out JsonElement value)) return parts;
            if (value.ValueKind == JsonValueKind.String)
            {
                AddClean(parts, value.GetString());
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) AddClean(parts, item.GetString());
                }
            }
            return parts;
        }

        private static List<string> ExtractLikelySpokenParts(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            string text = raw.Trim();
            foreach (string property in new[] { "spokenText", "spoken_text", "reply", "response", "message", "text" })
            {
                List<string> array = ExtractJsonStringArrayProperty(text, property);
                if (array.Count > 0) return CleanSpeechParts(array);
                string? value = ExtractJsonStringProperty(text, property);
                if (!string.IsNullOrWhiteSpace(value)) return CleanSpeechParts(new[] { CleanRawTextForSpeech(value) });
            }
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNewLine = text.IndexOf('\n');
                int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (firstNewLine >= 0 && lastFence > firstNewLine) text = text.Substring(firstNewLine + 1, lastFence - firstNewLine - 1).Trim();
            }
            if (text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal)) return new List<string>();
            return CleanSpeechParts(new[] { CleanRawTextForSpeech(text) });
        }

        private static List<string> ExtractJsonStringArrayProperty(string text, string propertyName)
        {
            List<string> parts = new List<string>();
            string pattern = "\\\"" + Regex.Escape(propertyName) + "\\\"\\s*:\\s*\\[(?<value>.*?)\\]";
            Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) return parts;
            string arrayText = "[" + match.Groups["value"].Value + "]";
            try
            {
                List<string>? values = JsonSerializer.Deserialize<List<string>>(arrayText);
                if (values != null) parts.AddRange(values);
            }
            catch
            {
            }
            return parts;
        }

        private static string? ExtractJsonStringProperty(string text, string propertyName)
        {
            string pattern = "\\\"" + Regex.Escape(propertyName) + "\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)";
            Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) return null;
            string escaped = match.Groups["value"].Value;
            try { return JsonSerializer.Deserialize<string>("\"" + escaped + "\""); }
            catch { return escaped.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\"); }
        }

        private static string CleanRawTextForSpeech(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string text = value.Trim();
            text = Regex.Replace(text, "^\\s*(assistant|star|spokenText|spoken_text|reply|response)\\s*[:：]\\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
            text = text.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("```", string.Empty).Trim();
            return text;
        }

        private static void AddClean(List<string> parts, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string cleaned = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (cleaned.Length > 0) parts.Add(cleaned);
        }

        private static void TrimEmotionScoresToImpactfulSet(StarInteractionResponse response, int maxCount)
        {
            if (response.EmotionScores == null || response.EmotionScores.Count <= maxCount)
            {
                return;
            }

            response.EmotionScores = response.EmotionScores
                .Where(x => x.Value != null && !string.IsNullOrWhiteSpace(x.Key))
                .GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.Value.Score * Math.Max(0.01, x.Value.Confidence)).First())
                .OrderByDescending(x => x.Value.Score * Math.Max(0.01, x.Value.Confidence))
                .Take(Math.Max(1, maxCount))
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        }
    }
}
