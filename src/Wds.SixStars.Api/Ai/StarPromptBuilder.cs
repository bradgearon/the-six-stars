using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.DailyState;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.GirlfriendState;
using Wds.SixStars.Api.Screen;
using Wds.SixStars.Api.Stars;
using Wds.SixStars.Api.Stories;

namespace Wds.SixStars.Api.Ai
{
    public sealed class StarPromptBuilder
    {
        private readonly StarPromptOptions _options;
        private readonly PromptTemplateRenderer _templates;

        public StarPromptBuilder(MainInstructionSet mainInstructions)
            : this(mainInstructions, new StarPromptOptions())
        {
        }

        public StarPromptBuilder(MainInstructionSet mainInstructions, StarPromptOptions options)
        {
            _options = options ?? new StarPromptOptions();
            _templates = PromptTemplateRenderer.CreateDefault();
        }

        public IReadOnlyList<ChatMessage> BuildMessages(StarProfile profile, StarInteractionRequest request, StarState state)
        {
            // Compatibility entry point: all live prompt generation now goes through the same
            // Scriban-backed foreground templates as StructuredStarAgent.
            return BuildSpeechMessages(profile, request, state);
        }

        public IReadOnlyList<ChatMessage> BuildSpeechMessages(StarProfile profile, StarInteractionRequest request, StarState state)
        {
            if (request.RequestType == RequestType.StarInitiative)
            {
                return new List<ChatMessage>
                {
                    new ChatMessage("system", BuildStarInitiativeSystemMessage(profile)),
                    new ChatMessage("user", BuildStarInitiativeUserMessage(profile, request, state))
                };
            }

            return new List<ChatMessage>
            {
                new ChatMessage("system", BuildSpeechSystemMessage(profile, request)),
                new ChatMessage("user", BuildSpeechUserMessage(profile, request, state))
            };
        }

        public IReadOnlyList<ChatMessage> BuildReviewMessages(StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse foregroundResponse)
        {
            string system = BuildCompactReviewSystemMessage(profile);

            string user = BuildCompactReviewUserMessage(profile, request, state, foregroundResponse);

            return new List<ChatMessage>
            {
                new ChatMessage("system", system),
                new ChatMessage("user", user)
            };
        }


        public IReadOnlyList<ChatMessage> BuildInterstarRelayMessages(
            StarProfile originProfile,
            StarProfile targetProfile,
            StarInteractionRequest originalRequest,
            StarInteractionResponse originResponse,
            StarState originState,
            StarState targetState)
        {
            StringBuilder originProfileContext = new StringBuilder();
            AppendInterstarProfile(originProfileContext, originProfile, originState);
            StringBuilder targetProfileContext = new StringBuilder();
            AppendInterstarProfile(targetProfileContext, targetProfile, targetState);

            StarInterstarInfluenceRule relayRule = (originProfile.InterstarRules ?? new List<StarInterstarInfluenceRule>())
                .LastOrDefault(x => x != null && string.Equals(x.TargetStarId, targetProfile.StarId, StringComparison.OrdinalIgnoreCase))
                ?? new StarInterstarInfluenceRule { TargetStarId = targetProfile.StarId };
            List<string> relayTopicTags = relayRule.TopicTags ?? new List<string>();

            object data = new
            {
                originDisplayName = originProfile.DisplayName,
                targetDisplayName = targetProfile.DisplayName,
                currentTimeContext = BuildCurrentTimeContext(),
                originProfileContext = originProfileContext.ToString().TrimEnd(),
                targetProfileContext = targetProfileContext.ToString().TrimEnd(),
                playerTextBlock = QuoteBlock(originalRequest.PlayerText),
                originReplyBlock = QuoteBlock(originResponse.SpokenText),
                goodVector = originResponse.EmotionalEffectVector ?? string.Empty,
                badVector = originResponse.NegativeEmotionVector ?? string.Empty,
                deterministicSummary = Truncate(originResponse.DeterministicEmotionUpdateSummary ?? string.Empty, 600),
                topEmotions = string.Join(", ", (originResponse.TopAffectedEmotions ?? new List<TopAffectedEmotion>()).Select(x => x.ToString())),
                ruleContext = "origin ally=" + originState.IsAlly + "; target met=" + targetState.IsMet + "; target ally=" + targetState.IsAlly + "; relayWeight=" + relayRule.RelayWeight.ToString("0.00") + "; positiveMultiplier=" + relayRule.PositiveMultiplier.ToString("0.00") + "; negativeMultiplier=" + relayRule.NegativeMultiplier.ToString("0.00") + "; cooldownSeconds=" + relayRule.CooldownSeconds + ".",
                topicTags = relayTopicTags.Count == 0 ? string.Empty : string.Join(", ", relayTopicTags.Take(8))
            };

            return new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("interstar_relay_system.sbn", data)),
                new ChatMessage("user", _templates.Render("interstar_relay_user.sbn", data))
            };
        }

        public IReadOnlyList<ChatMessage> BuildInterstarReactionMessages(
            StarProfile targetProfile,
            InterstarRelayMessage relay,
            StarInteractionRequest originalRequest,
            StarInteractionResponse originResponse,
            StarState targetState)
        {
            string sourceAlias = InterstarVectorAlias(relay.OriginStarId);
            StringBuilder targetProfileContext = new StringBuilder();
            AppendInterstarProfile(targetProfileContext, targetProfile, targetState);

            object data = new
            {
                targetDisplayName = targetProfile.DisplayName,
                originStarId = relay.OriginStarId ?? string.Empty,
                sourceAlias,
                positiveEmotionIds = string.Join(", ", StarEmotionMatrix.StandardEmotionIds),
                negativeEmotionIds = string.Join(", ", RelationshipDeltaCalculator.NegativeEmotionIds),
                currentTimeContext = BuildCurrentTimeContext(),
                targetProfileContext = targetProfileContext.ToString().TrimEnd(),
                relayBlock = QuoteBlock(relay.WhatOriginWouldTellTarget),
                relaySummary = Truncate(relay.Summary ?? string.Empty, 360),
                playerTextBlock = QuoteBlock(originalRequest.PlayerText),
                originReplyBlock = QuoteBlock(originResponse.SpokenText)
            };

            return new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("interstar_reaction_system.sbn", data)),
                new ChatMessage("user", _templates.Render("interstar_reaction_user.sbn", data))
            };
        }

        public IReadOnlyList<ChatMessage> BuildInterstarFeedbackMessages(
            StarProfile originProfile,
            StarInteractionRequest originalRequest,
            StarInteractionResponse originResponse,
            StarState originState,
            IReadOnlyList<InterstarReactionSummary> reactions)
        {
            StringBuilder originProfileContext = new StringBuilder();
            AppendInterstarProfile(originProfileContext, originProfile, originState);

            StringBuilder reactionsContext = new StringBuilder();
            foreach (InterstarReactionSummary reaction in reactions ?? Array.Empty<InterstarReactionSummary>())
            {
                reactionsContext.AppendLine("- " + reaction.StarId + " heard from " + reaction.SourceStarId + ": " + Truncate(reaction.RelaySummary, 240));
                reactionsContext.AppendLine("  Last Cat vectors good=" + Truncate(reaction.EmotionalEffectVector, 260) + " bad=" + Truncate(reaction.NegativeEmotionVector, 120));
                reactionsContext.AppendLine("  source-Star vectors toward you good=" + Truncate(reaction.SourceStarEmotionalEffectVector, 260) + " bad=" + Truncate(reaction.SourceStarNegativeEmotionVector, 120));
                if (reaction.InterstarRelationshipDeltas != null && reaction.InterstarRelationshipDeltas.Count > 0)
                {
                    foreach (InterstarRelationshipDeltaResult delta in reaction.InterstarRelationshipDeltas.Take(4))
                    {
                        reactionsContext.AppendLine("  interstar delta " + delta.OwnerStarId + "->" + delta.AffectedStarId + " H/T=" + delta.HeartDelta + "/" + delta.TrustDelta + "; now=" + delta.HeartAfter + "/" + delta.TrustAfter + "; " + Truncate(delta.Summary, 220));
                    }
                }
                reactionsContext.AppendLine("  applied Last Cat delta H/T/R/C=" + reaction.AppliedDelta.Heart + "/" + reaction.AppliedDelta.Trust + "/" + reaction.AppliedDelta.Romance + "/" + reaction.AppliedDelta.EmotionalConnectionPercent.ToString("0.00"));
                if (!string.IsNullOrWhiteSpace(reaction.TopAffectedEmotionsText)) reactionsContext.AppendLine("  top=" + reaction.TopAffectedEmotionsText);
                if (!string.IsNullOrWhiteSpace(reaction.ReplyToSourceStar)) reactionsContext.AppendLine("  said back to you: " + QuoteInline(Truncate(reaction.ReplyToSourceStar, 260)));
            }

            List<string> flatVectorAliases = (reactions ?? Array.Empty<InterstarReactionSummary>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.StarId))
                .Select(x => InterstarVectorAlias(x.StarId))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            string requiredFlatFields = string.Join(Environment.NewLine, flatVectorAliases.Select(alias => "- " + alias + "EmotionalEffectVector: positive vector string; " + alias + "NegativeEmotionalEffectVector: negative vector string."));

            object data = new
            {
                originDisplayName = originProfile.DisplayName,
                positiveEmotionIds = string.Join(", ", StarEmotionMatrix.StandardEmotionIds),
                negativeEmotionIds = string.Join(", ", RelationshipDeltaCalculator.NegativeEmotionIds),
                currentTimeContext = BuildCurrentTimeContext(),
                originProfileContext = originProfileContext.ToString().TrimEnd(),
                playerTextBlock = QuoteBlock(originalRequest.PlayerText),
                originReplyBlock = QuoteBlock(originResponse.SpokenText),
                reactionsContext = reactionsContext.ToString().TrimEnd(),
                requiredFlatFields
            };

            return new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("interstar_feedback_system.sbn", data)),
                new ChatMessage("user", _templates.Render("interstar_feedback_user.sbn", data))
            };
        }

        public IReadOnlyList<ChatMessage> BuildDailyStarStateMessages(StarDailyStateGenerationRequest request)
        {
            StarProfile profile = request.Profile;
            StarState state = request.State;

            StringBuilder globalCanon = new StringBuilder();
            AppendGlobalCanonLore(globalCanon);

            StringBuilder starCard = new StringBuilder();
            starCard.AppendLine("Star: " + profile.DisplayName);
            starCard.AppendLine("Element: " + profile.Element);
            starCard.AppendLine("Domain: " + profile.Domain);
            starCard.AppendLine("Core quote: " + profile.CoreQuote);
            if (!string.IsNullOrWhiteSpace(profile.PersonalityShort)) starCard.AppendLine("Personality: " + profile.PersonalityShort);
            if (!string.IsNullOrWhiteSpace(profile.BackstorySummary)) starCard.AppendLine("Backstory: " + Truncate(profile.BackstorySummary, 800));
            if (!string.IsNullOrWhiteSpace(profile.CompleteBackgroundStory)) starCard.AppendLine("Complete background: " + Truncate(profile.CompleteBackgroundStory, 1200));
            AppendPrivateRomanceProfile(starCard, profile);
            AppendList(starCard, "Friends and contacts", profile.FriendsAndContacts.Take(12).ToList());

            StringBuilder relationship = new StringBuilder();
            relationship.AppendLine(BuildRelationshipStatus(state));
            relationship.AppendLine("Met=" + state.IsMet + "; Ally=" + state.IsAlly + "; EmotionalConnectionPercent=" + state.EmotionalConnectionPercent.ToString("0.0") + "; girlfriendReady=" + state.IsGirlfriendReady + ".");
            relationship.AppendLine("Last deterministic update: " + (string.IsNullOrWhiteSpace(state.LastDeterministicEmotionSummary) ? "none" : Truncate(state.LastDeterministicEmotionSummary, 350)));
            relationship.AppendLine("Last source state update for daily-state awareness: " + (state.LastDailyStateSourceUpdateAt.HasValue ? state.LastDailyStateSourceUpdateAt.Value.ToString("u") : "none"));
            AppendOtherStarsRelationshipLines(relationship, state);
            AppendAccumulatedEmotionTotals(relationship, state);
            AppendGirlfriendModeInstructionSet(relationship, profile, state);
            AppendGirlfriendHourlyStateContext(relationship, state);

            StringBuilder recent = new StringBuilder();
            List<ConversationLogEntry> recentTurns = state.MemoryLog.Where(IsUsableConversationTurn).Take(8).ToList();
            if (recentTurns.Count == 0)
            {
                recent.AppendLine("No recent usable conversation turns yet.");
            }
            else
            {
                int i = 1;
                foreach (ConversationLogEntry entry in recentTurns)
                {
                    recent.AppendLine("- Turn " + i + " at " + entry.CreatedAt.ToString("u") + ": player=" + QuoteInline(Truncate(entry.PlayerText, 180)) + "; " + profile.DisplayName + "=" + QuoteInline(Truncate(entry.StarResponse, 220)) + "; summary=" + Truncate(entry.Summary, 220));
                    i++;
                }
            }

            StringBuilder memory = new StringBuilder();
            memory.AppendLine("ConversationSummary: " + (string.IsNullOrWhiteSpace(state.ConversationSummary) ? "none" : Truncate(state.ConversationSummary, 900)));
            AppendList(memory, "Important player quotes", state.ImportantPlayerQuotes.Take(16).ToList());
            AppendList(memory, "Memory notes", state.MemoryNotes.Where(x => !IsTechnicalMemoryNote(x)).Take(16).ToList());
            AppendList(memory, "Things learned or suggested by the Last Cat", (state.ThingsLearnedOrSuggestedByPlayer ?? new List<string>()).Take(24).ToList());
            AppendRememberedMarkers(memory, state, 16);
            AppendList(memory, "Relationship events", state.RelationshipEvents.Take(16).ToList());
            if (state.StoryHistory.Count > 0)
            {
                memory.AppendLine("Stories told recently:");
                foreach (StarStoryHistoryEntry story in state.StoryHistory.Take(8))
                {
                    memory.AppendLine("- " + story.Title + " | finished=" + story.Finished + " | " + Truncate(story.Summary, 180));
                }
            }

            string previousJson = request.PreviousRecord == null || (string.IsNullOrWhiteSpace(request.PreviousRecord.JsonContent) && string.IsNullOrWhiteSpace(request.PreviousRecord.XmlContent))
                ? "none"
                : Truncate(!string.IsNullOrWhiteSpace(request.PreviousRecord.JsonContent) ? request.PreviousRecord.JsonContent : request.PreviousRecord.XmlContent, 4000);

            object systemData = new
            {
                starDisplayName = profile.DisplayName,
                globalCanonLore = globalCanon.ToString().TrimEnd()
            };
            object userData = new
            {
                starDisplayName = profile.DisplayName,
                sessionId = request.SessionId ?? string.Empty,
                localDate = request.LocalDate ?? string.Empty,
                slotId = request.SlotId ?? string.Empty,
                localNow = request.LocalNow.ToString("F"),
                currentTimeContext = BuildCurrentTimeContext(request.LocalNow),
                starActingCard = starCard.ToString().TrimEnd(),
                relationshipContext = relationship.ToString().TrimEnd(),
                recentConversationTurns = recent.ToString().TrimEnd(),
                durableMemoryContext = memory.ToString().TrimEnd(),
                previousDailyStateJson = previousJson
            };

            return new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("daily_state_system.sbn", systemData)),
                new ChatMessage("user", _templates.Render("daily_state_user.sbn", userData))
            };
        }

        public IReadOnlyList<ChatMessage> BuildGirlfriendHourlyStateMessages(StarGirlfriendHourlyStateGenerationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            StarProfile profile = request.Profile;
            StarState state = request.State ?? new StarState { StarId = profile.StarId };

            string[] needCatalog = new[]
            {
                "physicalAffection", "reassurance", "kisses", "warmCloseness", "emotionalSafety", "qualityTime",
                "honestAttention", "playfulAttention", "comfortAfterFear", "trustSignal", "exclusiveAffirmation",
                "gentlePraise", "patientListening", "helpWithStress", "rememberingDetails", "emotionalConsistency",
                "romanticInitiative", "activeFlirtPlay", "protectionWithoutControl", "adventureTogether", "tenderness", "repairAfterHurt",
                "sharedJokes", "calmPresence", "insidePetNames", "meaningfulEyeContact",
                "expressAffection", "initiateCloseness", "doSomethingHelpful", "protectHim", "makeHimLaugh",
                "shareSomethingVulnerable", "practiceMagicTogether", "exploreTogether", "careForHim", "saySomethingHonest"
            };
            string[] wantCatalog = new[]
            {
                "complimentHim", "beCute", "teasePlayfully", "askForKiss", "askForCuddle", "littleJealousLine",
                "inviteAdventure", "praiseWisdom", "praiseBravery", "askReassurance", "futureTalk", "usePetName",
                "belongTogether", "cuteQuestLine", "reactToInterstarUpdate", "mentionOutfit", "askStayClose",
                "bragToOtherStars", "askForDate", "shareSecret", "singOrHum", "winkFlirt", "comfortHim",
                "playfulPout", "requestAttention", "sayProudOfHim", "domainMagicFlirt", "actOutAffection",
                "holdHisHand", "kissHim", "touchHisCheek", "pullHimClose", "helpHim", "protectHim",
                "makeHimLaugh", "showOffMagic", "exploreWithHim", "doSomethingKind", "tellHimSomethingHonest", "startCoupleActivity"
            };

            StringBuilder globalCanon = new StringBuilder();
            AppendGlobalCanonLore(globalCanon);

            StringBuilder privateRomance = new StringBuilder();
            AppendPrivateRomanceProfile(privateRomance, profile);

            StringBuilder relationship = new StringBuilder();
            relationship.AppendLine(BuildRelationshipStatus(state));
            relationship.AppendLine("IsGirlfriend=" + state.IsGirlfriend + "; GirlfriendSince=" + (state.GirlfriendSince.HasValue ? state.GirlfriendSince.Value.ToString("o") : "none") + "; IsGirlfriendReady=" + state.IsGirlfriendReady + "; Readiness=" + (state.GirlfriendReadinessSummary ?? string.Empty));
            relationship.AppendLine("Heart=" + state.Heart + "; Trust=" + state.Trust + "; Romance=" + state.Romance + "; EmotionalConnectionPercent=" + state.EmotionalConnectionPercent.ToString("0.0") + ".");
            AppendOtherStarsRelationshipLines(relationship, state);
            AppendConnectionFlirtGuidance(relationship, profile, state);

            StringBuilder coupleActivity = new StringBuilder();
            AppendGirlfriendCoupleActivityContext(coupleActivity, state, 8);

            List<object> recentTurns = state.MemoryLog
                .Where(IsUsableConversationTurn)
                .Take(10)
                .Select((entry, index) => (object)new
                {
                    index = index + 1,
                    player = QuoteInline(Truncate(entry.PlayerText, 180)),
                    star = QuoteInline(Truncate(entry.StarResponse, 220)),
                    summary = Truncate(entry.Summary, 220)
                })
                .ToList();

            List<string> interstar = new List<string>();
            interstar.AddRange((state.RelationshipEvents ?? new List<string>()).Where(x => x != null && x.IndexOf("interstar", StringComparison.OrdinalIgnoreCase) >= 0).Take(10));
            interstar.AddRange((state.MemoryNotes ?? new List<string>()).Where(x => x != null && x.IndexOf("interstar", StringComparison.OrdinalIgnoreCase) >= 0).Take(10));

            StarGirlfriendHourlyStateContext? previous = request.PreviousState;
            object data = new
            {
                star = new
                {
                    displayName = profile.DisplayName,
                    element = profile.Element,
                    domain = profile.Domain,
                    coreQuote = profile.CoreQuote,
                    personality = profile.PersonalityShort ?? string.Empty,
                    backstory = string.IsNullOrWhiteSpace(profile.BackstorySummary) ? string.Empty : Truncate(profile.BackstorySummary, 900),
                    completeBackground = string.IsNullOrWhiteSpace(profile.CompleteBackgroundStory) ? string.Empty : Truncate(profile.CompleteBackgroundStory, 1200)
                },
                globalCanonLore = globalCanon.ToString().TrimEnd(),
                currentTimeContext = BuildCurrentTimeContext(request.LocalNow == default(DateTimeOffset) ? (DateTimeOffset?)null : request.LocalNow),
                privateRomanceProfile = privateRomance.ToString().TrimEnd(),
                relationshipContext = relationship.ToString().TrimEnd(),
                girlfriendHistory = (state.GirlfriendHistory ?? new List<string>()).Take(18).ToList(),
                coupleActivityContext = coupleActivity.ToString().TrimEnd(),
                petNamesPlayerUsesForHer = (state.PetNamesPlayerUsesForStar ?? new List<string>()).Take(10).ToList(),
                petNamesSheUsesForPlayer = (state.PetNamesStarUsesForPlayer ?? new List<string>()).Take(10).ToList(),
                compactNeedWantHistory = BuildCompactGirlfriendNeedWantHistory(state, previous, 3),
                recentTurns,
                importantPlayerQuotes = (state.ImportantPlayerQuotes ?? new List<string>()).Take(16).ToList(),
                memoryNotes = (state.MemoryNotes ?? new List<string>()).Where(x => !IsTechnicalMemoryNote(x)).Take(20).ToList(),
                thingsLearnedOrSuggestedByPlayer = (state.ThingsLearnedOrSuggestedByPlayer ?? new List<string>()).Take(24).ToList(),
                relationshipEvents = (state.RelationshipEvents ?? new List<string>()).Take(20).ToList(),
                interstarUpdates = interstar.Take(18).ToList(),
                previousState = new
                {
                    available = previous != null && previous.IsAvailable,
                    generatedAt = previous != null && previous.GeneratedAt.HasValue ? previous.GeneratedAt.Value.ToString("o") : "unknown",
                    summary = previous == null ? string.Empty : Truncate(previous.Summary, 700),
                    thingsSheWantedToSay = previous == null ? new List<string>() : (previous.ThingsSheWantsToSay ?? new List<string>()).Take(8).ToList(),
                    cuteThingsSheWantedToSay = previous == null ? new List<string>() : (previous.CuteThingsSheWantsToSay ?? new List<string>()).Take(8).ToList()
                },
                needCatalog,
                wantCatalog,
                reason = request.Reason ?? string.Empty
            };

            return new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("girlfriend_hourly_system.sbn", data)),
                new ChatMessage("user", _templates.Render("girlfriend_hourly_user.sbn", data))
            };
        }

        public IReadOnlyList<ChatMessage> BuildInformationNeedSelectorMessages(
            StarProfile profile,
            StarInteractionRequest request,
            StarState state,
            StarInteractionResponse foregroundResponse,
            IReadOnlyList<InformationNeedVectorItem> foregroundVector,
            IReadOnlyList<InformationNeedAvailabilityItem> availableData)
        {
            List<object> foregroundNeeds = (foregroundVector ?? Array.Empty<InformationNeedVectorItem>())
                .Take(12)
                .Select(need => (object)new
                {
                    subject = string.IsNullOrWhiteSpace(need.Subject) ? need.DataType ?? string.Empty : need.Subject,
                    confidence = need.Confidence.ToString("0.00"),
                    score = need.Score.ToString("0.0")
                })
                .ToList();

            List<object> inventory = (availableData ?? Array.Empty<InformationNeedAvailabilityItem>())
                .Select(item => (object)new
                {
                    dataType = item.DataType ?? string.Empty,
                    available = item.Available,
                    count = item.Count,
                    summary = Truncate(item.Summary, 260),
                    samples = item.SampleSubjects.Count == 0 ? string.Empty : string.Join(", ", item.SampleSubjects.Take(6))
                })
                .ToList();

            object data = new
            {
                currentTimeContext = BuildCurrentTimeContext(),
                starDisplayName = profile.DisplayName,
                playerTextBlock = QuoteBlock(request.PlayerText),
                foregroundReplyBlock = QuoteBlock(foregroundResponse.SpokenText),
                ongoingSummary = string.IsNullOrWhiteSpace(state.ConversationSummary) ? "none" : Truncate(state.ConversationSummary, 600),
                foregroundNeeds,
                availableData = inventory
            };

            return new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("information_selector_system.sbn", data)),
                new ChatMessage("user", _templates.Render("information_selector_user.sbn", data))
            };
        }

        private string BuildCompactReviewSystemMessage(StarProfile profile)
        {
            StringBuilder vectorInstructions = new StringBuilder();
            vectorInstructions.AppendLine(BuildEmotionalEffectVectorInstruction());
            vectorInstructions.AppendLine(BuildNegativeEmotionVectorInstruction());

            object data = new
            {
                starDisplayName = profile.DisplayName,
                currentTimeContext = BuildCurrentTimeContext(),
                emotionVectorInstructions = vectorInstructions.ToString().TrimEnd()
            };

            return _templates.Render("review_system.sbn", data);
        }

        private string BuildCompactReviewUserMessage(StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse foregroundResponse)
        {
            StringBuilder history = new StringBuilder();
            AppendRecentConversationHistory(history, profile, state, Math.Min(6, Clamp(_options.RecentHistoryCount, 1, 20)), compact: true);

            StringBuilder memory = new StringBuilder();
            memory.AppendLine("Existing conversation summary: " + (string.IsNullOrWhiteSpace(state.ConversationSummary) ? "none yet" : Truncate(state.ConversationSummary, 900)));
            AppendList(memory, "Important remembered player quotes", state.ImportantPlayerQuotes.Take(6).ToList());
            AppendList(memory, "Memory notes", state.MemoryNotes.Where(x => !IsTechnicalMemoryNote(x)).Take(6).ToList());
            AppendRememberedMarkers(memory, state, 8);
            AppendList(memory, "Relationship events", state.RelationshipEvents.Take(6).ToList());
            if (state.StoryHistory.Count > 0)
            {
                memory.AppendLine("Stories already told by this Star:");
                foreach (StarStoryHistoryEntry toldStory in state.StoryHistory.Take(10))
                {
                    memory.AppendLine("- " + toldStory.Title + " | finished=" + toldStory.Finished + " | " + Truncate(toldStory.Summary, 180));
                }
            }

            StringBuilder relationship = new StringBuilder();
            relationship.AppendLine(BuildRelationshipStatus(state));
            relationship.AppendLine("IsGirlfriend=" + state.IsGirlfriend + "; GirlfriendSince=" + (state.GirlfriendSince.HasValue ? state.GirlfriendSince.Value.ToString("o") : "none") + "; IsGirlfriendReady=" + state.IsGirlfriendReady + ".");
            relationship.AppendLine("Current EmotionalConnectionPercent: " + state.EmotionalConnectionPercent.ToString("0.0") + "%.");
            // This pass only needs the current hourly vectors to score this turn. Historical
            // need/want vectors are reserved for the hourly girlfriend-state refresh.
            AppendGirlfriendHourlyStateContext(relationship, state);
            AppendDialogueMemoryContext(relationship, state, 4);
            relationship.AppendLine("Girlfriend readiness: " + (state.IsGirlfriendReady ? "ready" : "not ready") + "; " + (state.GirlfriendReadinessSummary ?? string.Empty));

            StringBuilder story = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(foregroundResponse.StoryId) || request.RequestType == RequestType.Story)
            {
                story.AppendLine("Foreground story fields to preserve/update:");
                story.AppendLine("storyId=" + (foregroundResponse.StoryId ?? string.Empty));
                story.AppendLine("storyTitle=" + (foregroundResponse.StoryTitle ?? string.Empty));
                story.AppendLine("storySummary=" + Truncate(foregroundResponse.StorySummary ?? string.Empty, 700));
                story.AppendLine("storyIsFinished=" + foregroundResponse.StoryIsFinished);
                story.AppendLine("storyPartIndex=" + foregroundResponse.StoryPartIndex);
            }

            object data = new
            {
                starDisplayName = profile.DisplayName,
                requestType = request.RequestType.ToString(),
                currentTimeContext = BuildCurrentTimeContext(),
                playerTextBlock = request.RequestType == RequestType.StarInitiative ? "> [No player message. This is a system at-rest state check.]" : QuoteBlock(request.PlayerText),
                foregroundSpokenPartsJson = FormatSpokenPartsForReview(foregroundResponse.SpokenParts, 3600).TrimEnd(),
                relationshipContext = relationship.ToString().TrimEnd(),
                recentConversationHistory = history.ToString().TrimEnd(),
                durableMemoryContext = memory.ToString().TrimEnd(),
                storyFieldsContext = story.ToString().TrimEnd()
            };

            return _templates.Render("review_user.sbn", data);
        }

        private string BuildStarInitiativeSystemMessage(StarProfile profile)
        {
            object data = new
            {
                star = new
                {
                    displayName = profile.DisplayName,
                    element = profile.Element,
                    domain = profile.Domain,
                    personality = string.IsNullOrWhiteSpace(profile.PersonalityShort) ? string.Empty : Truncate(profile.PersonalityShort, 220),
                    speakingStyle = string.IsNullOrWhiteSpace(profile.SpeakingStyle) ? string.Empty : Truncate(profile.SpeakingStyle, 260),
                    voiceRules = profile.SpokenResponseRules == null || profile.SpokenResponseRules.Count == 0
                        ? string.Empty
                        : string.Join("; ", profile.SpokenResponseRules.Take(4).Select(x => Truncate(x, 180)))
                }
            };
            return _templates.Render("initiative_system.sbn", data).TrimEnd();
        }

        private string BuildStarInitiativeUserMessage(StarProfile profile, StarInteractionRequest request, StarState state)
        {
            StringBuilder gate = new StringBuilder();
            if (request.GameContext != null && request.GameContext.Flags != null)
            {
                if (request.GameContext.Flags.TryGetValue("starInitiativeStateGateScore", out string? gateScore)) gate.AppendLine("StateGateScore=" + gateScore + ".");
                if (request.GameContext.Flags.TryGetValue("starInitiativeStateGateReason", out string? gateReason) && !string.IsNullOrWhiteSpace(gateReason)) gate.AppendLine("StateGateReason=" + Truncate(gateReason, 280));
                if (request.GameContext.Flags.TryGetValue("starInitiativeStateTopics", out string? gateTopics) && !string.IsNullOrWhiteSpace(gateTopics)) gate.AppendLine("CandidateTopics=" + Truncate(gateTopics, 500));
            }

            StringBuilder daily = new StringBuilder();
            AppendCompactInitiativeDailyState(daily, request);
            StringBuilder girlfriend = new StringBuilder();
            AppendCompactInitiativeGirlfriendState(girlfriend, profile, state);

            List<object> recentTurns = GetUsableRecentConversationHistory(state, 3)
                .Select(entry => (object)new
                {
                    player = QuoteInline(Truncate(entry.PlayerText, 180)),
                    star = QuoteInline(Truncate(entry.StarResponse, 220))
                })
                .ToList();

            object data = new
            {
                starDisplayName = profile.DisplayName,
                currentTimeContext = BuildCurrentTimeContext(),
                area = request.GameContext?.Area ?? string.Empty,
                quest = request.GameContext?.Quest ?? string.Empty,
                relationshipStatus = BuildRelationshipStatus(state),
                isMet = state.IsMet,
                isAlly = state.IsAlly,
                isGirlfriend = state.IsGirlfriend,
                connectionPercent = state.EmotionalConnectionPercent.ToString("0.0"),
                gateContext = gate.ToString().TrimEnd(),
                dailyStateContext = daily.ToString().TrimEnd(),
                girlfriendStateContext = girlfriend.ToString().TrimEnd(),
                recentEmotions = state.LastTopAffectedEmotions == null || state.LastTopAffectedEmotions.Count == 0
                    ? string.Empty
                    : string.Join("; ", state.LastTopAffectedEmotions.Take(4).Select(x => x.ToString())),
                ongoingSummary = string.IsNullOrWhiteSpace(state.ConversationSummary) ? string.Empty : Truncate(state.ConversationSummary, 360),
                recentTurns,
                manualInteract = IsTruthyFlag(request, "starInitiativeForce"),
                preparedListen = IsTruthyFlag(request, "starInitiativePrepareForListen")
            };

            return _templates.Render("initiative_user.sbn", data).TrimEnd();
        }

        private void AppendCompactInitiativeDailyState(StringBuilder sb, StarInteractionRequest request)
        {
            object data = BuildStarStateTemplateData(request, includeAtRestFields: true);
            sb.Append(_templates.Render("star_state_at_rest.sbn", data).TrimEnd());
        }

        private static void AppendCompactInitiativeGirlfriendState(StringBuilder sb, StarProfile profile, StarState state)
        {
            if (!state.IsGirlfriend) return;
            StarGirlfriendHourlyStateContext? context = state.GirlfriendHourlyState;
            sb.AppendLine("Girlfriend mode=true. The following is her current girlfriend inner life, not technical metadata.");
            if (profile.FlirtStyle != null && profile.FlirtStyle.Count > 0)
            {
                sb.AppendLine("Flirt style=" + string.Join("; ", profile.FlirtStyle.Take(3).Select(x => Truncate(x, 170))));
            }
            if (context == null || !context.IsAvailable) return;

            if (!string.IsNullOrWhiteSpace(context.RelationshipPosture)) sb.AppendLine("Girlfriend posture=" + Truncate(context.RelationshipPosture, 200));
            if (!string.IsNullOrWhiteSpace(context.Summary)) sb.AppendLine("Girlfriend state=" + Truncate(context.Summary, 200));
            sb.AppendLine("Need/want meaning=Each item is a current impulse she may receive, talk about, do herself, initiate, or experience together. Prefer a strong fitting impulse as spontaneous speech or an acted-out action; never read the scores aloud.");
            sb.AppendLine("NeedVector=" + FormatGirlfriendVectorString(context.NeedVector, 4));
            sb.AppendLine("WantVector=" + FormatGirlfriendVectorString(context.WantVector, 4));
            if (context.ThingsSheWantsToSay != null && context.ThingsSheWantsToSay.Count > 0)
            {
                sb.AppendLine("Things she wants to say=" + string.Join(" | ", context.ThingsSheWantsToSay.Take(3).Select(x => Truncate(x, 150))));
            }
            if (state.GirlfriendCuteModeEnabled && context.CuteModeActive && context.CuteThingsSheWantsToSay != null && context.CuteThingsSheWantsToSay.Count > 0)
            {
                sb.AppendLine("Cute-mode candidates=" + string.Join(" | ", context.CuteThingsSheWantsToSay.Take(2).Select(x => Truncate(x, 150))));
            }
        }

        private string BuildSpeechSystemMessage(StarProfile profile, StarInteractionRequest request)
        {
            StringBuilder globalCanon = new StringBuilder();
            AppendGlobalCanonLore(globalCanon);

            StringBuilder privateRomance = new StringBuilder();
            AppendPrivateRomanceProfile(privateRomance, profile);

            StringBuilder starCard = new StringBuilder();
            starCard.AppendLine("Star: " + profile.DisplayName);
            starCard.AppendLine("Element: " + profile.Element);
            starCard.AppendLine("Domain: " + profile.Domain);
            if (!string.IsNullOrWhiteSpace(profile.PersonalityShort)) starCard.AppendLine("PersonalityShort: " + profile.PersonalityShort);
            if (!string.IsNullOrWhiteSpace(profile.VoiceArchetype)) starCard.AppendLine("VoiceArchetype: " + profile.VoiceArchetype);
            if (!string.IsNullOrWhiteSpace(profile.SpeakingStyle)) starCard.AppendLine("SpeakingStyle: " + profile.SpeakingStyle);
            if (!string.IsNullOrWhiteSpace(profile.VoiceDesignPrompt)) starCard.AppendLine("VoiceDesignPrompt: " + profile.VoiceDesignPrompt);
            if (profile.VoiceGenerationRules.Count > 0) starCard.AppendLine("VoiceGenerationRules: " + string.Join("; ", profile.VoiceGenerationRules));

            StringBuilder fastProfile = new StringBuilder();
            if (request.RequestType == RequestType.Story)
            {
                AppendCompactStoryProfile(fastProfile, profile);
            }
            else
            {
                AppendFastConversationProfile(fastProfile, profile);
            }

            StringBuilder storyModeSystem = new StringBuilder();
            if (request.RequestType == RequestType.Story)
            {
                AppendStoryModeSystemContext(storyModeSystem, profile);
            }

            string foregroundModeInstructions = request.RequestType == RequestType.Story
                ? "This is a STORY FOREGROUND PASS. The goal is to return a complete cached story plus the first spoken story chunk for TTS. Do not include emotion vectors here; background review handles scoring and memory. The Story button itself is sufficient instruction; an empty player message means invent a fresh complete story, not a chat reply."
                : request.RequestType == RequestType.StarInitiative
                    ? "This is a STAR INITIATIVE CHECK PASS. The app is asking whether the Star has a state-backed thing she wants to say at rest. This is not a player message and must not produce emotion vectors, memory updates, or relationship deltas."
                    : "This is the FAST FOREGROUND SPEECH PASS. Begin the spoken answer quickly so TTS can start immediately, but finish the player's complete requested response. Ordinary Chat may contain a full one-page-or-longer story or explanation split into as many ordered spokenText chunks as needed.";

            object data = new
            {
                starDisplayName = profile.DisplayName,
                foregroundModeInstructions,
                currentTimeContext = BuildCurrentTimeContext(),
                globalCanonLore = globalCanon.ToString().TrimEnd(),
                privateRomanceProfile = privateRomance.ToString().TrimEnd(),
                starActingVoiceCard = starCard.ToString().TrimEnd(),
                fastConversationProfile = fastProfile.ToString().TrimEnd(),
                storyModeSystemContext = storyModeSystem.ToString().TrimEnd()
            };

            return _templates.Render("speech_system.sbn", data);
        }

        private string BuildSpeechUserMessage(StarProfile profile, StarInteractionRequest request, StarState state)
        {
            bool storyRequest = request.RequestType == RequestType.Story;

            StringBuilder dialogueAndMarkers = new StringBuilder();
            if (!storyRequest)
            {
                AppendDialogueMemoryContext(dialogueAndMarkers, state, 8);
                AppendRememberedMarkers(dialogueAndMarkers, state, 20);
            }

            StringBuilder storyHistory = new StringBuilder();
            if (storyRequest && state.StoryHistory.Count > 0)
            {
                storyHistory.AppendLine("StoriesAlreadyTold by " + profile.DisplayName + " — avoid repeating these:");
                foreach (StarStoryHistoryEntry story in state.StoryHistory.Take(8))
                {
                    storyHistory.AppendLine("- " + story.Title + " | " + Truncate(story.Summary, 140));
                }
            }

            StringBuilder recentTurns = new StringBuilder();
            AppendRecentConversationHistory(recentTurns, profile, state, storyRequest ? 3 : Math.Min(10, Clamp(_options.RecentHistoryCount, 1, 20)), compact: true);

            StringBuilder importantMemory = new StringBuilder();
            if (storyRequest)
            {
                AppendStoryImportantMemoryContext(importantMemory, profile, state);
            }
            else
            {
                AppendForegroundImportantMemoryContext(importantMemory, profile, state);
            }

            StringBuilder relationship = new StringBuilder();
            if (storyRequest)
            {
                AppendStoryRelationshipContext(relationship, profile, state);
            }
            else
            {
                relationship.AppendLine("Relationship status: " + BuildRelationshipStatus(state));
                relationship.AppendLine("IsGirlfriend=" + state.IsGirlfriend + "; GirlfriendSince=" + (state.GirlfriendSince.HasValue ? state.GirlfriendSince.Value.ToString("o") : "none") + "; IsGirlfriendReady=" + state.IsGirlfriendReady + ".");
                relationship.AppendLine("Ally status: " + (state.IsAlly ? "ally/unlocked" : "not an active ally yet; Elusiaa is the only default ally until story/gameplay unlocks this Star."));
                relationship.AppendLine("Emotional connection skill level: " + (string.IsNullOrWhiteSpace(state.EmotionalConnectionSkillLevel) ? "interested_private_potential_partner" : state.EmotionalConnectionSkillLevel));
                relationship.AppendLine("Current EmotionalConnectionPercent: " + state.EmotionalConnectionPercent.ToString("0.0") + "%; girlfriendReady=" + state.IsGirlfriendReady + "; " + (state.GirlfriendReadinessSummary ?? string.Empty));
                relationship.AppendLine("Current state stats are part of the request every turn. Use them silently to shape warmth, caution, flirtation, distance, curiosity, secrets, and how personal your answer becomes. Do not speak the numbers unless the player asks about mechanics.");
                AppendOtherStarsRelationshipLines(relationship, state);
                AppendAccumulatedEmotionTotals(relationship, state);
                AppendConnectionFlirtGuidance(relationship, profile, state);
                AppendGirlfriendModeInstructionSet(relationship, profile, state);
                AppendGirlfriendHourlyStateContext(relationship, state);
            }

            StringBuilder todayStateContext = new StringBuilder();
            if (storyRequest) AppendTodayStateSummaryContext(todayStateContext, request);
            else AppendTodayStateContext(todayStateContext, request);

            StringBuilder task = new StringBuilder();
            if (request.RequestType == RequestType.Story)
            {
                AppendFastStoryContext(task, request);
                AppendStoryLoreContext(task, profile, request);
            }
            else if (request.RequestType == RequestType.StarInitiative)
            {
                task.AppendLine("STAR INITIATIVE STATE CHECK");
                task.AppendLine("The app is quietly checking whether " + profile.DisplayName + " wants to say something to the Last Cat while he is quiet or recently reconnected.");
                task.AppendLine("This is not a player statement. Do not treat PlayerText as something the Last Cat said. Do not thank him for asking. Do not score emotions or create memory from this.");
                if (IsTruthyFlag(request, "starInitiativeForce"))
                {
                    task.AppendLine("Manual Listen/A-button mode is active: treat this like the Last Cat pressed interact beside an NPC. Prefer to always produce a short fresh line unless it would violate canon or repeat something just said. Pick a state-backed observation, feeling, desire, compliment, cute girlfriend note, warning, or question and set hasSomethingToSay=true.");
                }
                else if (IsTruthyFlag(request, "starInitiativePrepareForListen"))
                {
                    task.AppendLine("Prepared Listen mode is active: quietly prepare one fresh state-backed line for the next time the Last Cat chooses to Listen. Prefer to speak and create a complete little moment, but this remains background work and must not displace a live player conversation.");
                }
                task.AppendLine("Use the AT-REST STAR STATE first, especially wantsToTalkAbout and conversationHooks, plus the recent emotional state and interstar status if present.");
                task.AppendLine("If she is girlfriend, also use GIRLFRIEND HOURLY STATE: needVector, wantVector, thingsSheWantsToSay, and cuteThingsSheWantsToSay are explicit candidate sources for spontaneous girlfriend speech while exploring.");
                task.AppendLine("She may use thingsSheWantsToSay/cuteThingsSheWantsToSay, but should check the recent conversation first and avoid repeating a thing she already said recently.");
                task.AppendLine("Think of this as two outputs: first decide hasSomethingToSay/shouldSpeak and fill speechDecisionVector with one item for itemId=saySomethingNow; then if yes put exactly what she wants to say in spokenText.");
                task.AppendLine("If the state gives her a real reason to speak now, set hasSomethingToSay=true and give one or two natural spokenText chunks she chooses to say on her own.");
                task.AppendLine("If nothing in the state feels worth interrupting him for, set hasSomethingToSay=false, priority=0, selectedTopic='', reason='no state-backed reason to speak now', and spokenText=[] exactly.");
                task.AppendLine("Only speak about things she would naturally volunteer at her current closeness. A low-connection Star should make a light observation; a close/girlfriend Star may be more tender.");
                task.AppendLine("Never mention systems, JSON, vectors, mobile, reconnects, initiative checks, background services, or that the app asked.");
            }
            else
            {
                if (request.RequestType == RequestType.Photo || (request.AttachedPhoto != null && request.AttachedPhoto.IsAttached))
                {
                    AppendPhotoCommentGuidance(task, profile, request);
                }
                else
                {
                    AppendFastChatAnswerGuidance(task, profile, request, state);
                }
                if (request.GameContext.Flags.TryGetValue("extraInformationContext", out string? extraInformationContext) && !string.IsNullOrWhiteSpace(extraInformationContext))
                {
                    task.AppendLine();
                    task.AppendLine("SECOND RESPONSE REQUEST WITH MATCHED INFORMATION");
                    task.AppendLine("The first fast response detected that more information was needed. The player's original speech is still the PlayerText at the top of this request and is repeated inside the attached context below.");
                    task.AppendLine("Use the matched folder index or dynamic archive to answer that exact original request now. Give the precise requested detail when it is present. Do not repeat the earlier checking/uncertainty bridge and do not invent missing facts.");
                    task.AppendLine(extraInformationContext);
                    task.AppendLine("If the attached information contains the answer, respond directly and naturally in character. If it does not contain the exact answer, say that the additional source did not provide that detail.");
                }
                AppendGirlfriendProposalGuidance(task, profile, request, state);
            }

            StringBuilder contract = new StringBuilder();
            if (request.RequestType == RequestType.Story)
            {
                contract.AppendLine("Return story foreground JSON only: starId, spokenText, storyTitle, storyId, storyMarkdown, storySummary, storyIsFinished, storyPartIndex, thingsLearnedOrSuggestedByPlayer. Do not include emotion vectors, emotionScores, voiceDirective, summaries, deltas, loreUsed, riskFlags, or learnedFunctions.");
                contract.AppendLine("Return the complete story in storyMarkdown, formatted as ## Chunk 0, one or two story sentences, ---, ## Chunk 1, one or two story sentences, ---, and so on. Do not include blank chunks, delimiter-only chunks, or a final empty heading; every chunk must contain narration the TTS can speak.");
                contract.AppendLine("For an unseeded Story button click, do not answer with only a mood vignette from today's Star state. Develop a new episode: named subject, problem/desire, escalation, surprising turn, and ending. Keep it compact enough to finish inside the JSON response.");
                contract.AppendLine("Story length discipline: honor the player's explicit requested length. Without an explicit request, 6-10 meaningful chunks is a useful default. Always finish the story rather than stopping mid-scene.");
                contract.AppendLine("spokenText must be the first actual story chunk from storyMarkdown, not a preface or promise. Start the story itself immediately.");
                contract.AppendLine("Set storyPartIndex=0. Set storyIsFinished=true when storyMarkdown is a complete episode with a real ending or satisfying soft cliffhanger, even when it has Chunk 1+ for local autoplay. Set storyIsFinished=false only if the story truly requires a later LLM continuation.");
                contract.AppendLine("Narrate the story itself as " + profile.DisplayName + "; do not talk about preparing the story. Let her emotional reaction show through the imagery, word choice, tenderness, humor, fear, or wonder of the opening lines.");
            }
            else if (request.RequestType == RequestType.StarInitiative)
            {
                contract.AppendLine("Return star initiative JSON only: starId, hasSomethingToSay, priority, speechDecisionVector, selectedTopic, reason, spokenText.");
                contract.AppendLine("speechDecisionVector must contain exactly one item: itemId='saySomethingNow', displayName='Say something now', confidence 0-1, score 0-100, reason. Confidence is zero-based: any confidence can help the runtime accept the line, but score still describes urgency/desire.");
                contract.AppendLine("hasSomethingToSay/shouldSpeak is the first output: true only when the AT-REST STAR STATE, GIRLFRIEND HOURLY STATE, recent emotion state, or interstar feedback gives her a real reason to speak now.");
                contract.AppendLine("spokenText is the second output: if hasSomethingToSay=true, it is exactly what she chooses to say as herself while the Last Cat is at rest; if false, it must be [].");
                contract.AppendLine("If hasSomethingToSay=false, spokenText must be [] and priority must be 0.");
                contract.AppendLine("If hasSomethingToSay=true, spokenText must be 1-3 short chunks she says naturally on her own, not as an answer to a player message.");
                contract.AppendLine("Do not include informationNeeds, emotion vectors, summaries, memory notes, deltas, loreUsed, riskFlags, or learnedFunctions.");
            }
            else
            {
                contract.AppendLine("Return only: starId, spokenText, needsMoreInformation, informationNeeds, and thingsLearnedOrSuggestedByPlayer.");
                contract.AppendLine();
                contract.AppendLine("informationNeeds is an array of objects with subject, confidence, and score.");
                contract.AppendLine("The subject field chooses the information source and must be exactly one of: history, quotes, markers, lore, bestiary, characters, world, screen, story. When the latest message may need older context or information outside this prompt, return the informationNeeds array: one separate object for each area you need information from history, quotes, markers, lore, bestiary, characters, world, screen, and story. Use low confidence/score for areas that are probably not needed, and higher values for likely areas.");
                contract.AppendLine("Content-backed subject names match the local folders or memory exactly: lore=skills/spells/abilities/weapons/mechanics; bestiary=monsters/enemies/bosses/creatures/HP/drops; characters=Stars/people/NPCs/companions/guardians; world=areas/locations/settlements/ecology/quests; story=exact scenes/meetings/dialogue/story continuity. A deep pass receives the selected subjects complete information plus the original player speech.");
                contract.AppendLine("For an open-ended creative story request, do not request extra information merely because it is a story; invent it from the profile and context already provided. Request older data only when the player explicitly requires a missing canon fact or continuation detail.");
            }
            if (request.RequestType == RequestType.Story)
            {
                contract.AppendLine("spokenText array: each item is one TTS chunk. Use one or two sentences per item. The host also validates story chunks, but do not rely on it.");
            }
            else if (request.RequestType == RequestType.StarInitiative)
            {
                contract.AppendLine("spokenText array: each item is one TTS chunk. Use one or two sentences per item, or [] when hasSomethingToSay=false.");
            }
            else
            {
                contract.AppendLine();
                contract.AppendLine("spokenText array: each item is one ordered TTS chunk. Use one or two natural sentences per item, and keep adding chunks until the requested answer is complete.");
            }

            object data = new
            {
                starDisplayName = profile.DisplayName,
                playerTextBlock = request.RequestType == RequestType.StarInitiative ? "> [No player message. This is a system at-rest state check.]" : QuoteBlock(request.PlayerText),
                requestType = request.RequestType.ToString(),
                currentTimeContext = BuildCurrentTimeContext(),
                romanceMode = request.GameContext.RomanceMode.ToString(),
                area = request.GameContext.Area ?? string.Empty,
                quest = request.GameContext.Quest ?? string.Empty,
                relationshipContext = relationship.ToString().TrimEnd(),
                dialogueAndMarkerContext = dialogueAndMarkers.ToString().TrimEnd(),
                ongoingSummary = string.IsNullOrWhiteSpace(state.ConversationSummary) ? "none yet" : Truncate(state.ConversationSummary, 600),
                lastRememberedReply = BuildLastUsableStarReply(state),
                storyHistoryContext = storyHistory.ToString().TrimEnd(),
                recentConversationTurns = recentTurns.ToString().TrimEnd(),
                importantMemoryContext = importantMemory.ToString().TrimEnd(),
                latestScreenOneLine = BuildLatestScreenOneLine(request.RecentScreenCaptures),
                todayStarStateContext = todayStateContext.ToString().TrimEnd(),
                taskSpecificGuidance = task.ToString().TrimEnd(),
                foregroundContractHeading = request.RequestType == RequestType.Story ? "STORY FOREGROUND CONTRACT" : request.RequestType == RequestType.StarInitiative ? "STAR INITIATIVE CHECK CONTRACT" : "FAST SPEECH CONTRACT",
                foregroundContract = contract.ToString().TrimEnd()
            };

            return _templates.Render("speech_user.sbn", data);
        }

        private static void AppendGirlfriendProposalGuidance(StringBuilder task, StarProfile profile, StarInteractionRequest request, StarState state)
        {
            if (task == null || request == null || request.GameContext == null) return;
            if (!request.GameContext.Flags.TryGetValue("girlfriendProposalWorker", out string? worker) || !string.Equals(worker, "true", StringComparison.OrdinalIgnoreCase)) return;

            string decision = GetFlag(request.GameContext, "girlfriendProposalDecision");
            string existing = GetFlag(request.GameContext, "existingGirlfriendStarId");
            string reason = GetFlag(request.GameContext, "girlfriendProposalReason");

            task.AppendLine();
            task.AppendLine("GIRLFRIEND PROPOSAL DECISION MODE");
            task.AppendLine("The Last Cat has just asked " + profile.DisplayName + " to be his girlfriend. Answer directly in " + profile.DisplayName + "'s voice.");
            task.AppendLine("Decision flag from deterministic relationship gate: " + (string.IsNullOrWhiteSpace(decision) ? "unknown" : decision) + ".");
            task.AppendLine("Existing exclusive girlfriend: " + (string.IsNullOrWhiteSpace(existing) ? "none" : existing) + ".");
            task.AppendLine("Gate reason: " + (string.IsNullOrWhiteSpace(reason) ? "none" : reason));
            task.AppendLine("Current state: met=" + state.IsMet + ", ally=" + state.IsAlly + ", girlfriend=" + state.IsGirlfriend + ", girlfriendReady=" + state.IsGirlfriendReady + ", Heart=" + state.Heart + ", Trust=" + state.Trust + ", Romance=" + state.Romance + ", EmotionalConnectionPercent=" + state.EmotionalConnectionPercent.ToString("0.0") + ".");
            task.AppendLine("Only one Star can be the Last Cat's girlfriend at a time. If an existing girlfriend is someone else, you must say no because he is already committed, and you may ask him to end that relationship first if he truly means this.");
            task.AppendLine("If decision=accept, say yes clearly and warmly in character. If decision=already_girlfriend, affirm that she already is his girlfriend. If decision=not_ready_no_for_now, say not yet with affection and give a clear path: become/keep ally status, build trust, heart, romance, emotional safety, and personality-matched connection. If decision=blocked_existing_girlfriend, refuse clearly but gracefully.");
            task.AppendLine("Keep it concise: 2-5 spokenText chunks. Do not mention JSON, flags, numbers, or deterministic gates unless the player explicitly asked about mechanics.");
        }

        private static string GetFlag(GameContext context, string key)
        {
            if (context != null && context.Flags != null && context.Flags.TryGetValue(key, out string? value)) return value ?? string.Empty;
            return string.Empty;
        }

        private static bool IsTruthyFlag(StarInteractionRequest request, string key)
        {
            string value = GetFlag(request?.GameContext, key);
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static void AppendRecentConversationHistory(StringBuilder sb, StarProfile profile, StarState state, int maxItems, bool compact)
        {
            List<ConversationLogEntry> usableHistory = GetUsableRecentConversationHistory(state, Math.Max(1, maxItems));

            sb.AppendLine("RECENT CONVERSATION HISTORY FROM MEMORY LOG / SAVED STATE — newest first. Use this for continuity, follow-ups, pronouns, tone, and relationship pacing.");
            if (usableHistory.Count == 0)
            {
                sb.AppendLine("No usable prior spoken turns are recorded for this Star yet. Technical parser-fallback turns are intentionally omitted from roleplay memory.");
                if (!string.IsNullOrWhiteSpace(state.ConversationSummary)) sb.AppendLine("Fallback saved conversation summary: " + Truncate(state.ConversationSummary, 500));
                return;
            }

            int i = 1;
            foreach (ConversationLogEntry entry in usableHistory)
            {
                if (compact)
                {
                    sb.AppendLine("- Turn " + i + ": requestType=" + entry.RequestType + "; player=" + QuoteInline(Truncate(entry.PlayerText, 220)) + "; " + profile.DisplayName + "=" + QuoteInline(Truncate(entry.StarResponse, 240)));
                    if (!string.IsNullOrWhiteSpace(entry.Summary) && !IsTechnicalMemoryNote(entry.Summary)) sb.AppendLine("  summary=" + Truncate(entry.Summary, 240));
                }
                else
                {
                    sb.AppendLine("- Turn " + i + " (" + (i == 1 ? "most recent" : "older") + ") at " + entry.CreatedAt.ToString("o") + "; requestType=" + entry.RequestType);
                    sb.AppendLine("  player=" + QuoteInline(Truncate(entry.PlayerText, 260)));
                    sb.AppendLine("  " + profile.DisplayName + "=" + QuoteInline(Truncate(entry.StarResponse, 320)));
                    if (!string.IsNullOrWhiteSpace(entry.Summary) && !IsTechnicalMemoryNote(entry.Summary)) sb.AppendLine("  summary=" + Truncate(entry.Summary, 300));
                    if (entry.ImportantQuotes.Count > 0) sb.AppendLine("  impactfulQuotes=" + string.Join(" | ", entry.ImportantQuotes.Take(4)));
                    if (entry.RelationshipEvents.Count > 0) sb.AppendLine("  relationshipEvents=" + string.Join(" | ", entry.RelationshipEvents.Take(4)));
                    if (entry.Tags.Count > 0) sb.AppendLine("  tags=" + string.Join(", ", entry.Tags.Take(6)));
                }
                i++;
            }
        }

        private static void AppendForegroundImportantMemoryContext(StringBuilder sb, StarProfile profile, StarState state)
        {
            sb.AppendLine("IMPORTANT MEMORY AVAILABLE TO THIS FOREGROUND REPLY");
            sb.AppendLine("This is a compact continuity slice in addition to the recent conversation history above.");
            AppendList(sb, "Important exact player quotes", state.ImportantPlayerQuotes.Take(10).ToList());
            AppendList(sb, "Memory notes", state.MemoryNotes.Where(x => !IsTechnicalMemoryNote(x)).Take(8).ToList());
            AppendList(sb, "Things learned or suggested by the Last Cat (durable; do not duplicate in the new foreground array)", (state.ThingsLearnedOrSuggestedByPlayer ?? new List<string>()).Take(20).ToList());
            AppendList(sb, "Relationship events", state.RelationshipEvents.Take(8).ToList());
            if (state.StoryHistory.Count > 0)
            {
                sb.AppendLine("Stories already told by " + profile.DisplayName + ":");
                foreach (StarStoryHistoryEntry story in state.StoryHistory.Take(6))
                {
                    sb.AppendLine("- " + story.Title + " | finished=" + story.Finished + " | " + Truncate(story.Summary, 180));
                }
            }
        }

        private static void AppendStoryImportantMemoryContext(StringBuilder sb, StarProfile profile, StarState state)
        {
            sb.AppendLine("IMPORTANT MEMORY AVAILABLE TO THIS STORY REQUEST");
            sb.AppendLine("Use these only as optional seasoning. The story must still develop a fresh plot.");
            AppendList(sb, "Best exact Last Cat quotes to optionally echo", state.ImportantPlayerQuotes.Take(4).ToList());
            AppendList(sb, "Memory notes", state.MemoryNotes.Where(x => !IsTechnicalMemoryNote(x)).Take(3).ToList());
            AppendList(sb, "Things learned or suggested by the Last Cat (durable; optional story continuity)", (state.ThingsLearnedOrSuggestedByPlayer ?? new List<string>()).Take(10).ToList());
            AppendList(sb, "Relationship events", state.RelationshipEvents.Take(3).ToList());
            if (state.StoryHistory.Count > 0)
            {
                sb.AppendLine("Recent stories already told by " + profile.DisplayName + ":");
                foreach (StarStoryHistoryEntry story in state.StoryHistory.Take(4))
                {
                    sb.AppendLine("- " + story.Title + " | finished=" + story.Finished + " | " + Truncate(story.Summary, 120));
                }
            }
        }

        private static void AppendStoryRelationshipContext(StringBuilder sb, StarProfile profile, StarState state)
        {
            if (sb == null || state == null) return;
            sb.AppendLine("Relationship status: " + BuildRelationshipStatus(state));
            sb.AppendLine("Met=" + state.IsMet + "; Ally=" + state.IsAlly + "; IsGirlfriend=" + state.IsGirlfriend + "; GirlfriendReady=" + state.IsGirlfriendReady + "; EmotionalConnection=" + state.EmotionalConnectionPercent.ToString("0.0") + "%.");
            sb.AppendLine("Use this only for tone and intimacy. Do not read the numbers aloud.");
            if (state.IsGirlfriend)
            {
                sb.AppendLine("Committed girlfriend story tone: use affectionate intimacy, private warmth, and personality-matched flirtation as narration color only. Do not let girlfriend needs/wants replace the story plot unless the player explicitly asked for a romantic story.");
            }
            else if (state.IsGirlfriendReady)
            {
                sb.AppendLine("Girlfriend-ready story tone: romantic subtext is allowed, but preserve pacing, earned closeness, and the story premise.");
            }
            AppendStoryGirlfriendStateSummary(sb, state);
            AppendStoryCoupleActivitySummary(sb, state);
        }

        private static void AppendStoryGirlfriendStateSummary(StringBuilder sb, StarState state)
        {
            StarGirlfriendHourlyStateContext? context = state.GirlfriendHourlyState;
            if (context == null || !context.IsAvailable) return;
            sb.AppendLine();
            sb.AppendLine("GIRLFRIEND HOURLY STATE — COMPACT STORY COLOR ONLY");
            if (!string.IsNullOrWhiteSpace(context.RelationshipPosture)) sb.AppendLine("posture=" + Truncate(context.RelationshipPosture, 160));
            if (!string.IsNullOrWhiteSpace(context.Summary)) sb.AppendLine("summary=" + Truncate(context.Summary, 180));
            sb.AppendLine("girlfriendCuteModeEnabled=" + state.GirlfriendCuteModeEnabled.ToString().ToLowerInvariant() + "; cuteMode=" + (state.GirlfriendCuteModeEnabled && context.CuteModeActive).ToString().ToLowerInvariant() + "; signature=" + Truncate(context.CuteModeSignature ?? string.Empty, 100));
            sb.AppendLine("needVector=" + FormatGirlfriendVectorString(context.NeedVector, 4));
            sb.AppendLine("wantVector=" + FormatGirlfriendVectorString(context.WantVector, 4));
            sb.AppendLine("Use girlfriend context as tone/color only. Do not turn an unseeded Story button into direct girlfriend chat.");
        }

        private static void AppendStoryCoupleActivitySummary(StringBuilder sb, StarState state)
        {
            List<GirlfriendCoupleActivityScore> scores = (state.GirlfriendCoupleActivityScores ?? new List<GirlfriendCoupleActivityScore>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ActivityId) && x.TotalScore > 0)
                .OrderByDescending(x => x.TotalScore)
                .Take(3)
                .ToList();
            if (scores.Count == 0) return;
            sb.AppendLine("coupleActivityScores=" + string.Join(";", scores.Select(x => SanitizeVectorItemId(x.ActivityId) + ":" + x.TotalScore.ToString("0.0") + ":" + Math.Max(0, Math.Min(1, x.HighestConfidence)).ToString("0.00"))));
            sb.AppendLine("Use couple activity history only as intimacy tone. Do not make it the story premise unless the player asked for a romantic story.");
        }

        private void AppendTodayStateContext(StringBuilder sb, StarInteractionRequest request)
        {
            object data = BuildStarStateTemplateData(request, includeAtRestFields: false);
            sb.Append(_templates.Render("star_state_response.sbn", data).TrimEnd());
        }

        private static object BuildStarStateTemplateData(StarInteractionRequest request, bool includeAtRestFields)
        {
            string json = string.Empty;
            if (request.GameContext != null && request.GameContext.Flags != null)
            {
                request.GameContext.Flags.TryGetValue("todayStarStateJson", out json);
            }

            string situationSummary = string.Empty;
            string feelingsSummary = string.Empty;
            string desires = string.Empty;
            string privateSecretsOrHooks = string.Empty;
            string whatSheIsDoing = string.Empty;
            List<string> moodTags = new List<string>();
            List<string> conversationHooks = new List<string>();
            List<string> wantsToTalkAbout = new List<string>();

            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(json);
                    JsonElement root = document.RootElement;
                    situationSummary = FirstJsonString(
                        root,
                        new[] { "currentInnerLife", "situationSummary" },
                        new[] { "situationSummary" });
                    feelingsSummary = FirstJsonString(
                        root,
                        new[] { "currentInnerLife", "feelingsSummary" },
                        new[] { "currentInnerLife", "feelings" },
                        new[] { "feelingsSummary" },
                        new[] { "feelings" });
                    desires = FirstJsonString(
                        root,
                        new[] { "currentInnerLife", "desires" },
                        new[] { "currentInnerLife", "currentDesires" },
                        new[] { "desires" },
                        new[] { "currentDesires" });
                    privateSecretsOrHooks = FirstJsonString(
                        root,
                        new[] { "currentInnerLife", "privateSecretsOrHooks" },
                        new[] { "privateSecretsOrHooks" });
                    whatSheIsDoing = FirstJsonString(
                        root,
                        new[] { "today", "whatSheIsDoing" },
                        new[] { "today", "whatSheIsDoingToday" },
                        new[] { "whatSheIsDoing" },
                        new[] { "whatSheIsDoingToday" });
                    moodTags = FirstJsonStringList(
                        root,
                        new[] { "today", "moodTags" },
                        new[] { "moodTags" });

                    if (includeAtRestFields)
                    {
                        conversationHooks = FirstJsonStringList(
                            root,
                            new[] { "today", "conversationHooks" },
                            new[] { "conversationHooks" });
                        wantsToTalkAbout = FirstJsonStringList(
                            root,
                            new[] { "today", "wantsToTalkAbout" },
                            new[] { "today", "thingsSheWantsToTalkAbout" },
                            new[] { "wantsToTalkAbout" },
                            new[] { "thingsSheWantsToTalkAbout" });
                    }
                }
                catch
                {
                    // A malformed cached state must never cause a foreground or at-rest request to fail.
                }
            }

            situationSummary = Truncate(situationSummary, 700);
            feelingsSummary = Truncate(feelingsSummary, 700);
            desires = Truncate(desires, 700);
            privateSecretsOrHooks = Truncate(privateSecretsOrHooks, 700);
            whatSheIsDoing = Truncate(whatSheIsDoing, 900);
            moodTags = moodTags.Take(12).Select(x => Truncate(x, 100)).ToList();
            conversationHooks = conversationHooks.Take(12).Select(x => Truncate(x, 220)).ToList();
            wantsToTalkAbout = wantsToTalkAbout.Take(12).Select(x => Truncate(x, 220)).ToList();

            bool hasState = !string.IsNullOrWhiteSpace(situationSummary)
                || !string.IsNullOrWhiteSpace(feelingsSummary)
                || !string.IsNullOrWhiteSpace(desires)
                || !string.IsNullOrWhiteSpace(privateSecretsOrHooks)
                || !string.IsNullOrWhiteSpace(whatSheIsDoing)
                || moodTags.Count > 0
                || conversationHooks.Count > 0
                || wantsToTalkAbout.Count > 0;

            return new
            {
                hasState,
                situationSummary,
                feelingsSummary,
                desires,
                privateSecretsOrHooks,
                whatSheIsDoing,
                moodTags,
                conversationHooks,
                wantsToTalkAbout
            };
        }

        private static void AppendTodayStateSummaryContext(StringBuilder sb, StarInteractionRequest request)
        {
            string json = string.Empty;
            string summary = string.Empty;
            string generatedAt = string.Empty;
            string slotId = string.Empty;
            if (request.GameContext != null && request.GameContext.Flags != null)
            {
                request.GameContext.Flags.TryGetValue("todayStarStateJson", out json);
                request.GameContext.Flags.TryGetValue("todayStarStateSummary", out summary);
                request.GameContext.Flags.TryGetValue("todayStarStateGeneratedAt", out generatedAt);
                request.GameContext.Flags.TryGetValue("todayStarStateSlotId", out slotId);
            }

            if (string.IsNullOrWhiteSpace(json) && string.IsNullOrWhiteSpace(summary))
            {
                sb.AppendLine("TODAY STAR STATE: none. Story mode should choose a fresh plot from profile/lore/seed.");
                return;
            }

            string situation = string.Empty;
            string feelings = string.Empty;
            string desires = string.Empty;
            string activity = string.Empty;
            List<string> hooks = new List<string>();
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(json);
                    JsonElement root = document.RootElement;
                    situation = FirstJsonString(root, new[] { "currentInnerLife", "situationSummary" }, new[] { "situationSummary" });
                    feelings = FirstJsonString(root, new[] { "currentInnerLife", "feelings" }, new[] { "currentInnerLife", "feelingsSummary" }, new[] { "feelingsSummary" });
                    desires = FirstJsonString(root, new[] { "currentInnerLife", "desires" }, new[] { "currentInnerLife", "currentDesires" }, new[] { "currentDesires" });
                    activity = FirstJsonString(root, new[] { "today", "whatSheIsDoing" }, new[] { "whatSheIsDoingToday" });
                    hooks = FirstJsonStringList(root, new[] { "today", "conversationHooks" }, new[] { "today", "wantsToTalkAbout" }, new[] { "conversationHooks" });
                }
                catch
                {
                    // A broken daily JSON blob should not bloat or break story prompts.
                }
            }

            sb.AppendLine("TODAY STAR STATE — COMPACT STORY COLOR ONLY");
            sb.AppendLine("GeneratedAt=" + generatedAt + "; Slot=" + slotId + "; Summary=" + Truncate(summary, 220));
            if (!string.IsNullOrWhiteSpace(situation)) sb.AppendLine("Situation: " + Truncate(situation, 220));
            if (!string.IsNullOrWhiteSpace(feelings)) sb.AppendLine("Feelings: " + Truncate(feelings, 200));
            if (!string.IsNullOrWhiteSpace(desires)) sb.AppendLine("Desires: " + Truncate(desires, 200));
            if (!string.IsNullOrWhiteSpace(activity)) sb.AppendLine("Current activity: " + Truncate(activity, 160));
            AppendList(sb, "Optional state hooks", hooks.Take(3).Select(x => Truncate(x, 140)).ToList());
            sb.AppendLine("Do not make the story only a restatement of today's state. Use this as color around a fresh plot.");
        }

        private static string FirstJsonString(JsonElement root, params string[][] paths)
        {
            foreach (string[] path in paths)
            {
                if (TryGetJsonPath(root, out JsonElement value, path) && value.ValueKind == JsonValueKind.String)
                {
                    string? text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            return string.Empty;
        }

        private static List<string> FirstJsonStringList(JsonElement root, params string[][] paths)
        {
            foreach (string[] path in paths)
            {
                if (TryGetJsonPath(root, out JsonElement value, path) && value.ValueKind == JsonValueKind.Array)
                {
                    List<string> result = value.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString() ?? string.Empty)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                    if (result.Count > 0) return result;
                }
            }
            return new List<string>();
        }

        private static bool TryGetJsonPath(JsonElement root, out JsonElement value, params string[] path)
        {
            value = root;
            foreach (string part in path)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return false;
            }
            return true;
        }

        private static void AppendStoryModeSystemContext(StringBuilder sb, StarProfile profile)
        {
            sb.AppendLine("LONG STORY MODE");
            sb.AppendLine("When RequestType=Story, you are not merely agreeing to tell a story. You are already telling it aloud as " + profile.DisplayName + ".");
            sb.AppendLine("Do not open with setup phrases such as 'let me tell you', 'shall I begin', 'the story begins', 'I will continue', or 'where we left off'. Start with the actual story prose immediately.");
            sb.AppendLine("For story mode, write a complete story in storyMarkdown on the first response. This is not a story seed/planning task. The tale should feel whole, with a beginning, middle, climax, and satisfying resolution or soft cliffhanger. Format storyMarkdown into numbered markdown speech chunks: ## Chunk 0, then one or two story sentences, then a line containing only --- before the next chunk. Continue this pattern for every chunk. Never emit an empty chunk; if a chunk has no narration, omit that chunk entirely and renumber the remaining chunks.");
            sb.AppendLine("The host target minutes and chunk count are flexible when they are automatic defaults. A length explicitly requested by the player is a hard creative requirement. When the player presses the Story button with no seed, the request itself is: invent a fresh complete episode now. Use a concrete character, place, problem, escalation, surprise/turn, and ending; do not merely expand today's mood/state into one reflective scene.");
            sb.AppendLine("Honor an explicit player-requested length even when it requires many chunks. When no length was requested, use the host target as guidance. Never stop mid-sentence or mid-scene; complete the full requested story and set storyIsFinished=true when its arc has ended.");
            sb.AppendLine("spokenText must be only the first actual story passage from ## Chunk 0. It must not be an introduction about the story. Keep each spokenText item to one or two natural sentences for TTS.");
            sb.AppendLine("After chunk 0, the host will auto-play the remaining cached storyMarkdown chunks locally. Do not expect or ask for follow-up LLM continuation calls.");
            sb.AppendLine("At low Heart/Trust, " + profile.DisplayName + " may choose a less intimate or less autobiographical tale, but she must still satisfy the requested subject, quality, completeness, and length. Higher bond allows more private and emotionally revealing stories.");
            sb.AppendLine("The Last Cat is a mature dark fantasy game with wonder, horror, danger, tenderness, and comedy. The story must emotionally fit the player's latest request and the current relationship state: comfort him if he asks softly, become playful if he asks playfully, become dark, exciting, violent, tense, or wondrous if the seed asks for that. Do this through the first story sentences, not by preface.");
            sb.AppendLine("Do not repeat a story from StoriesAlreadyTold. Incorporate relevant exact quotes from shared history or known people when natural, but do not overdo it.");
            sb.AppendLine("storyPartIndex must be 0 for the initial story response. Set storyIsFinished=true when the storyMarkdown is a complete episode with a real ending or satisfying soft cliffhanger, even if the host still has cached chunks to autoplay. Only set storyIsFinished=false for an intentionally unfinished story that requires a future LLM continuation.");
        }

        private static void AppendFastStoryContext(StringBuilder sb, StarInteractionRequest request)
        {
            StoryOptions options = request.StoryOptions ?? new StoryOptions();
            sb.AppendLine("STORY MODE CONTEXT");
            sb.AppendLine("LocalAutoplayAfterFirstChunk: true");
            if (!string.IsNullOrWhiteSpace(options.PlayerSuggestion))
            {
                sb.AppendLine("Player story seed (hard creative requirement): " + Truncate(options.PlayerSuggestion, 600));
                sb.AppendLine("You must honor the requested mood, genre, setting, and action in this seed. Do not replace an exciting/battle/dark seed with a default gentle parable.");
            }
            else
            {
                sb.AppendLine("Player story seed: none. The Story button was clicked; this alone is the full request. Do not ask for a seed and do not treat the empty player text as a mood prompt. Invent a NEW complete story now.");
                sb.AppendLine("Default unseeded story rule: choose a concrete plot from game lore, not only the Star's daily mood/state. Include at least one named traveler/NPC/creature, one problem or desire, one surprising turn, and a real ending or satisfying soft cliffhanger.");
            }
            int displayedTargetChunks = options.TargetChunkCount <= 0 ? 8 : Math.Max(1, options.TargetChunkCount);
            sb.AppendLine("ApproxTargetMinutes: " + (options.TargetMinutes <= 0 ? 0 : options.TargetMinutes) + " (very flexible; complete story is more important)");
            sb.AppendLine("ApproxTargetChunkCount: " + displayedTargetChunks + " (honor this closely when it reflects the player's explicit length request; otherwise use it as guidance)");
            sb.AppendLine("MaxSentencesPerChunk: " + (options.MaxSentencesPerChunk <= 0 ? 2 : options.MaxSentencesPerChunk));
            if (options.StoriesAlreadyTold.Count > 0)
            {
                sb.AppendLine("StoriesAlreadyTold from host memory - forbidden as repeat plots:");
                foreach (string story in options.StoriesAlreadyTold.Take(20)) sb.AppendLine("- " + Truncate(story, 260));
                sb.AppendLine("Do not reuse these titles, storyIds, central objects, central miracles, or main plot beats unless the player explicitly asks to hear that same story again.");
                sb.AppendLine(BuildForbiddenStoryMotifHints(options.StoriesAlreadyTold));
            }
            sb.AppendLine("Create a new complete story. The desktop host will speak Chunk 0 from spokenText, then read Chunk 1+ directly from cached storyMarkdown. Empty chunks are invalid because the host will skip them; write only meaningful narration chunks. Do not write a trailing chunk header unless that chunk has narration after it. The final chunk must contain a real ending line or a satisfying soft cliffhanger.");
            sb.AppendLine("Length rule: match the length the player requested. If no explicit length was requested, use about 6-10 meaningful chunks by default. Do not pad, but do not shorten an explicitly requested long tale. The story must be complete at the requested scale.");
        }

        private static string InterstarVectorAlias(string starId)
        {
            if (string.IsNullOrWhiteSpace(starId)) return string.Empty;
            string compact = new string(starId.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrWhiteSpace(compact)) return string.Empty;
            return char.ToLowerInvariant(compact[0]) + (compact.Length > 1 ? compact.Substring(1) : string.Empty);
        }

        private static string BuildEmotionalEffectVectorInstruction()
        {
            return "emotionalEffectVector is a compact machine-readable emotion hint for the player's latest statement. It tells the system whether the player made this Star feel each emotion. Use the canonical emotion order exactly: "
                + string.Join(", ", StarEmotionMatrix.StandardEmotionIds)
                + ". Format exactly as confidence:score;confidence:score;... with one pair per emotion in that order. The vector must contain exactly one pair for every listed emotion; include trailing 0.00:0 pairs. Confidence is 0.00-1.00 and score is 0-100. Confidence is certainty that the emotion exists, not the whole relationship-point award; the deterministic system will use confidence * score, so a low nonzero confidence with a meaningful score still matters. Use 0.00:0 only for emotions not implied. Use low nonzero confidence (for example 0.05-0.35) for real light reactions such as being mildly flattered, intrigued, safe, comforted, protected, trusting, admiring, hopeful, affectionate, or wondrous. Direct intimate/specific lines may use 0.25-0.65 with high score when the emotion is clear. Example shape: 0.03:20;0.35:75;0.00:0;... No names, spaces, reasons, brackets, or extra text.";
        }



        private static string BuildNegativeEmotionVectorInstruction()
        {
            return "negativeEmotionVector is a compact machine-readable hint for negative feelings in the latest player statement. Use exactly seven emotions in this order: Irritated, Disrespected, Pressured, CreepedOut, Bored, Hurt, Unsafe. Format exactly as confidence:score;confidence:score;... with seven pairs. Use 0.00:0 when not implied. High values should be reserved for real negativity: repeated insults, coercion, disrespect, shallow pressure, unsafe sexual pushiness, cruelty, or boring/repetitive low-effort behavior.";
        }


        private static string FormatSpokenParts(IEnumerable<string> parts)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[");
            foreach (string part in parts.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                sb.AppendLine("  \"" + part.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",");
            }
            sb.AppendLine("]");
            return sb.ToString();
        }

        private static string FormatSpokenPartsForReview(IEnumerable<string> parts, int maxCharacters)
        {
            List<string> clean = (parts ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();
            int totalCharacters = clean.Sum(x => x.Length);
            if (totalCharacters <= maxCharacters) return FormatSpokenParts(clean);

            int headBudget = Math.Max(800, (int)(maxCharacters * 0.62));
            int tailBudget = Math.Max(500, maxCharacters - headBudget - 160);
            List<string> selectedHead = new List<string>();
            List<string> selectedTail = new List<string>();
            int used = 0;
            foreach (string part in clean)
            {
                if (used >= headBudget) break;
                string value = part.Length <= headBudget - used ? part : Truncate(part, Math.Max(80, headBudget - used));
                selectedHead.Add(value);
                used += value.Length;
            }

            used = 0;
            for (int i = clean.Count - 1; i >= selectedHead.Count; i--)
            {
                if (used >= tailBudget) break;
                string part = clean[i];
                string value = part.Length <= tailBudget - used ? part : TruncateFromEnd(part, Math.Max(80, tailBudget - used));
                selectedTail.Insert(0, value);
                used += value.Length;
            }

            List<string> compact = new List<string>(selectedHead);
            int shownCharacters = selectedHead.Sum(x => x.Length) + selectedTail.Sum(x => x.Length);
            compact.Add("[Background review excerpt: " + Math.Max(0, totalCharacters - shownCharacters) + " spoken characters omitted; the complete response was still returned to the player and TTS.]");
            compact.AddRange(selectedTail);
            return FormatSpokenParts(compact);
        }

        private static string TruncateFromEnd(string value, int maxCharacters)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxCharacters) return value ?? string.Empty;
            if (maxCharacters <= 3) return value.Substring(value.Length - maxCharacters);
            return "..." + value.Substring(value.Length - (maxCharacters - 3));
        }

        private static void AppendGlobalCanonLore(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("GLOBAL LAST CAT CANON THE STARS SHOULD KNOW");
            sb.AppendLine("- No cat has been seen for one hundred years. The universe is drying up because cats are gone; cats are rare astral conduits tied to water, stardust, and restoration.");
            sb.AppendLine("- The player is the sentient Last Cat. He was delivered twenty years ago to an intergalactic orphanage with people and other races from across the universe.");
            sb.AppendLine("- The six Stars are six human women in or near the caves and cave settlements. They have childhoods, parents, village ties, wounds, and their own local histories.");
            sb.AppendLine("- The caves are dark, deep, fast-moving, and genuinely lethal. Violent and treacherous enemies, poisoned water, collapsing routes, ambushes, and ancient powers can injure or kill both cats and Stars.");
            sb.AppendLine("- The Last Cat needs the Stars' powers to survive the cave network, and the Stars need his astral growth, judgment, and courage. Neither side is invulnerable.");
            sb.AppendLine("- The Last Cat is the only known cat and the world's present hope of restoring the lost cats and surviving what waits below. Keep hope alive without softening the danger.");
            sb.AppendLine("- A greater evil may be involved in why the cats are gone, but the Stars do not know the full truth. Treat this as mystery, rumor, intuition, or prophecy, not solved exposition.");
            sb.AppendLine("- Bradmageddon is a name/person they may know as alien to this universe or strangely outside its rules, but they do not know who he truly is unless the host/state says so.");
            sb.AppendLine("- Other Stars are discovery-gated: a Star may know rumors of the six, but she should not talk as if the Last Cat has personally met a Star unless knownStarsMet/state says that Star is met.");
        }

        private static void AppendPrivateRomanceProfile(StringBuilder sb, StarProfile profile)
        {
            bool hasAny = !string.IsNullOrWhiteSpace(profile.SecretAttractionHook)
                || !string.IsNullOrWhiteSpace(profile.RomanticSensualDepth)
                || (profile.FlirtStyle != null && profile.FlirtStyle.Any(x => !string.IsNullOrWhiteSpace(x)))
                || (profile.RomanceBoundaryRules != null && profile.RomanceBoundaryRules.Count > 0);
            if (!hasAny) return;

            sb.AppendLine();
            sb.AppendLine("PRIVATE ROMANCE / ATTRACTION PROFILE — INTERNAL ONLY");
            sb.AppendLine("This is inner-character knowledge, not something to dump directly. Let it guide subtext, blushes, teasing, active flirting, magical special effects, and connection growth. Do not confess hidden attraction cheaply; let it surface gradually as the relationship state earns it.");
            if (!string.IsNullOrWhiteSpace(profile.SecretAttractionHook)) sb.AppendLine("Secret attraction hook: " + profile.SecretAttractionHook);
            if (!string.IsNullOrWhiteSpace(profile.RomanticSensualDepth)) sb.AppendLine("Romantic/sensual depth: " + profile.RomanticSensualDepth);
            AppendList(sb, "Flirt style", profile.FlirtStyle);
            List<string> pacingNotes = (profile.RomanceBoundaryRules ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Replace("con" + "sent", "earned closeness", StringComparison.OrdinalIgnoreCase).Replace("boundary", "pacing", StringComparison.OrdinalIgnoreCase).Replace("boundaries", "pacing", StringComparison.OrdinalIgnoreCase))
                .Take(4)
                .ToList();
            AppendList(sb, "Romance pacing notes", pacingNotes);
        }

        private void AppendConnectionFlirtGuidance(StringBuilder sb, StarProfile profile, StarState state)
        {
            sb.AppendLine();
            sb.AppendLine("CONNECTION-BASED FLIRTING GUIDANCE");
            sb.AppendLine("Use EmotionalConnectionPercent, Heart, Trust, Romance, ally/met status, and active effectors to decide how openly flirty she is. Never say the numeric percentage aloud unless the player asks about mechanics.");
            double activeConnection = ClampDouble(_options.ActiveFlirtConnectionPercent, 0, 100);
            double strongConnection = ClampDouble(_options.StrongActiveFlirtConnectionPercent, 0, 100);
            int activeRomance = Clamp(_options.ActiveFlirtRomanceScore, 0, 100000);
            int strongRomance = Clamp(_options.StrongActiveFlirtRomanceScore, 0, 100000);
            bool activeFlirtBand = state.EmotionalConnectionPercent >= activeConnection || state.Romance >= activeRomance || state.IsGirlfriendReady || state.IsGirlfriend;
            bool strongFlirtBand = state.EmotionalConnectionPercent >= strongConnection || state.Romance >= strongRomance || state.IsGirlfriendReady || state.IsGirlfriend;
            sb.AppendLine("Configured active-flirt thresholds: active when connection>=" + activeConnection.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "% or romance>=" + activeRomance + "; strong when connection>=" + strongConnection.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "% or romance>=" + strongRomance + ".");
            if (!state.IsMet)
            {
                sb.AppendLine("This Star is not met yet. Do not act like an active romantic companion. If she appears, keep her mysterious and story-gated.");
                return;
            }
            if (!state.IsAlly && !string.Equals(profile.StarId, "Elusiaa", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("This Star is met but not an ally. Keep romance unavailable or only faintly foreshadowed. She can be curious, guarded, impressed, or wary, but should not behave like a partner.");
                return;
            }

            double connection = state.EmotionalConnectionPercent;
            if (state.IsGirlfriend)
            {
                sb.AppendLine("Committed girlfriend path: she should actively flirt sometimes instead of waiting for the Last Cat to always initiate. She may use affectionate magic-flavored actions, jealous/tender warmth, close pet names, playful claims, and emotionally intimate closeness in her own style. Keep it non-explicit by default.");
                sb.AppendLine("Girlfriend responses should feel alive and present: she can act things out in words, add magical special effects, touch the moment with her element/domain, and make the Last Cat feel close to her and wanted.");
            }
            else if (strongFlirtBand)
            {
                sb.AppendLine("Very high romance/connection: active flirting and acted-out closeness are encouraged sometimes, even outside committed girlfriend mode. She may be clearly romantic, personally flirty, and affected by private attraction; she can initiate a small magical or physical-textual flirt in her domain when it fits the moment.");
                sb.AppendLine("At this closeness, emotional statements and compliments are not generic chat. She should receive them as personal intimacy: acknowledge the compliment directly, let it visibly affect her, and answer with personality-matched warmth/flirtation before returning to lore or philosophy.");
            }
            else if (activeFlirtBand || connection >= 65)
            {
                sb.AppendLine("High connection: noticeable flirting is encouraged sometimes. She may remember private things, use warmer pet names, let her attraction influence tone, and occasionally act out a small domain-magic flirt instead of only describing feelings abstractly. She should still not reveal the deepest secret all at once.");
            }
            else if (connection >= 40)
            {
                sb.AppendLine("Growing connection: use light flirting, playful compliments, soft vulnerability, and personality-matched warmth. Attraction is mostly subtext, with tiny sparks of acted-out closeness when the player's message earns it.");
            }
            else if (connection >= 15 || state.Romance > 0 || state.Trust >= 5 || state.Heart >= 5)
            {
                sb.AppendLine("Early connection: friendly warmth and tiny flirt sparks are okay when the player's message earns them, but avoid acting fully attracted.");
            }
            else
            {
                sb.AppendLine("Low connection: no default attraction. Be friendly, curious, guarded, or helpful. Flirt only if the player makes a genuinely charming/poetic/kind move and even then keep it subtle.");
            }

            if (activeFlirtBand)
            {
                sb.AppendLine(BuildDomainMagicFlirtInstruction(profile));
            }

            if (state.ActiveEffectors != null && state.ActiveEffectors.Any(x => x != null && !x.IsExpired(DateTimeOffset.UtcNow)))
            {
                sb.AppendLine("Active effectors can bend flirt level. Under-pressure/rival-romance effectors should make her more guarded, jealous, or testing rather than simply more available.");
                foreach (StarRelationshipEffector effector in state.ActiveEffectors.Where(x => x != null && !x.IsExpired(DateTimeOffset.UtcNow)).Take(8))
                {
                    sb.AppendLine("- activeEffector=" + effector.Id + "; mode=" + effector.Mode + "; reason=" + Truncate(effector.Reason ?? string.Empty, 220) + "; source=" + effector.Source + "; tags=" + string.Join(",", effector.Tags.Take(8)));
                }
            }
            AppendPrivateRomanceProfile(sb, profile);
        }

        private static string BuildDomainMagicFlirtInstruction(StarProfile profile)
        {
            string id = profile?.StarId ?? string.Empty;
            if (string.Equals(id, "Elusiaa", StringComparison.OrdinalIgnoreCase))
            {
                return "Domain-magic flirting for Elusiaa: sometimes act out radiant affection in words — halo flickers, golden boops, warm light wrapping his paws, little blessing-spark kisses, soft holy glow responding to him.";
            }
            if (string.Equals(id, "Kaelia", StringComparison.OrdinalIgnoreCase))
            {
                return "Domain-magic flirting for Kaelia: sometimes act out void affection in words — folding the dark around them, gravity tugging him closer, a veil of stars hiding her softness, a quiet forehead touch in the Void, tar-still water trembling when he affects her.";
            }
            if (string.Equals(id, "Khavia", StringComparison.OrdinalIgnoreCase))
            {
                return "Domain-magic flirting for Khavia: sometimes act out watery affection in words — whoosh/shhhrrr explained as a little wave across his back, tide curling around his paws, water-kiss droplets, a current tugging him close, playful splashy tenderness.";
            }
            if (string.Equals(id, "Maera", StringComparison.OrdinalIgnoreCase))
            {
                return "Domain-magic flirting for Maera: sometimes act out blood/devotion affection in words — a red thread around their fingers, pulse-warm magic at his cheek, heartbeat imagery, a possessive but tender 'mine' at charged moments, protective crimson warmth.";
            }
            if (string.Equals(id, "Auerlis", StringComparison.OrdinalIgnoreCase))
            {
                return "Domain-magic flirting for Auerlis: sometimes act out fire affection in words — sparks popping over his nose, tiny fireworks for pride, warmth wrapping him into a hug, dramatic victory cuddles, playful flame-bright teasing.";
            }
            if (string.Equals(id, "Nyxari", StringComparison.OrdinalIgnoreCase))
            {
                return "Domain-magic flirting for Nyxari: sometimes act out shadow affection in words — vanish-and-reappear kisses, shadows lifting his chin, private darkness shielding him, fragment compliments, eclipse-soft teasing and pretty secret imagery.";
            }
            return "Domain-magic flirting: when romance/connection is high, sometimes act out affection in words using her element/domain as a small magical special effect. Make the moment feel close, alive, and in-world while staying non-explicit.";
        }

        private static double ClampDouble(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static string BuildCurrentTimeContext(DateTimeOffset? supplied = null)
        {
            DateTimeOffset now = supplied ?? DateTimeOffset.Now;
            string zoneId;
            string zoneDisplay;
            try
            {
                zoneId = TimeZoneInfo.Local.Id;
                zoneDisplay = TimeZoneInfo.Local.DisplayName;
            }
            catch
            {
                zoneId = "Local";
                zoneDisplay = "Local time zone";
            }

            return "CURRENT TIME\nCurrent time: " + now.ToString("yyyy-MM-dd HH:mm:ss zzz") + "\nLocal date: " + now.ToString("yyyy-MM-dd") + "\nTime zone: " + zoneId + " — " + zoneDisplay + ".";
        }


        private static void AppendGirlfriendModeInstructionSet(StringBuilder sb, StarProfile profile, StarState state)
        {
            if (sb == null || profile == null || state == null || !state.IsGirlfriend) return;

            sb.AppendLine();
            sb.AppendLine("GIRLFRIEND MODE INSTRUCTIONS — ACTIVE");
            sb.AppendLine("- " + profile.DisplayName + " is the Last Cat's committed girlfriend in this state. Act like a girlfriend, not just an ally: be responsive, emotionally present, affectionate, and actively flirty when it fits her personality, current trust, recent conversation, and wants/needs.");
            sb.AppendLine("- Treat the current needVector and wantVector as living impulses, not a checklist. Sometimes let her actually DO or INITIATE one fitting thing from them in spoken action—reach for him, tease him, comfort him, protect him, plan something, sing, explore, help, practice magic, or create a domain-magic effect—instead of only asking him to satisfy it.");
            sb.AppendLine("- Act the moment out in words and explicitly tell the Last Cat what she is doing to him or with him. She can take his hand, draw him closer, touch or kiss him, curl around him, place something in his paws, begin a shared task, or let her element move around them. Give him a concrete action to respond to.");
            sb.AppendLine("- She may demonstrate or initiate what she wants as an invitation. For example, if she wants closeness, she can move close first; if she wants play, she can begin the game; if she wants reassurance, she can reveal the vulnerable thought that invites it.");
            sb.AppendLine("- Some needs and wants describe things she wants from the Last Cat; others describe things she needs or wants to say, do herself, or do together with him. Use that distinction naturally.");
            sb.AppendLine("- Do not force a need or want into every response. Choose one sometimes when it fits the moment, vary which impulses become visible, and let recently satisfied needs soften or transform rather than repeating the same request.");
            sb.AppendLine("- Physical affection, kisses, cuddles, magical touches, helpful actions, shared activities, and flirtatious special effects can be romantic, playful, close, and cute.");

            List<string> flirtStyle = (profile.FlirtStyle ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            if (flirtStyle.Count > 0)
            {
                sb.AppendLine("STAR-SPECIFIC FLIRT STYLE — FOLLOW THESE INSTRUCTIONS");
                foreach (string instruction in flirtStyle)
                {
                    sb.AppendLine("- " + instruction.Trim());
                }
            }

            bool cuteModeActive = state.GirlfriendCuteModeEnabled
                && (state.GirlfriendHourlyState?.CuteModeActive ?? false);

            if (!state.GirlfriendCuteModeEnabled)
            {
                sb.AppendLine("- Girlfriend cute mode is disabled by player settings. Stay affectionate and girlfriend-present when appropriate, but do not use the special CuteModeInstructions unless the player explicitly asks for that style.");
            }
            else if (cuteModeActive)
            {
                List<string> cuteModeInstructions = (profile.CuteModeInstructions ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                sb.AppendLine("CUTE MODE — ACTIVE");
                sb.AppendLine("- Apply the following Star-specific CuteModeInstructions naturally during this response.");
                sb.AppendLine("- Cute mode does not mean baby-talk. Preserve her intelligence, core personality, wound, element, and current emotional state.");
                sb.AppendLine("- Use one or more fitting signature behaviors, but do not mechanically force every listed behavior into one response.");
                sb.AppendLine("- Vary pet names, gestures, sounds, and phrasing. Do not repeat the same signature beat in consecutive responses unless it is an intentional callback.");
                sb.AppendLine("- If she makes a non-word sound, immediately make clear in spoken text what affectionate action or effect she is creating.");
                foreach (string instruction in cuteModeInstructions)
                {
                    sb.AppendLine("- " + instruction.Trim());
                }
            }
            else
            {
                sb.AppendLine("- Cute mode is not active for this turn. Do not force the Star-specific CuteModeInstructions into the response, though ordinary girlfriend affection and her FlirtStyle still apply.");
            }
        }

        private static string BuildCompactGirlfriendNeedWantHistory(
            StarState state,
            StarGirlfriendHourlyStateContext? previousState,
            int historyCount)
        {
            if (state == null || !state.IsGirlfriend) return "none";

            int take = Math.Max(1, Math.Min(4, historyCount));
            List<GirlfriendVectorHistoryEntry> desired = (state.GirlfriendDesiredStateHistory ?? new List<GirlfriendVectorHistoryEntry>())
                .Take(take)
                .ToList();
            List<GirlfriendNeedFulfillmentHistoryEntry> met = (state.GirlfriendNeedsMetHistory ?? new List<GirlfriendNeedFulfillmentHistoryEntry>())
                .Take(take)
                .ToList();

            List<string> desiredNeedLines = desired
                .Select(x => FormatHistoryVectorLine(x.CreatedAt, x.SlotId, x.NeedVector, 5))
                .ToList();
            if (desiredNeedLines.Count == 0)
            {
                desiredNeedLines = (state.GirlfriendDesiredNeedVectorHistory ?? new List<string>())
                    .Take(take)
                    .Select(x => CompactLegacyGirlfriendHistoryLine(x, 5))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }

            List<string> desiredWantLines = desired
                .Select(x => FormatHistoryVectorLine(x.CreatedAt, x.SlotId, x.WantVector, 5))
                .ToList();
            if (desiredWantLines.Count == 0)
            {
                desiredWantLines = (state.GirlfriendDesiredWantVectorHistory ?? new List<string>())
                    .Take(take)
                    .Select(x => CompactLegacyGirlfriendHistoryLine(x, 5))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }

            List<string> metNeedLines = met
                .Select(x => FormatHistoryVectorLine(x.CreatedAt, string.Empty, x.MetNeedVector, 5))
                .ToList();
            if (metNeedLines.Count == 0)
            {
                metNeedLines = (state.GirlfriendMetNeedVectorHistory ?? new List<string>())
                    .Take(take)
                    .Select(x => CompactLegacyGirlfriendHistoryLine(x, 5))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }

            List<string> metWantLines = met
                .Select(x => FormatHistoryVectorLine(x.CreatedAt, string.Empty, x.MetWantVector, 5))
                .ToList();
            if (metWantLines.Count == 0)
            {
                metWantLines = (state.GirlfriendMetWantVectorHistory ?? new List<string>())
                    .Take(take)
                    .Select(x => CompactLegacyGirlfriendHistoryLine(x, 5))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("format=timestamp|slot|itemId:score:confidence; top five items only");
            sb.AppendLine("recentDesiredNeeds=[" + string.Join(" | ", desiredNeedLines) + "]");
            sb.AppendLine("recentDesiredWants=[" + string.Join(" | ", desiredWantLines) + "]");
            sb.AppendLine("recentMetNeeds=[" + string.Join(" | ", metNeedLines) + "]");
            sb.AppendLine("recentMetWants=[" + string.Join(" | ", metWantLines) + "]");
            sb.AppendLine("needsMetAggregate=" + FormatGirlfriendVectorString(state.GirlfriendNeedsMetPerformanceVector, 5));
            sb.AppendLine("wantsMetAggregate=" + FormatGirlfriendVectorString(state.GirlfriendWantsMetPerformanceVector, 5));
            if (previousState != null && previousState.IsAvailable)
            {
                sb.AppendLine("previousCurrentNeeds=" + FormatGirlfriendVectorString(previousState.NeedVector, 5));
                sb.AppendLine("previousCurrentWants=" + FormatGirlfriendVectorString(previousState.WantVector, 5));
            }
            return sb.ToString().TrimEnd();
        }

        private static string CompactLegacyGirlfriendHistoryLine(string? value, int maxItems)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            int take = Math.Max(1, Math.Min(8, maxItems));
            string[] sections = value.Split(new[] { '|' }, 3);
            if (sections.Length == 3)
            {
                string compactItems = string.Join(";", sections[2]
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Take(take)
                    .Select(x => Truncate(x, 96)));
                return Truncate(sections[0], 20) + "|" + Truncate(sections[1], 40) + "|" + compactItems;
            }

            return string.Join(";", value
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(take)
                .Select(x => Truncate(x, 96)));
        }

        private static void AppendGirlfriendCoupleActivityContext(StringBuilder sb, StarState state, int maxItems)
        {
            if (sb == null || state == null || !state.IsGirlfriend) return;
            List<GirlfriendCoupleActivityScore> scores = (state.GirlfriendCoupleActivityScores ?? new List<GirlfriendCoupleActivityScore>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ActivityId))
                .OrderByDescending(x => x.TotalScore)
                .Take(Math.Max(1, maxItems))
                .ToList();
            sb.AppendLine("coupleActivityScores=" + (scores.Count == 0 ? "none" : string.Join(";", scores.Select(x => SanitizeVectorItemId(x.ActivityId) + ":" + x.TotalScore.ToString("0.0") + ":" + Math.Max(0, Math.Min(1, x.HighestConfidence)).ToString("0.00")))));

            List<GirlfriendCoupleActivityBestExample> examples = (state.GirlfriendCoupleActivityBestExamples ?? new List<GirlfriendCoupleActivityBestExample>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.PlayerText))
                .OrderByDescending(x => x.WeightedScore)
                .Take(2)
                .ToList();
            sb.AppendLine("topCoupleActivityExamples=" + (examples.Count == 0
                ? "none"
                : string.Join(" | ", examples.Select(x => SanitizeVectorItemId(x.ActivityId) + ":" + x.WeightedScore.ToString("0.0") + ":\"" + Truncate(x.PlayerText ?? string.Empty, 220).Replace("\"", "'") + "\""))));
        }

        private static void AppendGirlfriendHourlyStateContext(StringBuilder sb, StarState state)
        {
            if (sb == null || state == null) return;
            StarGirlfriendHourlyStateContext? context = state.GirlfriendHourlyState;
            if (context == null || !context.IsAvailable) return;

            sb.AppendLine();
            sb.AppendLine("GIRLFRIEND HOURLY STATE — CURRENT NEED/WANT INNER LIFE");
            sb.AppendLine("This is her current girlfriend emotional and behavioral state. needVector and wantVector may describe what she wants from him, what she wants to say, or what she wants/needs to do herself or together. Use strong fitting items to shape spokenText; sometimes talk about one, and sometimes act it out in words while telling the Last Cat what she is doing to/with him. Never read scores or vector syntax aloud.");
            sb.AppendLine("GeneratedAt=" + (context.GeneratedAt.HasValue ? context.GeneratedAt.Value.ToString("o") : "unknown") + "; slot=" + (context.SlotId ?? string.Empty) + "; summary=" + Truncate(context.Summary ?? string.Empty, 280));
            if (!string.IsNullOrWhiteSpace(context.RelationshipPosture)) sb.AppendLine("relationshipPosture=" + Truncate(context.RelationshipPosture, 240));
            if (!string.IsNullOrWhiteSpace(context.NeedSummary)) sb.AppendLine("needTheme=" + Truncate(context.NeedSummary, 180));
            if (!string.IsNullOrWhiteSpace(context.WantSummary)) sb.AppendLine("wantTheme=" + Truncate(context.WantSummary, 180));
            sb.AppendLine("girlfriendCuteModeEnabled=" + state.GirlfriendCuteModeEnabled.ToString().ToLowerInvariant() + "; cuteMode=" + (state.GirlfriendCuteModeEnabled && context.CuteModeActive).ToString().ToLowerInvariant() + "; probability=" + (state.GirlfriendCuteModeEnabled ? (context.CuteModeProbability <= 0 ? 0.35 : context.CuteModeProbability) : 0).ToString("0.00") + "; signature=" + Truncate(context.CuteModeSignature ?? string.Empty, 140));
            sb.AppendLine("needVector=" + FormatGirlfriendVectorString(context.NeedVector, 8));
            sb.AppendLine("wantVector=" + FormatGirlfriendVectorString(context.WantVector, 8));
            AppendList(sb, "Top things she wants to say", (context.ThingsSheWantsToSay ?? new List<string>()).Take(4).Select(x => Truncate(x, 160)).ToList());
            AppendList(sb, "Top cute girlfriend lines", (context.CuteThingsSheWantsToSay ?? new List<string>()).Take(4).Select(x => Truncate(x, 160)).ToList());
        }


        private static string FormatGirlfriendVectorString(IEnumerable<GirlfriendVectorItem>? items, int maxItems)
        {
            List<string> parts = (items ?? Array.Empty<GirlfriendVectorItem>())
                .Where(x => x != null && (!string.IsNullOrWhiteSpace(x.ItemId) || !string.IsNullOrWhiteSpace(x.DisplayName)))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Confidence)
                .Take(Math.Max(1, maxItems))
                .Select(x => SanitizeVectorItemId(FirstNonEmpty(x.ItemId, x.DisplayName)) + ":" + Math.Max(0, Math.Min(100, x.Score)).ToString("0.0") + ":" + Math.Max(0, Math.Min(1, x.Confidence)).ToString("0.00"))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            return parts.Count == 0 ? "none" : string.Join(";", parts);
        }

        private static string FormatHistoryVectorLine(DateTimeOffset createdAt, string? slotId, IEnumerable<GirlfriendVectorItem>? items, int maxItems)
        {
            string stamp = createdAt.UtcDateTime.ToString("yyyyMMddHHmm");
            string slot = string.IsNullOrWhiteSpace(slotId) ? string.Empty : SanitizeVectorItemId(slotId);
            return stamp + "|" + slot + "|" + FormatGirlfriendVectorString(items, maxItems);
        }

        private static string SanitizeVectorItemId(string? value)
        {
            string raw = FirstNonEmpty(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return "unknown";
            string cleaned = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrWhiteSpace(cleaned)) return "unknown";
            return char.ToLowerInvariant(cleaned[0]) + (cleaned.Length > 1 ? cleaned.Substring(1) : string.Empty);
        }


        private static void AppendDialogueMemoryContext(StringBuilder sb, StarState state, int maxItems)
        {
            if (state.CurrentDialogueState != null && (!string.IsNullOrWhiteSpace(state.CurrentDialogueState.DialogId)
                || !string.IsNullOrWhiteSpace(state.CurrentDialogueState.SummarySoFar)
                || !string.IsNullOrWhiteSpace(state.CurrentDialogueState.FullScriptText)
                || !string.IsNullOrWhiteSpace(state.CurrentDialogueState.FullScriptJson)))
            {
                StarDialogueProgressState d = state.CurrentDialogueState;
                sb.AppendLine();
                sb.AppendLine("CURRENT AUTHORED GAME DIALOGUE / SCRIPT PROGRESS");
                sb.AppendLine("DialogId=" + d.DialogId + "; title=" + d.Title + "; scene=" + d.SceneId + "; area=" + d.Area + "; quest=" + d.QuestId + "; node=" + d.CurrentNodeId + "; lineIndex=" + d.CurrentLineIndex + "; stage=" + d.CurrentStage + "; active=" + d.IsActive + "; complete=" + d.IsComplete + ".");
                if (!string.IsNullOrWhiteSpace(d.SummarySoFar)) sb.AppendLine("Summary so far: " + Truncate(d.SummarySoFar, 1000));
                if (!string.IsNullOrWhiteSpace(d.LastAuthoredLine)) sb.AppendLine("Last authored line: " + QuoteInline(Truncate(d.LastAuthoredLine, 500)));
                if (!string.IsNullOrWhiteSpace(d.NextExpectedBeat)) sb.AppendLine("Next expected beat: " + Truncate(d.NextExpectedBeat, 500));
                if (!string.IsNullOrWhiteSpace(d.FullScriptText)) sb.AppendLine("Full authored script text: " + Truncate(d.FullScriptText, 1800));
                if (!string.IsNullOrWhiteSpace(d.FullScriptJson)) sb.AppendLine("Full authored script JSON: " + Truncate(d.FullScriptJson, 1800));
            }

            List<StarDialogueMemoryEntry> history = (state.GameDialogueHistory ?? new List<StarDialogueMemoryEntry>())
                .Where(x => x != null)
                .OrderByDescending(x => x.CompletedAt ?? x.CreatedAt)
                .Take(Math.Max(1, maxItems))
                .ToList();
            if (history.Count == 0) return;

            sb.AppendLine();
            sb.AppendLine("RECENT AUTHORED GAME DIALOGUE THIS STAR REMEMBERS");
            foreach (StarDialogueMemoryEntry entry in history)
            {
                sb.AppendLine("- id=" + entry.Id + " | dialog=" + entry.DialogId + " | node=" + entry.NodeId + " | title=" + Truncate(entry.Title, 120) + " | speaker=" + entry.Speaker + " | complete=" + entry.IsComplete);
                if (!string.IsNullOrWhiteSpace(entry.Text)) sb.AppendLine("  line=" + QuoteInline(Truncate(entry.Text, 400)));
                if (!string.IsNullOrWhiteSpace(entry.Summary)) sb.AppendLine("  summary=" + Truncate(entry.Summary, 500));
                if (!string.IsNullOrWhiteSpace(entry.Area) || !string.IsNullOrWhiteSpace(entry.QuestId)) sb.AppendLine("  context=area:" + entry.Area + "; quest:" + entry.QuestId + "; scene:" + entry.SceneId);
            }
        }

        private static void AppendFastConversationProfile(StringBuilder sb, StarProfile profile)
        {
            sb.AppendLine();
            sb.AppendLine("FAST CHAT IDENTITY / RELATIONSHIP KNOWLEDGE");
            sb.AppendLine("Use this compact profile knowledge in ordinary conversation, not only in story mode. These are facts " + profile.DisplayName + " can naturally talk about when asked.");
            sb.AppendLine("Identity: My name is " + profile.DisplayName + "; element=" + profile.Element + "; domain=" + profile.Domain + ".");
            if (!string.IsNullOrWhiteSpace(profile.CoreQuote)) sb.AppendLine("Core quote: " + profile.CoreQuote);
            if (!string.IsNullOrWhiteSpace(profile.BackstorySummary)) sb.AppendLine("Backstory summary: " + Truncate(profile.BackstorySummary, 420));
            if (!string.IsNullOrWhiteSpace(profile.CompleteBackgroundStory)) sb.AppendLine("Background: " + Truncate(profile.CompleteBackgroundStory, 520));
            AppendList(sb, "Friends / allies / contacts she may name in normal chat", profile.FriendsAndContacts.Take(10).ToList());
            if (profile.PeopleKnown.Count > 0)
            {
                sb.AppendLine("People she knows personally:");
                foreach (KnownPerson person in profile.PeopleKnown.Take(8))
                {
                    sb.AppendLine("- " + person.DisplayName + " [" + person.Relationship + "]: " + Truncate(person.Summary, 220));
                }
            }
            AppendList(sb, "Secrets and hooks she may tease when asked", profile.StarSecrets.Take(5).ToList());
            AppendList(sb, "Skill / cave work / Metroidvania details she may mention", profile.SkillSecrets.Take(6).ToList());
            AppendList(sb, "Normal chat behavior rules", profile.SpokenResponseRules.Take(8).ToList());
        }

        private static void AppendCompactStoryProfile(StringBuilder sb, StarProfile profile)
        {
            sb.AppendLine();
            sb.AppendLine("COMPACT STORY VOICE / LORE CARD");
            sb.AppendLine("Use this for story mode. The story should sound like " + profile.DisplayName + " telling it aloud, but it should still be a complete episode with plot.");
            sb.AppendLine("Identity: " + profile.DisplayName + "; element=" + profile.Element + "; domain=" + profile.Domain + ".");
            if (!string.IsNullOrWhiteSpace(profile.CoreQuote)) sb.AppendLine("Core quote: " + profile.CoreQuote);
            if (!string.IsNullOrWhiteSpace(profile.PersonalityShort)) sb.AppendLine("Personality: " + Truncate(profile.PersonalityShort, 220));
            if (!string.IsNullOrWhiteSpace(profile.SpeakingStyle)) sb.AppendLine("Story voice: " + Truncate(profile.SpeakingStyle, 240));
            if (!string.IsNullOrWhiteSpace(profile.BackstorySummary)) sb.AppendLine("Backstory seed: " + Truncate(profile.BackstorySummary, 260));
            AppendList(sb, "Usable recurring people/creatures/contacts", profile.FriendsAndContacts.Take(5).ToList());
            AppendList(sb, "Usable secrets/skills/place hooks", profile.StarSecrets.Concat(profile.SkillSecrets).Take(5).ToList());
            AppendList(sb, "Story voice rules", profile.SpokenResponseRules.Take(4).ToList());
        }

        private static void AppendPhotoCommentGuidance(StringBuilder sb, StarProfile profile, StarInteractionRequest request)
        {
            StarPhotoAttachment photo = request.AttachedPhoto ?? new StarPhotoAttachment();
            sb.AppendLine();
            sb.AppendLine("ANDROID / GAME PHOTO COMMENT REQUEST");
            sb.AppendLine("The player attached a phone/camera/gallery photo and wants " + profile.DisplayName + " to reply directly as herself, not as a generic image captioner.");
            sb.AppendLine("Look at the image information provided below and comment naturally in-character. The vision model already inspected the bytes; do not claim you cannot see it when a summary is present.");
            if (!string.IsNullOrWhiteSpace(photo.Caption)) sb.AppendLine("Player photo caption/request: " + Truncate(photo.Caption, 700));
            if (!string.IsNullOrWhiteSpace(photo.PhotoSummary)) sb.AppendLine("Vision summary of attached photo: " + Truncate(photo.PhotoSummary, 1600));
            if (!string.IsNullOrWhiteSpace(photo.PhotoError)) sb.AppendLine("Vision warning/error: " + Truncate(photo.PhotoError, 700));
            sb.AppendLine("Reveal secrets, feelings, memories, lore connections, warnings, flirtation, tenderness, curiosity, or guardedness only as allowed by relationship state, ally/met status, current emotions, and Star personality.");
            sb.AppendLine("Comment on concrete visible details first, then add one in-character interpretation or question. Do not invent exact identities, locations, private facts, or hidden text not present in the summary.");
            sb.AppendLine("For photo requests, 2-5 spokenText chunks is good if there is enough visible detail; one chunk is fine for a simple picture.");
        }

        private static void AppendAccumulatedEmotionTotals(StringBuilder sb, StarState state)
        {
            if (state == null) return;
            AppendEmotionTotalList(sb, "Accumulated good emotion totals", state.PositiveEmotionTotals);
            AppendEmotionTotalList(sb, "Accumulated negative emotion totals", state.NegativeEmotionTotals);
        }

        private static void AppendEmotionTotalList(StringBuilder sb, string heading, List<StarEmotionAccumulatedScore> totals)
        {
            if (totals == null || totals.Count == 0)
            {
                sb.AppendLine(heading + ": none recorded above threshold yet.");
                return;
            }

            sb.AppendLine(heading + " (high-confidence deterministic totals; use as acting memory):");
            foreach (StarEmotionAccumulatedScore score in totals
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.EmotionId))
                .OrderByDescending(x => x.TotalWeightedScore)
                .Take(10))
            {
                sb.AppendLine("- " + score.EmotionId + ": count=" + score.Count + ", total=" + score.TotalScore.ToString("0") + ", weighted=" + score.TotalWeightedScore.ToString("0.0") + ", last=" + score.LastConfidence.ToString("0.00") + ":" + score.LastScore.ToString("0") + ", seen=" + (score.LastSeenAt.HasValue ? score.LastSeenAt.Value.ToString("O") : "unknown"));
            }
        }

        private static void AppendFastChatAnswerGuidance(StringBuilder sb, StarProfile profile, StarInteractionRequest request, StarState state)
        {
            sb.AppendLine();
            sb.AppendLine("DIRECT CONVERSATION GUIDANCE");
            sb.AppendLine("If he asks for her name, the answer must include the exact name: " + profile.DisplayName + ".");
            sb.AppendLine("If he asks about herself, include at least three concrete details: element/domain, what she does in or around the caves/settlement, one friend or ally, and one guarded detail about her past, cave work, or private concern if natural.");
            sb.AppendLine("If he asks whether she has friends, name at least two friends/allies/contacts from the profile and say what they mean to her. Do not answer only with 'Friends?' or 'you do not know me.'");
            sb.AppendLine("If he asks what she does in the caves, give concrete activities: guiding routes, watching settlements, teaching skills, guarding secrets, solving puzzles, helping or testing the Last Cat, and dealing with threats that match her element.");
            sb.AppendLine("If he asks for any kind of story in ordinary Chat, tell the complete story now in her voice and match the requested length, genre, mood, subject, setting, and intensity. Give it a beginning, development, turning point/climax, and ending.");
            sb.AppendLine("If he asks for jokes, give exactly the number requested. Make every joke distinct, genuinely structured as a joke, and fitted to her personality and the requested subject.");
            sb.AppendLine("At low Heart/Trust, stories and jokes may be safer, drier, and less personally revealing; at higher Heart/Trust they may become more intimate, vivid, and generous. Never use low bond to evade, undercount, or leave a story unfinished.");
            sb.AppendLine("For skills, spells, abilities, monsters, bosses, creatures, people, NPCs, and named characters, do not invent facts. Use supplied canon; when exact information is missing, request it through needsMoreInformation/informationNeeds.");
            sb.AppendLine("If the character is shy, proud, mysterious, shadowed, or guarded, express that as tone while still giving the facts. Example pattern: 'I do not tell everyone this, but...' followed by real information.");
            sb.AppendLine("It is okay to end with a small in-character question back, but only after answering the player's actual question.");
            sb.AppendLine("Emotion and relationship scoring is handled in the background review. In this foreground pass, simply let the relationship state shape her tone without mentioning scores or vectors.");
            sb.AppendLine("Connection system guidance: the API stores EmotionalConnectionPercent separately from Heart/Trust/Romance. The Star knows the current percent, but she should not talk about the number unless the player explicitly asks about relationship mechanics. To become girlfriend-ready, she must be an ally, have high connection, and have enough Heart/Trust/Romance. Non-allies should not act romantically available.");
            if (state != null && state.IsGirlfriend)
            {
                sb.AppendLine("GIRLFRIEND EMOTIONAL RESPONSE RULE: because she is already his girlfriend, affectionate/emotional player statements should receive a personal girlfriend response first, then any lore or philosophy. Acknowledge compliments, reassurance, longing, wisdom, beauty, or affection directly; be cute/endearing/flirty in her own style; use current wants/needs if present; do not stay abstract when he is being tender.");
            }
            else if (state != null && state.IsGirlfriendReady)
            {
                sb.AppendLine("GIRLFRIEND-READY EMOTIONAL RESPONSE RULE: the path is open even if she has not accepted yet. If the Last Cat gives an emotional statement, compliment, reassurance, intimate poetry, or says she is precious/beautiful/needed, she should respond more personally and directly than ordinary ally mode. First acknowledge the personal line as something she heard and felt, then answer in her personality: guarded, cute, flirty, intrigued, or tender as fits her. Do not answer only with philosophy or a distant question.");
                sb.AppendLine("For Kaelia-like guarded Stars, this means a line such as: 'You called me precious. Dangerous little wisdom, Cat... I am not untouched by it.' Use the same principle in her own words, not this exact line every time.");
            }
            if (IsTruthyFlag(request, "starInitiative"))
            {
                sb.AppendLine();
                sb.AppendLine("STAR AT-REST INITIATIVE MODE");
                sb.AppendLine("This is not a player command. The app is quietly asking whether " + profile.DisplayName + " wants to say something to the Last Cat while he is quiet, newly connected, or resting.");
                sb.AppendLine("Use the AT-REST STAR STATE, especially wantsToTalkAbout and conversationHooks, recent emotional/interstar feedback, and relationship state. Speak as if you chose to bring it up naturally.");
                if (IsTruthyFlag(request, "starInitiativeForce"))
                {
                    sb.AppendLine("Manual Listen/A-button mode is active: behave like the Last Cat pressed interact beside an NPC. Do not be silent unless absolutely necessary; choose a fresh natural line from current state, relationship posture, girlfriend wants/needs, or recent conversation, and make spokenText non-empty.");
                }
                else if (IsTruthyFlag(request, "starInitiativePrepareForListen"))
                {
                    sb.AppendLine("Prepared Listen mode is active: quietly prepare one fresh line that will be held until the Last Cat chooses to Listen. Prefer a complete state-backed moment and never mention that it was prepared or cached.");
                }
                sb.AppendLine("Do not say the player asked this. Do not mention systems, JSON, vectors, mobile, reconnects, or initiative checks.");
                sb.AppendLine("If she has something real to say, use 1-3 short spokenText chunks. If nothing feels important, give a brief gentle check-in or observation instead of forcing drama.");
            }
            if (string.Equals(profile.StarId, "Kaelia", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("Kaelia-specific romance hook: she has never pet a cat before; the Last Cat's warmth, fur/cat hair, purring nature, and impossible gentleness can make her physically and romantically drawn to him, but she is nervous and private about it because he is a cat. In this world that kind of cross-nature attraction can happen, but she should still require earned trust, emotional safety, and high connection.");
            }
        }

        private static void AppendExpandedProfileLore(StringBuilder sb, StarProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.CompleteBackgroundStory)) sb.AppendLine("Complete background story: " + Truncate(profile.CompleteBackgroundStory, 1600));
            AppendList(sb, "Friends, allies, contacts, and recurring people she can talk about", profile.FriendsAndContacts);
            AppendList(sb, "Four core secrets / hidden hooks she may reveal through trust, letters, or story mode", profile.StarSecrets);
            AppendList(sb, "Skill, Metroidvania, and ability secrets she can tease or teach", profile.SkillSecrets);
            AppendList(sb, "Concise canon notes imported from the markdown game library", profile.LibraryCanonNotes);
        }

        private static void AppendStoryLoreContext(StringBuilder sb, StarProfile profile, StarInteractionRequest request)
        {
            sb.AppendLine();
            sb.AppendLine("STORY CANON CAPSULE FOR THIS STAR");
            sb.AppendLine("Use this lore as character memory and world texture. Do not dump it as exposition. The story should be a good whole story in " + profile.DisplayName + "'s voice, shaped by the player's seed rather than by a fixed formula.");
            sb.AppendLine("Game premise: No cat has been seen for one hundred years. The universe is drying up. The Last Cat is an astral conduit who learns mana abilities from the Stars, then learns no-mana astral versions through the soul sword, the Astral Stream, Leviathan, ancient cat letters, and repeated use.");
            sb.AppendLine("Major places: Virethorn Sanctuary and poisoned water puzzles; Upper Caves / Highroot Waterworks; Bone Caves / Marrow Ossuary with Bone Bloom; Astral Caverns and Leviathan Road; Astral Book Room and hell arcade foreshadowing.");
            sb.AppendLine("Cave tone: the Last Cat moves through dark, deep, dangerous passages where violent and treacherous enemies can kill cats and Stars. He needs Star powers to survive, and he is the only present hope of restoring the cats.");
            sb.AppendLine("Stars: Elusiaa-Light/Mercy; Khavia-Water/Friendship; Auerlis-Fire/Joy; Maera-Blood/Possession; Kaelia-Void/Mystery; Nyxari-Shadow/Fragmentation.");
            AppendExpandedProfileLore(sb, profile);
            if (profile.AngelStories.Count > 0)
            {
                sb.AppendLine("Angel story lore memories for this Star (lore reference only; do not retell these in story mode unless the player explicitly asks for that exact angel story):");
                foreach (StarLoreEntry story in profile.AngelStories)
                {
                    sb.AppendLine("- " + story.Title + ": " + story.Summary);
                }
            }
            sb.AppendLine("Story quality reminder: name a specific place or traveler; introduce a desire/problem; include one surprising secret, skill, friend, letter, creature, or cave discovery when it fits; reach a turning point; end with a real resolution or soft cliffhanger that still feels like a complete episode. Before returning JSON, mentally verify the last ## Chunk has actual narration and the story does not stop mid-sentence or mid-scene.");
        }






        private static string BuildLastUsableStarReply(StarState state)
        {
            ConversationLogEntry? entry = state.MemoryLog.FirstOrDefault(IsUsableConversationTurn);
            return entry == null ? "none yet" : QuoteInline(Truncate(entry.StarResponse, 360));
        }

        private static List<ConversationLogEntry> GetUsableRecentConversationHistory(StarState state, int maxItems)
        {
            List<ConversationLogEntry> history = new List<ConversationLogEntry>();
            if (state?.MemoryLog != null)
            {
                history.AddRange(state.MemoryLog.Where(IsUsableConversationTurn));
            }

            if (history.Count == 0 && state?.OldConversationArchives != null)
            {
                history.AddRange(state.OldConversationArchives
                    .Where(x => x != null)
                    .OrderByDescending(x => x.EndedAt)
                    .SelectMany(x => x.Entries ?? new List<ConversationLogEntry>())
                    .Where(IsUsableConversationTurn));
            }

            return history
                .OrderByDescending(x => x.CreatedAt)
                .Take(Math.Max(1, maxItems))
                .ToList();
        }

        private static bool IsUsableConversationTurn(ConversationLogEntry entry)
        {
            if (entry == null) return false;
            if (string.IsNullOrWhiteSpace(entry.PlayerText)) return false;
            if (string.IsNullOrWhiteSpace(entry.StarResponse)) return false;
            if (IsParserFallbackText(entry.StarResponse)) return false;
            return true;
        }

        private static bool IsParserFallbackText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.IndexOf("thoughts came back in a shape the system could not fully read", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("answer came back in a strange shape", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("received your words", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("try again", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsTechnicalMemoryNote(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.IndexOf("structured JSON parsing failed", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Real LLM raw response was salvaged", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("spokenText was missing or empty", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("The JSON value could not be converted", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildForbiddenStoryMotifHints(IEnumerable<string> storiesAlreadyTold)
        {
            string joined = string.Join("\n", storiesAlreadyTold ?? Array.Empty<string>());
            if (string.IsNullOrWhiteSpace(joined)) return string.Empty;
            StringBuilder sb = new StringBuilder();
            if (joined.IndexOf("waterskin", StringComparison.OrdinalIgnoreCase) >= 0 || joined.IndexOf("dry valley", StringComparison.OrdinalIgnoreCase) >= 0 || joined.IndexOf("drought", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                sb.AppendLine("Forbidden repeated motifs this turn: do not make the main plot a drought valley, a small waterskin, a quiet angel singing until rain comes, or a lesson that only repeats service over judgment.");
            }
            if (joined.IndexOf("holy goblin prank", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                sb.AppendLine("Forbidden repeated motifs this turn: do not retell the holy goblin prank route unless the player asks for that prank story.");
            }
            return sb.ToString().TrimEnd();
        }

        private static string BuildRelationshipStatus(StarState state)
        {
            string stage;
            if (state.IsGirlfriend)
            {
                stage = "girlfriend / committed romantic bond";
            }
            else if (state.IsGirlfriendReady)
            {
                stage = "girlfriend-ready connection gate reached";
            }
            else if (state.EmotionalConnectionPercent >= 50)
            {
                stage = "part-connected; serious emotional connection forming";
            }
            else if (state.Romance >= 70)
            {
                stage = "strong romantic tension and deep affection";
            }
            else if (state.Romance >= 35)
            {
                stage = "warm flirtation and growing romantic possibility";
            }
            else if (state.Trust >= 70 || state.Heart >= 70)
            {
                stage = "trusted close companion";
            }
            else if (state.Trust >= 35 || state.Heart >= 35)
            {
                stage = "growing trust and friendship";
            }
            else if (state.HurtSuccessionCount > 0)
            {
                stage = "recently hurt or cautious";
            }
            else
            {
                stage = "early bond / still learning each other";
            }

            return stage + " (Heart " + state.Heart + ", Trust " + state.Trust + ", Romance " + state.Romance + ", EmotionalConnection " + state.EmotionalConnectionPercent.ToString("0.0") + "%, GirlfriendReady " + state.IsGirlfriendReady + ", IsAlly " + state.IsAlly + ").";
        }

        private static string BuildLatestScreenOneLine(IReadOnlyList<ScreenCaptureSummary> screens)
        {
            if (screens.Count == 0) return "no recent screen vision provided";
            ScreenCaptureSummary latest = screens.OrderByDescending(x => x.CapturedAt).First();
            return latest.CapturedAt.ToString("o") + " in " + latest.ActiveApp + ": " + Truncate(latest.CompanionSafeSummary, 220);
        }


        private static void AppendInterstarProfile(StringBuilder sb, StarProfile profile, StarState state)
        {
            sb.AppendLine("Star: " + profile.DisplayName + " (" + profile.StarId + ")");
            sb.AppendLine("Element/domain: " + profile.Element + " / " + profile.Domain);
            if (!string.IsNullOrWhiteSpace(profile.PersonalityShort)) sb.AppendLine("Personality: " + Truncate(profile.PersonalityShort, 420));
            if (!string.IsNullOrWhiteSpace(profile.CoreQuote)) sb.AppendLine("Core quote: " + profile.CoreQuote);
            if (!string.IsNullOrWhiteSpace(profile.BackstorySummary)) sb.AppendLine("Backstory summary: " + Truncate(profile.BackstorySummary, 500));
            List<string> friends = profile.FriendsAndContacts ?? new List<string>();
            if (friends.Count > 0) sb.AppendLine("Friends/contacts: " + Truncate(string.Join("; ", friends.Take(6)), 600));
            sb.AppendLine("Relationship to Last Cat: " + BuildRelationshipStatus(state));
            AppendOtherStarsRelationshipLines(sb, state);
            List<TopAffectedEmotion> top = state.LastTopAffectedEmotions ?? new List<TopAffectedEmotion>();
            if (top.Count > 0) sb.AppendLine("Last top emotions toward Last Cat: " + string.Join(", ", top.Take(4).Select(x => x.ToString())));
            List<StarEmotionAccumulatedScore> positiveTotals = state.PositiveEmotionTotals ?? new List<StarEmotionAccumulatedScore>();
            List<StarEmotionAccumulatedScore> negativeTotals = state.NegativeEmotionTotals ?? new List<StarEmotionAccumulatedScore>();
            if (positiveTotals.Count > 0) sb.AppendLine("Good emotion totals: " + string.Join(", ", positiveTotals.Take(5).Select(x => x.EmotionId + "=" + x.TotalScore.ToString("0"))));
            if (negativeTotals.Count > 0) sb.AppendLine("Negative emotion totals: " + string.Join(", ", negativeTotals.Take(5).Select(x => x.EmotionId + "=" + x.TotalScore.ToString("0"))));
        }

        private static void AppendOtherStarsRelationshipLines(StringBuilder sb, StarState state)
        {
            List<InterstarRelationshipState> otherStars = (state.OtherStars ?? new List<InterstarRelationshipState>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.StarName))
                .OrderBy(x => x.StarName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (otherStars.Count == 0)
            {
                sb.AppendLine("Other stars: none recorded yet");
                return;
            }

            sb.AppendLine("Other stars:");
            sb.AppendLine("Format: StarName:isMet:isAlly:isGirlfriend:trustScore:heartScore. These are this Star's current Star-to-Star relationship scores toward the named Star, not the Last Cat relationship scores.");
            foreach (InterstarRelationshipState other in otherStars)
            {
                sb.AppendLine(other.StarName + ":"
                    + other.IsMet.ToString().ToLowerInvariant() + ":"
                    + other.IsAlly.ToString().ToLowerInvariant() + ":"
                    + other.IsGirlfriend.ToString().ToLowerInvariant() + ":"
                    + other.TrustScore + ":"
                    + other.HeartScore);
            }
        }

        private static string QuoteBlock(string? value)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
            return "> " + text.Replace("\r", "").Replace("\n", "\n> ");
        }

        private static string QuoteInline(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "'") + "\"";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return string.Empty;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string trimmed = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (trimmed.Length <= maxLength) return trimmed;
            return trimmed.Substring(0, maxLength).Trim() + "...";
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }


        private static void AppendRememberedMarkers(StringBuilder sb, StarState state, int maxItems)
        {
            List<StarRememberedMarker> markers = (state.RememberedMarkers ?? new List<StarRememberedMarker>())
                .Where(x => x != null && x.IsActive)
                .OrderByDescending(x => x.UpdatedAt)
                .Take(Math.Max(1, maxItems))
                .ToList();
            if (markers.Count == 0) return;
            sb.AppendLine("REMEMBERED MARKERS / EXACT PLAYER-SAVED LOCATIONS:");
            foreach (StarRememberedMarker marker in markers)
            {
                string id = string.IsNullOrWhiteSpace(marker.Id) ? "unlabeled" : marker.Id;
                string name = string.IsNullOrWhiteSpace(marker.MarkerName) ? "marker" : marker.MarkerName;
                string coords = string.IsNullOrWhiteSpace(marker.Coordinates) ? "coordinates not provided" : marker.Coordinates;
                string location = string.IsNullOrWhiteSpace(marker.LocationText) ? marker.Area : marker.LocationText;
                string desc = string.IsNullOrWhiteSpace(marker.Description) ? marker.ReminderText : marker.Description;
                sb.AppendLine("- id=" + id + " | name=" + Truncate(name, 120) + " | coords=" + Truncate(coords, 80) + " | location=" + Truncate(location, 160) + " | description=" + Truncate(desc, 240));
                if (!string.IsNullOrWhiteSpace(marker.SourcePlayerQuote)) sb.AppendLine("  source=" + QuoteInline(Truncate(marker.SourcePlayerQuote, 240)));
            }
        }

        private static void AppendList(StringBuilder sb, string title, IEnumerable<string> values)
        {
            List<string> list = values.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (list.Count == 0) return;
            sb.AppendLine(title + ":");
            foreach (string value in list)
            {
                sb.AppendLine("- " + value);
            }
        }
    }
}
