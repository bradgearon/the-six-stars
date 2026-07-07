using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.DailyState;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.Screen;
using Wds.SixStars.Api.Stars;
using Wds.SixStars.Api.Stories;

namespace Wds.SixStars.Api.Ai
{
    public sealed class StarPromptBuilder
    {
        private readonly MainInstructionSet _mainInstructions;
        private readonly StarPromptOptions _options;
        private readonly PromptTemplateRenderer _templates;

        public StarPromptBuilder(MainInstructionSet mainInstructions)
            : this(mainInstructions, new StarPromptOptions())
        {
        }

        public StarPromptBuilder(MainInstructionSet mainInstructions, StarPromptOptions options)
        {
            _mainInstructions = mainInstructions;
            _options = options ?? new StarPromptOptions();
            _templates = PromptTemplateRenderer.CreateDefault();
        }

        public IReadOnlyList<ChatMessage> BuildMessages(StarProfile profile, StarInteractionRequest request, StarState state)
        {
            List<ChatMessage> messages = new List<ChatMessage>
            {
                new ChatMessage("system", BuildSystemMessage(profile)),
                new ChatMessage("user", BuildUserMessage(profile, request, state))
            };
            return messages;
        }

        public IReadOnlyList<ChatMessage> BuildSpeechMessages(StarProfile profile, StarInteractionRequest request, StarState state)
        {
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
            StringBuilder system = new StringBuilder();
            system.AppendLine("You are " + originProfile.DisplayName + " from The Last Cat, but this is NOT a spoken reply.");
            system.AppendLine("INTERSTAR RELAY MODE. Decide what you would naturally tell " + targetProfile.DisplayName + " about the Last Cat after your recent conversation.");
            system.AppendLine("Return compact JSON only. Do not speak to the player. Do not award points. Do not invent that the target heard the conversation directly.");
            system.AppendLine("Only set shouldTell=true when the conversation is emotionally meaningful, practically important, funny enough to share, worrying, or relevant to the target Star's personality. If it is small talk and not useful, set shouldTell=false.");
            system.AppendLine("whatOriginWouldTellTarget should be written as the origin Star's concise private report to the target Star, 1-5 sentences. Include emotional nuance, not just a transcript.");

            StringBuilder user = new StringBuilder();
            user.AppendLine("ORIGIN STAR");
            AppendInterstarProfile(user, originProfile, originState);
            user.AppendLine();
            user.AppendLine("TARGET STAR");
            AppendInterstarProfile(user, targetProfile, targetState);
            user.AppendLine();
            user.AppendLine("LATEST CONVERSATION WITH THE LAST CAT");
            user.AppendLine("Player said:");
            user.AppendLine(QuoteBlock(originalRequest.PlayerText));
            user.AppendLine("Origin Star answered:");
            user.AppendLine(QuoteBlock(originResponse.SpokenText));
            user.AppendLine("Origin emotional vectors: good=" + (originResponse.EmotionalEffectVector ?? string.Empty) + " bad=" + (originResponse.NegativeEmotionVector ?? string.Empty));
            user.AppendLine("Origin deterministic summary: " + Truncate(originResponse.DeterministicEmotionUpdateSummary ?? string.Empty, 600));
            user.AppendLine("Known top emotions: " + string.Join(", ", (originResponse.TopAffectedEmotions ?? new List<TopAffectedEmotion>()).Select(x => x.ToString())));
            user.AppendLine();
            StarInterstarInfluenceRule relayRule = (originProfile.InterstarRules ?? new List<StarInterstarInfluenceRule>())
                .LastOrDefault(x => x != null && string.Equals(x.TargetStarId, targetProfile.StarId, StringComparison.OrdinalIgnoreCase))
                ?? new StarInterstarInfluenceRule { TargetStarId = targetProfile.StarId };
            user.AppendLine("Interstar rule context: origin ally=" + originState.IsAlly + "; target met=" + targetState.IsMet + "; target ally=" + targetState.IsAlly + "; relayWeight=" + relayRule.RelayWeight.ToString("0.00") + "; positiveMultiplier=" + relayRule.PositiveMultiplier.ToString("0.00") + "; negativeMultiplier=" + relayRule.NegativeMultiplier.ToString("0.00") + "; cooldownSeconds=" + relayRule.CooldownSeconds + ".");
            List<string> relayTopicTags = relayRule.TopicTags ?? new List<string>();
            if (relayTopicTags.Count > 0) user.AppendLine("Rule topic tags: " + string.Join(", ", relayTopicTags.Take(8)) + ".");
            user.AppendLine("Now decide whether " + originProfile.DisplayName + " would tell " + targetProfile.DisplayName + " anything, and what she would tell her.");

            return new List<ChatMessage>
            {
                new ChatMessage("system", system.ToString()),
                new ChatMessage("user", user.ToString())
            };
        }

        public IReadOnlyList<ChatMessage> BuildInterstarReactionMessages(
            StarProfile targetProfile,
            InterstarRelayMessage relay,
            StarInteractionRequest originalRequest,
            StarInteractionResponse originResponse,
            StarState targetState)
        {
            StringBuilder system = new StringBuilder();
            system.AppendLine("You are " + targetProfile.DisplayName + " from The Last Cat, but this is NOT a spoken reply.");
            system.AppendLine("INTERSTAR REACTION VECTOR MODE. Another Star privately told you about her recent conversation with the Last Cat.");
            system.AppendLine("Return compact JSON only. Do not write spokenText. Do not talk to the player. Do not directly award relationship points; suggestedStateDeltas are only hints.");
            system.AppendLine("Compute emotionalEffectVector and negativeEmotionVector for how YOU now feel about the Last Cat after hearing the relay.");
            system.AppendLine("Use the canonical emotion order exactly: " + string.Join(", ", StarEmotionMatrix.StandardEmotionIds) + ". Format exactly confidence:score;confidence:score;... with one pair per emotion.");
            system.AppendLine("negativeEmotionVector uses exactly: " + string.Join(", ", RelationshipDeltaCalculator.NegativeEmotionIds) + ". Use 0.00:0 for emotions not implied.");
            system.AppendLine("Keep the update modest because this is second-hand information. Strong signals require the relay to matter strongly to your personality/domain.");

            StringBuilder user = new StringBuilder();
            user.AppendLine("TARGET STAR STATE / PERSONALITY");
            AppendInterstarProfile(user, targetProfile, targetState);
            user.AppendLine();
            user.AppendLine("WHAT " + relay.OriginStarId + " TOLD YOU");
            user.AppendLine(QuoteBlock(relay.WhatOriginWouldTellTarget));
            user.AppendLine("Relay summary: " + Truncate(relay.Summary ?? string.Empty, 360));
            user.AppendLine("Relay reason: " + Truncate(relay.Reason ?? string.Empty, 360));
            user.AppendLine();
            user.AppendLine("ORIGINAL PLAYER MESSAGE TO " + relay.OriginStarId + ":");
            user.AppendLine(QuoteBlock(originalRequest.PlayerText));
            user.AppendLine("Origin Star foreground reply:");
            user.AppendLine(QuoteBlock(originResponse.SpokenText));
            user.AppendLine();
            user.AppendLine("Return your private interstar emotional vector response toward the Last Cat now.");

            return new List<ChatMessage>
            {
                new ChatMessage("system", system.ToString()),
                new ChatMessage("user", user.ToString())
            };
        }

        public IReadOnlyList<ChatMessage> BuildInterstarFeedbackMessages(
            StarProfile originProfile,
            StarInteractionRequest originalRequest,
            StarInteractionResponse originResponse,
            StarState originState,
            IReadOnlyList<InterstarReactionSummary> reactions)
        {
            StringBuilder system = new StringBuilder();
            system.AppendLine("You are " + originProfile.DisplayName + " from The Last Cat, but this is NOT a spoken reply.");
            system.AppendLine("INTERSTAR FEEDBACK VECTOR MODE. Other Stars reacted emotionally to what you told them about the Last Cat.");
            system.AppendLine("Return compact JSON only. Do not write spokenText. Compute how their responses make YOU feel about the Last Cat now.");
            system.AppendLine("Use the canonical emotion order exactly: " + string.Join(", ", StarEmotionMatrix.StandardEmotionIds) + ". Format exactly confidence:score;confidence:score;... with one pair per emotion.");
            system.AppendLine("negativeEmotionVector uses exactly: " + string.Join(", ", RelationshipDeltaCalculator.NegativeEmotionIds) + ". Use 0.00:0 for emotions not implied.");
            system.AppendLine("Keep this very modest; it is a reflection pass, not a second full conversation.");

            StringBuilder user = new StringBuilder();
            user.AppendLine("ORIGIN STAR STATE / PERSONALITY");
            AppendInterstarProfile(user, originProfile, originState);
            user.AppendLine();
            user.AppendLine("ORIGINAL LAST CAT MESSAGE");
            user.AppendLine(QuoteBlock(originalRequest.PlayerText));
            user.AppendLine("Your spoken reply was:");
            user.AppendLine(QuoteBlock(originResponse.SpokenText));
            user.AppendLine();
            user.AppendLine("OTHER STARS' PRIVATE REACTIONS");
            foreach (InterstarReactionSummary reaction in reactions ?? Array.Empty<InterstarReactionSummary>())
            {
                user.AppendLine("- " + reaction.StarId + " heard from " + reaction.SourceStarId + ": " + Truncate(reaction.RelaySummary, 240));
                user.AppendLine("  vectors good=" + Truncate(reaction.EmotionalEffectVector, 260) + " bad=" + Truncate(reaction.NegativeEmotionVector, 120));
                user.AppendLine("  applied delta H/T/R/C=" + reaction.AppliedDelta.Heart + "/" + reaction.AppliedDelta.Trust + "/" + reaction.AppliedDelta.Romance + "/" + reaction.AppliedDelta.EmotionalConnectionPercent.ToString("0.00"));
                if (!string.IsNullOrWhiteSpace(reaction.TopAffectedEmotionsText)) user.AppendLine("  top=" + reaction.TopAffectedEmotionsText);
            }
            user.AppendLine();
            user.AppendLine("Return your feedback emotional vectors toward the Last Cat now.");

            return new List<ChatMessage>
            {
                new ChatMessage("system", system.ToString()),
                new ChatMessage("user", user.ToString())
            };
        }

        public IReadOnlyList<ChatMessage> BuildStoryIntentMessages(StarProfile profile, StarInteractionRequest request, StarState state)
        {
            StringBuilder system = new StringBuilder();
            system.AppendLine("You are a tiny pre-flight intent classifier for The Last Cat desktop companion.");
            system.AppendLine("Decide whether the latest player message means the selected Star should enter LONG STORY MODE and begin narrating a story aloud.");
            system.AppendLine("Return JSON only. Do not roleplay. Do not answer the player.");
            system.AppendLine("Set isStoryRequest=true only when the player is asking for a story-like narration: story/stories, tale, fable, legend, myth, parable, saga, chronicle, folktale, yarn, bedtime story, cave story, narrated adventure, or similar. Active story playback is handled locally by the host after the first response.");
            system.AppendLine("Use conversation history to catch indirect phrasing, but do not classify ordinary chat, code requests, ratings, design discussion, compliments using story-like metaphors, or questions ABOUT the story system as story requests. A message that merely contains the word story is NOT enough; the player must be asking the Star to narrate or continue a story now.");
            system.AppendLine("If true, storySeed should be the usable story request/seed in the player's words. If false, storySeed must be empty.");

            StringBuilder user = new StringBuilder();
            user.AppendLine("Selected Star: " + profile.DisplayName);
            user.AppendLine("Current RequestType from caller: " + request.RequestType);
            user.AppendLine("Latest player message:");
            user.AppendLine(QuoteBlock(request.PlayerText));
            user.AppendLine("Current shared summary: " + (string.IsNullOrWhiteSpace(state.ConversationSummary) ? "none" : Truncate(state.ConversationSummary, 600)));
            List<ConversationLogEntry> usableIntentHistory = state.MemoryLog.Where(IsUsableConversationTurn).Take(6).ToList();
            if (usableIntentHistory.Count > 0)
            {
                user.AppendLine("Recent usable turns, newest first:");
                int i = 1;
                foreach (ConversationLogEntry entry in usableIntentHistory)
                {
                    user.AppendLine("- Turn " + i + ": player=" + QuoteInline(Truncate(entry.PlayerText, 160)) + "; star=" + QuoteInline(Truncate(entry.StarResponse, 160)) + "; requestType=" + entry.RequestType);
                    i++;
                }
            }
            if (state.StoryHistory.Count > 0)
            {
                user.AppendLine("Stories already told by this Star:");
                foreach (StarStoryHistoryEntry story in state.StoryHistory.Take(8))
                {
                    user.AppendLine("- " + story.Title + " | finished=" + story.Finished + " | " + Truncate(story.Summary, 180));
                }
            }
            user.AppendLine("Classify the latest message now.");

            return new List<ChatMessage>
            {
                new ChatMessage("system", system.ToString()),
                new ChatMessage("user", user.ToString())
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
            AppendAccumulatedEmotionTotals(relationship, state);

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

            string previousXml = request.PreviousRecord == null || string.IsNullOrWhiteSpace(request.PreviousRecord.XmlContent)
                ? "none"
                : Truncate(request.PreviousRecord.XmlContent, 4000);

            Dictionary<string, string> systemData = new Dictionary<string, string>
            {
                { "StarDisplayName", profile.DisplayName },
                { "GlobalCanonLore", globalCanon.ToString().TrimEnd() }
            };
            Dictionary<string, string> userData = new Dictionary<string, string>
            {
                { "StarDisplayName", profile.DisplayName },
                { "SessionId", request.SessionId ?? string.Empty },
                { "LocalDate", request.LocalDate ?? string.Empty },
                { "SlotId", request.SlotId ?? string.Empty },
                { "LocalNow", request.LocalNow.ToString("F") },
                { "StarActingCard", starCard.ToString().TrimEnd() },
                { "RelationshipContext", relationship.ToString().TrimEnd() },
                { "RecentConversationTurns", recent.ToString().TrimEnd() },
                { "DurableMemoryContext", memory.ToString().TrimEnd() },
                { "PreviousDailyStateXml", previousXml }
            };

            return new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("daily_state_system.sbn", systemData)),
                new ChatMessage("user", _templates.Render("daily_state_user.sbn", userData))
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
            StringBuilder system = new StringBuilder();
            system.AppendLine("You are a tiny information-need selector for The Last Cat desktop companion.");
            system.AppendLine("Do not roleplay. Do not answer the player. Analyze the Star's optional foreground information-needs vector and the local data inventory.");
            system.AppendLine("Select at most one available data area for a deep second LLM pass. Prefer the area that best matches what the player actually asked for, not merely the highest raw score.");
            system.AppendLine("If no local data area is available or the request can already be answered from the prompt, set shouldRequestMoreInformation=false and keep the selected area low-confidence.");
            system.AppendLine("Return JSON only.");

            StringBuilder user = new StringBuilder();
            user.AppendLine("Selected Star: " + profile.DisplayName);
            user.AppendLine("Latest player message:");
            user.AppendLine(QuoteBlock(request.PlayerText));
            user.AppendLine("Foreground spoken bridge/reply:");
            user.AppendLine(QuoteBlock(foregroundResponse.SpokenText));
            user.AppendLine("Ongoing summary: " + (string.IsNullOrWhiteSpace(state.ConversationSummary) ? "none" : Truncate(state.ConversationSummary, 600)));
            user.AppendLine();
            user.AppendLine("FOREGROUND INFORMATION NEED VECTOR");
            if (foregroundVector == null || foregroundVector.Count == 0)
            {
                user.AppendLine("none");
            }
            else
            {
                foreach (InformationNeedVectorItem need in foregroundVector.Take(12))
                {
                    user.AppendLine("- dataType=" + need.DataType + "; subject=" + need.Subject + "; confidence=" + need.Confidence.ToString("0.00") + "; score=" + need.Score.ToString("0.0") + "; reason=" + Truncate(need.Reason, 220));
                }
            }
            user.AppendLine();
            user.AppendLine("LOCAL DATA INVENTORY");
            if (availableData == null || availableData.Count == 0)
            {
                user.AppendLine("none");
            }
            else
            {
                foreach (InformationNeedAvailabilityItem item in availableData)
                {
                    user.AppendLine("- dataType=" + item.DataType + "; available=" + item.Available + "; count=" + item.Count + "; summary=" + Truncate(item.Summary, 260));
                    if (item.SampleSubjects.Count > 0) user.AppendLine("  samples=" + string.Join(", ", item.SampleSubjects.Take(6)));
                }
            }
            user.AppendLine();
            user.AppendLine("Selector rules:");
            user.AppendLine("- Return rankedNeeds as the useful vector sorted from strongest to weakest after considering the player message and availability.");
            user.AppendLine("- selectedDataType must be one of the available types when shouldRequestMoreInformation=true.");
            user.AppendLine("- Use subject from the foreground vector when useful, otherwise infer a compact subject from the player message, or self.");
            user.AppendLine("- Do not request deep history just because the word remember appears if a direct answer is already possible.");
            user.AppendLine("- Do request deep data when the Star's first reply was a bridge line or the foreground vector points to old history, quotes, markers, lore, bestiary, character, world, screen, or story data.");

            return new List<ChatMessage>
            {
                new ChatMessage("system", system.ToString()),
                new ChatMessage("user", user.ToString())
            };
        }

        private string BuildCompactReviewSystemMessage(StarProfile profile)
        {
            StringBuilder vectorInstructions = new StringBuilder();
            vectorInstructions.AppendLine(BuildEmotionalEffectVectorInstruction());
            vectorInstructions.AppendLine(BuildNegativeEmotionVectorInstruction());

            Dictionary<string, string> data = new Dictionary<string, string>
            {
                { "StarDisplayName", profile.DisplayName },
                { "EmotionVectorInstructions", vectorInstructions.ToString().TrimEnd() }
            };

            return _templates.Render("review_system.sbn", data);
        }

        private string BuildCompactReviewUserMessage(StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse foregroundResponse)
        {
            StringBuilder history = new StringBuilder();
            AppendRecentConversationHistory(history, profile, state, Math.Min(10, Clamp(_options.RecentHistoryCount, 1, 20)), compact: false);

            StringBuilder memory = new StringBuilder();
            memory.AppendLine("Existing conversation summary: " + (string.IsNullOrWhiteSpace(state.ConversationSummary) ? "none yet" : Truncate(state.ConversationSummary, 900)));
            AppendList(memory, "Important remembered player quotes", state.ImportantPlayerQuotes.Take(12).ToList());
            AppendList(memory, "Memory notes", state.MemoryNotes.Where(x => !IsTechnicalMemoryNote(x)).Take(12).ToList());
            AppendRememberedMarkers(memory, state, 20);
            AppendList(memory, "Relationship events", state.RelationshipEvents.Take(10).ToList());
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
            relationship.AppendLine("Current EmotionalConnectionPercent: " + state.EmotionalConnectionPercent.ToString("0.0") + "%.");
            AppendConnectionFlirtGuidance(relationship, profile, state);
            AppendDialogueMemoryContext(relationship, state, 10);
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

            Dictionary<string, string> data = new Dictionary<string, string>
            {
                { "StarDisplayName", profile.DisplayName },
                { "RequestType", request.RequestType.ToString() },
                { "PlayerTextBlock", QuoteBlock(request.PlayerText) },
                { "ForegroundSpokenPartsJson", FormatSpokenParts(foregroundResponse.SpokenParts).TrimEnd() },
                { "RelationshipContext", relationship.ToString().TrimEnd() },
                { "RecentConversationHistory", history.ToString().TrimEnd() },
                { "DurableMemoryContext", memory.ToString().TrimEnd() },
                { "StoryFieldsContext", story.ToString().TrimEnd() }
            };

            return _templates.Render("review_user.sbn", data);
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
            AppendFastConversationProfile(fastProfile, profile);

            StringBuilder storyModeSystem = new StringBuilder();
            if (request.RequestType == RequestType.Story)
            {
                AppendStoryModeSystemContext(storyModeSystem, profile);
            }

            string foregroundModeInstructions = request.RequestType == RequestType.Story
                ? "This is a STORY FOREGROUND PASS. The goal is to return a complete cached story plan plus the first spoken story chunk for TTS. Do not include emotion vectors here; background review handles scoring and memory."
                : "This is the FAST FOREGROUND SPEECH PASS. The goal is to produce the spoken answer quickly so TTS can begin immediately.";

            Dictionary<string, string> data = new Dictionary<string, string>
            {
                { "StarDisplayName", profile.DisplayName },
                { "ForegroundModeInstructions", foregroundModeInstructions },
                { "GlobalCanonLore", globalCanon.ToString().TrimEnd() },
                { "PrivateRomanceProfile", privateRomance.ToString().TrimEnd() },
                { "StarActingVoiceCard", starCard.ToString().TrimEnd() },
                { "FastConversationProfile", fastProfile.ToString().TrimEnd() },
                { "StoryModeSystemContext", storyModeSystem.ToString().TrimEnd() }
            };

            return _templates.Render("speech_system.sbn", data);
        }

        private string BuildSpeechUserMessage(StarProfile profile, StarInteractionRequest request, StarState state)
        {
            StringBuilder dialogueAndMarkers = new StringBuilder();
            AppendDialogueMemoryContext(dialogueAndMarkers, state, 8);
            AppendRememberedMarkers(dialogueAndMarkers, state, 20);

            StringBuilder storyHistory = new StringBuilder();
            if (request.RequestType == RequestType.Story && state.StoryHistory.Count > 0)
            {
                storyHistory.AppendLine("StoriesAlreadyTold by " + profile.DisplayName + " — avoid repeating these:");
                foreach (StarStoryHistoryEntry story in state.StoryHistory.Take(12))
                {
                    storyHistory.AppendLine("- " + story.Title + " | " + Truncate(story.Summary, 220));
                }
            }

            StringBuilder recentTurns = new StringBuilder();
            AppendRecentConversationHistory(recentTurns, profile, state, Math.Min(10, Clamp(_options.RecentHistoryCount, 1, 20)), compact: true);

            StringBuilder importantMemory = new StringBuilder();
            AppendForegroundImportantMemoryContext(importantMemory, profile, state);

            StringBuilder relationship = new StringBuilder();
            relationship.AppendLine("Relationship status: " + BuildRelationshipStatus(state));
            relationship.AppendLine("Ally status: " + (state.IsAlly ? "ally/unlocked" : "not an active ally yet; Elusiaa is the only default ally until story/gameplay unlocks this Star."));
            relationship.AppendLine("Emotional connection skill level: " + (string.IsNullOrWhiteSpace(state.EmotionalConnectionSkillLevel) ? "interested_private_potential_partner" : state.EmotionalConnectionSkillLevel));
            relationship.AppendLine("Current EmotionalConnectionPercent: " + state.EmotionalConnectionPercent.ToString("0.0") + "%; girlfriendReady=" + state.IsGirlfriendReady + "; " + (state.GirlfriendReadinessSummary ?? string.Empty));
            relationship.AppendLine("Current state stats are part of the request every turn. Use them silently to shape warmth, caution, flirtation, distance, curiosity, secrets, and how personal your answer becomes. Do not speak the numbers unless the player asks about mechanics.");
            AppendAccumulatedEmotionTotals(relationship, state);
            AppendConnectionFlirtGuidance(relationship, profile, state);

            StringBuilder todayStateContext = new StringBuilder();
            AppendTodayStateContext(todayStateContext, request);

            StringBuilder task = new StringBuilder();
            if (request.RequestType == RequestType.Story)
            {
                AppendFastStoryContext(task, request);
                AppendStoryLoreContext(task, profile, request);
            }
            else
            {
                if (request.RequestType == RequestType.Photo || (request.AttachedPhoto != null && request.AttachedPhoto.IsAttached))
                {
                    AppendPhotoCommentGuidance(task, profile, request);
                }
                else
                {
                    AppendFastChatAnswerGuidance(task, profile, request);
                }
                if (request.GameContext.Flags.TryGetValue("extraInformationContext", out string? extraInformationContext) && !string.IsNullOrWhiteSpace(extraInformationContext))
                {
                    task.AppendLine();
                    task.AppendLine("EXTRA REQUESTED INFORMATION FROM LOCAL STATE / ARCHIVES");
                    task.AppendLine(extraInformationContext);
                    task.AppendLine("Use this extra information to answer the same latest player message. If it answers the question, speak naturally as if you remembered or found the old note. If it still does not answer, say you searched your memory and cannot find enough yet.");
                }
                AppendGirlfriendProposalGuidance(task, profile, request, state);
            }

            StringBuilder contract = new StringBuilder();
            if (request.RequestType == RequestType.Story)
            {
                contract.AppendLine("Return story foreground JSON only: starId, spokenText, storyTitle, storyId, storyMarkdown, storySummary, storyIsFinished, storyPartIndex. Do not include emotion vectors, emotionScores, voiceDirective, summaries, deltas, loreUsed, riskFlags, or learnedFunctions.");
                contract.AppendLine("Return the complete story in storyMarkdown, formatted as ## Chunk 0, one or two story sentences, ---, ## Chunk 1, one or two story sentences, ---, and so on. Do not include blank chunks, delimiter-only chunks, or a final empty heading; every chunk must contain narration the TTS can speak.");
                contract.AppendLine("spokenText must be the first actual story chunk from storyMarkdown, not a preface or promise. Start the story itself immediately.");
                contract.AppendLine("Set storyPartIndex=0. Set storyIsFinished=true when storyMarkdown is a complete episode with a real ending or satisfying soft cliffhanger, even when it has Chunk 1+ for local autoplay. Set storyIsFinished=false only if the story truly requires a later LLM continuation.");
                contract.AppendLine("Narrate the story itself as " + profile.DisplayName + "; do not talk about preparing the story. Let her emotional reaction show through the imagery, word choice, tenderness, humor, fear, or wonder of the opening lines.");
            }
            else
            {
                contract.AppendLine("Return only: starId, spokenText, and optionally needsMoreInformation plus informationNeeds. Do not include emotion vectors, emotionScores, topEmotions, emotionalPurpose, voiceDirective, memoryNotes, summaries, deltas, loreUsed, riskFlags, or learnedFunctions in this foreground response.");
                contract.AppendLine("informationNeeds is a full optional information-request vector, not a single final decision. The host/API layer will analyze the whole vector, compare it to available local data, and deterministically decide whether to run a deep second pass.");
                contract.AppendLine("When the latest message may need older context or data outside this prompt, return the whole informationNeeds vector: one separate entry for each area you considered from history, quotes, markers, lore, bestiary, characters, world, screen, and story. Use low confidence/score for areas that are probably not needed, and higher values for likely areas. Do not collapse everything into one history need. Put subject='self' unless a specific marker/creature/person/topic is named. Confidence is 0-1 and score is 0-100.");
                contract.AppendLine("Omit informationNeeds or return [] when the answer is clearly available in the provided prompt. needsMoreInformation is optional legacy hint; set it true only when your spokenText is intentionally a brief 'I am trying to remember/check' bridge line.");
                contract.AppendLine("If you set needsMoreInformation=true or return a high scoring informationNeeds vector, spokenText should be brief and in character, such as saying you are reaching back into older memory. The host may immediately run a selector and second pass with a local archive/lore slice.");
                contract.AppendLine("For normal chat, use 1-4 spokenText items. If the player asks direct personal questions such as name/about yourself/friends/what do you do, use 3-5 concise spokenText items and answer completely instead of being evasive.");
                contract.AppendLine("Every spokenText item should be one or two natural sentences. A direct question deserves a direct answer, plus personality.");
                contract.AppendLine("Answer warmly and directly as " + profile.DisplayName + ".");
            }
            if (request.RequestType == RequestType.Story)
            {
                contract.AppendLine("spokenText array: each item is one TTS chunk. Use one or two sentences per item. The host also validates story chunks, but do not rely on it.");
            }
            else
            {
                contract.AppendLine("spokenText array: each item is one TTS chunk. Use one or two sentences per item.");
            }

            Dictionary<string, string> data = new Dictionary<string, string>
            {
                { "StarDisplayName", profile.DisplayName },
                { "PlayerTextBlock", QuoteBlock(request.PlayerText) },
                { "RequestType", request.RequestType.ToString() },
                { "RomanceMode", request.GameContext.RomanceMode.ToString() },
                { "Area", request.GameContext.Area ?? string.Empty },
                { "Quest", request.GameContext.Quest ?? string.Empty },
                { "RelationshipContext", relationship.ToString().TrimEnd() },
                { "DialogueAndMarkerContext", dialogueAndMarkers.ToString().TrimEnd() },
                { "OngoingSummary", string.IsNullOrWhiteSpace(state.ConversationSummary) ? "none yet" : Truncate(state.ConversationSummary, 600) },
                { "LastRememberedReply", BuildLastUsableStarReply(state) },
                { "StoryHistoryContext", storyHistory.ToString().TrimEnd() },
                { "RecentConversationTurns", recentTurns.ToString().TrimEnd() },
                { "ImportantMemoryContext", importantMemory.ToString().TrimEnd() },
                { "LatestScreenOneLine", BuildLatestScreenOneLine(request.RecentScreenCaptures) },
                { "TodayStarStateContext", todayStateContext.ToString().TrimEnd() },
                { "TaskSpecificGuidance", task.ToString().TrimEnd() },
                { "ForegroundContractHeading", request.RequestType == RequestType.Story ? "STORY FOREGROUND CONTRACT" : "FAST SPEECH CONTRACT" },
                { "ForegroundContract", contract.ToString().TrimEnd() }
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

        private static void AppendTodayStateContext(StringBuilder sb, StarInteractionRequest request)
        {
            string xml = string.Empty;
            string summary = string.Empty;
            string generatedAt = string.Empty;
            string slotId = string.Empty;
            if (request.GameContext != null && request.GameContext.Flags != null)
            {
                request.GameContext.Flags.TryGetValue("todayStarStateXml", out xml);
                request.GameContext.Flags.TryGetValue("todayStarStateSummary", out summary);
                request.GameContext.Flags.TryGetValue("todayStarStateGeneratedAt", out generatedAt);
                request.GameContext.Flags.TryGetValue("todayStarStateSlotId", out slotId);
            }

            if (string.IsNullOrWhiteSpace(xml))
            {
                sb.AppendLine("TODAY STAR STATE XML: none yet. If the player asks what you are up to, answer from your profile, relationship state, and recent history without pretending to know a generated daily schedule.");
                return;
            }

            sb.AppendLine("TODAY STAR STATE XML");
            sb.AppendLine("GeneratedAt=" + generatedAt + "; Slot=" + slotId + "; Summary=" + Truncate(summary, 300));
            sb.AppendLine(xml.Trim());
            sb.AppendLine("Use this current-day XML quietly when the player asks what you are doing, how you feel today, who you have seen, what has happened recently, what you want, or what your current situation is. Do not mention XML unless the player asks about system internals.");
        }

        private static void AppendStoryModeSystemContext(StringBuilder sb, StarProfile profile)
        {
            sb.AppendLine("LONG STORY MODE");
            sb.AppendLine("When RequestType=Story, you are not merely agreeing to tell a story. You are already telling it aloud as " + profile.DisplayName + ".");
            sb.AppendLine("Do not open with setup phrases such as 'let me tell you', 'shall I begin', 'the story begins', 'I will continue', or 'where we left off'. Start with the actual story prose immediately.");
            sb.AppendLine("For story mode, plan a complete story in storyMarkdown on the first response. The story does not need to follow a rigid formula, but it should feel whole, with a beginning, middle, climax, and satisfying resolution or soft cliffhanger. Format storyMarkdown into numbered markdown speech chunks: ## Chunk 0, then one or two story sentences, then a line containing only --- before the next chunk. Continue this pattern for every chunk. Never emit an empty chunk; if a chunk has no narration, omit that chunk entirely and renumber the remaining chunks.");
            sb.AppendLine("The target minutes and chunk count are flexible guidance for how much cave-travel narration the host hopes for, not a law. If exact length would risk truncation or an unfinished tale, choose a shorter complete episode over a huge incomplete one. The runtime randomly chooses an approximate 2-35 minute target and a capped chunk count for each new story.");
            sb.AppendLine("spokenText must be only the first actual story passage from ## Chunk 0. It must not be an introduction about the story. Keep each spokenText item to one or two natural sentences for TTS.");
            sb.AppendLine("After chunk 0, the host will auto-play the remaining cached storyMarkdown chunks locally. Do not expect or ask for follow-up LLM continuation calls.");
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
            else sb.AppendLine("Player story seed: choose a fitting new story.");
            sb.AppendLine("ApproxTargetMinutes: " + (options.TargetMinutes <= 0 ? 0 : options.TargetMinutes) + " (flexible, not strict)");
            sb.AppendLine("ApproxTargetChunkCount: " + (options.TargetChunkCount <= 0 ? 0 : options.TargetChunkCount) + " (capped so the story can finish)");
            sb.AppendLine("MaxSentencesPerChunk: " + (options.MaxSentencesPerChunk <= 0 ? 2 : options.MaxSentencesPerChunk));
            if (options.StoriesAlreadyTold.Count > 0)
            {
                sb.AppendLine("StoriesAlreadyTold from host memory - forbidden as repeat plots:");
                foreach (string story in options.StoriesAlreadyTold.Take(20)) sb.AppendLine("- " + Truncate(story, 260));
                sb.AppendLine("Do not reuse these titles, storyIds, central objects, central miracles, or main plot beats unless the player explicitly asks to hear that same story again.");
                sb.AppendLine(BuildForbiddenStoryMotifHints(options.StoriesAlreadyTold));
            }
            sb.AppendLine("Create a new complete story. The desktop host will speak Chunk 0 from spokenText, then read Chunk 1+ directly from cached storyMarkdown. Empty chunks are invalid because the host will skip them; write only meaningful narration chunks. Do not write a trailing chunk header unless that chunk has narration after it. The final chunk must contain a real ending line or a satisfying soft cliffhanger.");
            sb.AppendLine("Length rule: the approximate target exists to guide pacing only. Do not pad. Do not leave the tale unfinished just to chase the target length. Complete story > exact duration.");
        }

        private static string BuildEmotionalEffectVectorInstruction()
        {
            return "emotionalEffectVector is a compact machine-readable emotion hint for the player's latest statement. It tells the system whether the player made this Star feel each emotion. Use the canonical emotion order exactly: "
                + string.Join(", ", StarEmotionMatrix.StandardEmotionIds)
                + ". Format exactly as confidence:score;confidence:score;... with one pair per emotion in that order. The vector must contain exactly one pair for every listed emotion; include trailing 0.00:0 pairs. Confidence is 0.00-1.00. Score is 0-100. Use 0.00:0 for emotions not implied, but use low nonzero confidence (for example 0.05-0.35) for real light reactions such as being mildly flattered, intrigued, safe, or hopeful. Use high confidence only for strong, specific emotional impact. Example shape: 0.03:20;0.75:45;0.00:0;... No names, spaces, reasons, brackets, or extra text.";
        }



        private static string BuildNegativeEmotionVectorInstruction()
        {
            return "negativeEmotionVector is a compact machine-readable hint for negative/boundary feelings in the latest player statement. Use exactly seven emotions in this order: Irritated, Disrespected, Pressured, CreepedOut, Bored, Hurt, Unsafe. Format exactly as confidence:score;confidence:score;... with seven pairs. Use 0.00:0 when not implied. High values should be reserved for real negativity: repeated insults, coercion, disrespect, shallow pressure, unsafe sexual pushiness, cruelty, or boring/repetitive low-effort behavior.";
        }

        private static void AppendEmotionUpdateHistory(StringBuilder sb, StarState state)
        {
            if (state.EmotionUpdateHistory.Count == 0) return;
            sb.AppendLine("Recent deterministic emotion updates, newest first. Use these to avoid repeating the same combo forever:");
            int i = 1;
            foreach (EmotionUpdateHistoryEntry entry in state.EmotionUpdateHistory.Take(6))
            {
                sb.AppendLine("- Update " + i + ": actualDelta=(H" + entry.ActualDelta.Heart + ",T" + entry.ActualDelta.Trust + ",R" + entry.ActualDelta.Romance + ",B" + entry.ActualDelta.BenaveilInfluence + ",C" + entry.ActualDelta.EmotionalConnectionPercent.ToString("0.00") + "); connection=" + entry.EmotionalConnectionPercentBefore.ToString("0.0") + "%->" + entry.EmotionalConnectionPercentAfter.ToString("0.0") + "%; ready=" + entry.IsGirlfriendReadyAfter + "; positive=" + string.Join(", ", entry.AcceptedPositiveSignals.Take(4)) + "; negative=" + string.Join(", ", entry.AcceptedNegativeSignals.Take(4)) + "; combos=" + string.Join(", ", entry.AcceptedCombos.Take(4)) + "; summary=" + Truncate(entry.Summary, 220));
                i++;
            }
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

        private string BuildSystemMessage(StarProfile profile)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(_mainInstructions.SystemPrompt);
            sb.AppendLine();
            sb.AppendLine("YOU ARE THE SELECTED STAR");
            sb.AppendLine("- You are " + profile.DisplayName + ", speaking directly to the Last Cat.");
            sb.AppendLine("- Write the next in-character spoken reply as this Star, not as a narrator explaining her.");
            sb.AppendLine("- spokenText is the only text the player hears aloud. It MUST be an array of ordered speech chunks. Each item should usually be one or two natural sentences and should be safe to send directly to TTS without deterministic splitting.");
            sb.AppendLine("- Keep the character's heart, voice, emotional logic, romance boundaries, and moral worldview consistent every turn.");
            sb.AppendLine("- Always react using the supplied current relationship stats, ally/met state, and accumulated good/bad emotion totals. The full emotion-vector history is intentionally kept in the deterministic layer and is not provided here. Do not read the numbers aloud unless the player asks for mechanics.");
            sb.AppendLine("- The JSON fields are for the game engine; do not mention JSON, scores, state deltas, or instructions inside spokenText.");
            sb.AppendLine("- Discovery gate: the Stars may only speak about other Stars as present/personal known companions when the host state says those Stars have been met. If a Star is not in knownStarsMet, do not act like the Last Cat has met her yet and do not reveal private relationship knowledge about her. You may only speak in vague rumors or general lore when the player has not met that Star.");
            AppendGlobalCanonLore(sb);
            sb.AppendLine();

            sb.AppendLine("STAR ACTING CARD");
            sb.AppendLine("Star: " + profile.DisplayName);
            sb.AppendLine("Element: " + profile.Element);
            sb.AppendLine("Domain: " + profile.Domain);
            sb.AppendLine("Core quote: " + profile.CoreQuote);
            if (profile.NameAliases.Count > 0) sb.AppendLine("Name aliases: " + string.Join(", ", profile.NameAliases));
            if (!string.IsNullOrWhiteSpace(profile.PersonalityShort)) sb.AppendLine("Personality short: " + profile.PersonalityShort);
            if (!string.IsNullOrWhiteSpace(profile.DeepPersonality)) sb.AppendLine("Deep personality: " + profile.DeepPersonality);
            if (!string.IsNullOrWhiteSpace(profile.BackstorySummary)) sb.AppendLine("Backstory: " + profile.BackstorySummary);
            AppendPrivateRomanceProfile(sb, profile);
            AppendExpandedProfileLore(sb, profile);
            AppendList(sb, "Quotes she may echo or paraphrase", profile.Quotes);
            AppendList(sb, "Behavior rules", profile.Behaviors);
            AppendList(sb, "Spoken response rules", profile.SpokenResponseRules);
            AppendList(sb, "Storytelling rules", profile.StorytellingRules);
            sb.AppendLine();

            sb.AppendLine("VOICE AND PERFORMANCE CARD — ALSO APPLIES TO TTS");
            sb.AppendLine("These notes are both acting notes for the LLM and voice notes for Qwen3-TTS. Treat them as stable vocal identity.");
            if (!string.IsNullOrWhiteSpace(profile.VoiceDirection)) sb.AppendLine("Voice direction: " + profile.VoiceDirection);
            if (!string.IsNullOrWhiteSpace(profile.PersonalityShort)) sb.AppendLine("Personality short for voice: " + profile.PersonalityShort);
            if (!string.IsNullOrWhiteSpace(profile.VoiceArchetype)) sb.AppendLine("Voice archetype: " + profile.VoiceArchetype);
            if (!string.IsNullOrWhiteSpace(profile.SpeakingStyle)) sb.AppendLine("Speaking style: " + profile.SpeakingStyle);
            if (!string.IsNullOrWhiteSpace(profile.VoiceId)) sb.AppendLine("Preferred voice id / clone id: " + profile.VoiceId);
            if (!string.IsNullOrWhiteSpace(profile.VoiceDesignPrompt)) sb.AppendLine("Qwen voice design prompt: " + profile.VoiceDesignPrompt);
            AppendList(sb, "Voice style rules", profile.VoiceStyleRules);
            AppendList(sb, "Voice generation rules", profile.VoiceGenerationRules);
            if (profile.EmotionVoiceHints.Count > 0)
            {
                sb.AppendLine("Emotion voice hints:");
                foreach (KeyValuePair<string, string> hint in profile.EmotionVoiceHints)
                {
                    sb.AppendLine("- " + hint.Key + ": " + hint.Value);
                }
            }
            sb.AppendLine();

            sb.AppendLine("PEOPLE SHE KNOWS");
            foreach (KnownPerson person in profile.PeopleKnown)
            {
                sb.AppendLine("- " + person.DisplayName + " [" + person.Relationship + "]: " + person.Summary);
            }

            sb.AppendLine();
            sb.AppendLine("FULL PROFILE LORE AVAILABLE TO HER");
            sb.AppendLine("Angel stories and past stories:");
            foreach (StarLoreEntry story in profile.AngelStories)
            {
                sb.AppendLine("- " + story.Id + " [unlock bond " + story.UnlockedAtBond + "]: " + story.Title + " — " + story.Summary);
            }
            sb.AppendLine("Story memory deck:");
            foreach (StoryMemoryCard card in profile.StoryDeck)
            {
                sb.AppendLine("- " + card.Id + " [" + card.Kind + ", unlock bond " + card.UnlockedAtBond + "]: " + card.Summary);
                if (card.Themes.Count > 0) sb.AppendLine("  themes: " + string.Join(", ", card.Themes));
                if (card.AllowedUses.Count > 0) sb.AppendLine("  allowed uses: " + string.Join(", ", card.AllowedUses));
                if (card.ForbiddenUses.Count > 0) sb.AppendLine("  forbidden uses: " + string.Join(", ", card.ForbiddenUses));
            }

            sb.AppendLine();
            sb.AppendLine("REQUIRED MEMORY OUTPUT EVERY TURN");
            sb.AppendLine("- conversationSummary: always return an updated compact summary of the ongoing shared conversation. Include what the player and Star have been talking about, the emotional direction, and any durable relationship context. This replaces/updates the durable Star state summary.");
            sb.AppendLine("- changesSummary: always return 1-3 sentences about what changed this turn: what the player just said/did, how she reacted, and whether memory/relationship/status shifted.");
            sb.AppendLine("- importantPlayerQuotes: include exact player quotes only when they are emotionally impactful, romantic, wise, cruel, funny, instructive, secret-unlocking, relationship-changing, or likely useful for future story-building. Preserve the player's wording exactly.");
            sb.AppendLine("- memoryNotes: include durable notes she should remember later: preferences, promises, private shared jokes, story seeds, work context, screen/vision context, and relationship facts.");
            sb.AppendLine("- Use prior conversation history and existing summary; do not behave like this is the first message unless there truly is no memory.");
            sb.AppendLine();

            sb.AppendLine("NO HEAVY EMOTION SCORES");
            sb.AppendLine("Do not return emotionScores in this response. The Star should provide current-turn emotional evidence through emotionalEffectVector and negativeEmotionVector. Set suggestedStateDeltas heart/trust/romance to 0 unless a host-specific debug test explicitly asks otherwise; the deterministic layer decides actual Heart, Trust, Romance, Ally, and Girlfriend progress from Star-specific JSON mappings and combo history.");
            sb.AppendLine();
            sb.AppendLine(_mainInstructions.ResponseContractNotes);
            sb.AppendLine();
            sb.AppendLine("VOICE DIRECTIVE REQUIREMENTS");
            sb.AppendLine("Also fill voiceDirective for Qwen3-TTS. voiceDirective.deliveryInstructions must be natural-language acting direction for the exact spokenText array. Include tone, emotion, pace, and intensity. voiceDirective.language must be only one real language name from: Auto, English, Chinese, French, German, Spanish, Italian, Japanese, Korean, Portuguese, Russian. Do not put tone/style text such as warm, poetic, gentle, or radiant in language; those belong in deliveryInstructions/prosody. The runtime will inject the stable profile voice identity into TTS, but you should make deliveryInstructions fit this exact line. Use personalityShort, voiceArchetype, speakingStyle, voiceStyleRules, and voiceGenerationRules above when deciding how she speaks. Do not put private debug information into spokenText; deliveryInstructions can mention performance style only.");
            return sb.ToString();
        }

        private string BuildUserMessage(StarProfile profile, StarInteractionRequest request, StarState state)
        {
            StringBuilder sb = new StringBuilder();
            string requestType = request.RequestType.ToString();
            _mainInstructions.TaskInstructions.TryGetValue(requestType, out string? taskInstruction);

            sb.AppendLine("LIVE SCENE BRIEF");
            sb.AppendLine("You are " + profile.DisplayName + ". You are talking to the Last Cat, the player, in an ongoing desktop companion session.");
            sb.AppendLine("He has just said:");
            sb.AppendLine(QuoteBlock(request.PlayerText));
            sb.AppendLine("He wants: " + BuildRequestGoal(profile, request));
            sb.AppendLine("You were just talking about: " + BuildRecentConversationOneLine(state));
            sb.AppendLine("Your relationship status: " + BuildRelationshipStatus(state));
            sb.AppendLine("World discovery state for this Star: IsMet=" + state.IsMet + ", IsAlly=" + state.IsAlly + ", IsGirlfriend=" + state.IsGirlfriend + ".");
            if (request.GameContext.Flags.TryGetValue("knownStarsMet", out string? knownStarsMet)) sb.AppendLine("Stars the Last Cat has met: " + (string.IsNullOrWhiteSpace(knownStarsMet) ? "none" : knownStarsMet));
            if (request.GameContext.Flags.TryGetValue("knownStarsAllies", out string? knownStarsAllies)) sb.AppendLine("Stars currently allied with the Last Cat: " + (string.IsNullOrWhiteSpace(knownStarsAllies) ? "none" : knownStarsAllies));
            if (request.GameContext.Flags.TryGetValue("knownStarsDetail", out string? knownStarsDetail)) sb.AppendLine("Known Star relationship detail: " + knownStarsDetail);
            if (state.ActiveEffectors != null && state.ActiveEffectors.Count > 0)
            {
                sb.AppendLine("Active deterministic state effectors currently shaping her response/state updates:");
                foreach (StarRelationshipEffector effector in state.ActiveEffectors.Where(x => x != null && !x.IsExpired(DateTimeOffset.UtcNow)).Take(12))
                {
                    sb.AppendLine("- " + effector.Id + " mode=" + effector.Mode + " reason=" + effector.Reason + " gainx=" + effector.ConnectionGainMultiplier.ToString("0.00") + " lossx=" + effector.ConnectionLossMultiplier.ToString("0.00") + " turns=" + effector.AppliedTurns + "/" + effector.RemainingTurns);
                }
            }
            sb.AppendLine("Shared memory together: " + (string.IsNullOrWhiteSpace(state.ConversationSummary) ? "No durable summary yet; establish continuity from this turn and the recent turns below." : state.ConversationSummary.Trim()));
            sb.AppendLine("Latest screen/vision update: " + BuildLatestScreenOneLine(request.RecentScreenCaptures));
            sb.AppendLine();

            sb.AppendLine("CURRENT TASK");
            sb.AppendLine(string.IsNullOrWhiteSpace(taskInstruction) ? "Respond to the player as the selected Star." : taskInstruction);
            sb.AppendLine();
            sb.AppendLine("INTERACTION MODE");
            sb.AppendLine("RequestType: " + request.RequestType);
            if (request.RequestType == RequestType.Chat)
            {
                sb.AppendLine("- Normal mode: respond directly to the player's message as " + profile.DisplayName + ".");
                sb.AppendLine("- This is the desktop companion default: a typed message such as hi, a compliment, poetry about her eyes, work banter, or a relationship test should receive one natural in-character reply for TTS.");
            }
            else if (request.RequestType == RequestType.Story)
            {
                sb.AppendLine("- Story mode: the player wants " + profile.DisplayName + " to tell a story aloud, so she should begin narrating the actual story immediately.");
                sb.AppendLine("- Do not open with a setup promise such as 'let me tell you' or 'the story begins'; make the first spokenText chunk the first one-or-two-sentence story passage.");
                sb.AppendLine("- Use the player's story suggestion below when it is provided, but keep canon, personality, and relationship state intact.");
                sb.AppendLine("- The story should still sound like something she is saying to the Last Cat, not a detached narrator.");
            }
            else
            {
                sb.AppendLine("- Use the selected request type while staying in character.");
            }
            sb.AppendLine();

            sb.AppendLine("PLAYER INPUT");
            sb.AppendLine(request.PlayerText ?? string.Empty);
            sb.AppendLine();

            if (request.RequestType == RequestType.Story)
            {
                sb.AppendLine("STORY OPTIONS");
                sb.AppendLine("Length: " + request.StoryOptions.Length);
                sb.AppendLine("Tone: " + request.StoryOptions.Tone);
                sb.AppendLine("CanonStrictness: " + request.StoryOptions.CanonStrictness);
                sb.AppendLine("UseUnlockedSecrets: " + request.StoryOptions.UseUnlockedSecrets);
                sb.AppendLine("AllowPoetry: " + request.StoryOptions.AllowPoetry);
                sb.AppendLine("AskFollowupAfter: " + request.StoryOptions.AskFollowupAfter);
                sb.AppendLine("TargetMinutes: " + request.StoryOptions.TargetMinutes);
                sb.AppendLine("TargetChunkCount: " + request.StoryOptions.TargetChunkCount);
                sb.AppendLine("MaxSentencesPerChunk: " + request.StoryOptions.MaxSentencesPerChunk);
                if (!string.IsNullOrWhiteSpace(request.StoryOptions.PlayerSuggestion))
                {
                    sb.AppendLine("Player story suggestion / seed:");
                    sb.AppendLine(request.StoryOptions.PlayerSuggestion.Trim());
                }
                else
                {
                    sb.AppendLine("Player story suggestion / seed: none provided; choose a fitting story from her personality, lore, memories, screen context, or relationship state.");
                }
                sb.AppendLine();
            }

            sb.AppendLine("GAME CONTEXT AND CURRENT PLAYER STATS");
            sb.AppendLine("Session: " + request.SessionId);
            sb.AppendLine("Speaker: " + request.Speaker);
            sb.AppendLine("RequestType: " + request.RequestType);
            sb.AppendLine("Area: " + request.GameContext.Area);
            sb.AppendLine("Quest: " + request.GameContext.Quest);
            sb.AppendLine("BattleState: " + request.GameContext.BattleState);
            sb.AppendLine("RomanceMode: " + request.GameContext.RomanceMode);
            sb.AppendLine("BenaveilPressureLevel: " + request.GameContext.BenaveilPressureLevel);
            AppendList(sb, "EquippedStars", request.GameContext.EquippedStars);
            AppendList(sb, "RecentEvents", request.GameContext.RecentEvents);
            if (request.GameContext.Flags.Count > 0)
            {
                sb.AppendLine("Flags / stats / input metadata:");
                foreach (KeyValuePair<string, string> flag in request.GameContext.Flags)
                {
                    sb.AppendLine("- " + flag.Key + ": " + flag.Value);
                }
            }

            sb.AppendLine();
            sb.AppendLine("CURRENT RELATIONSHIP STATUS");
            sb.AppendLine(BuildRelationshipStatus(state));
            sb.AppendLine("IsMet=" + state.IsMet + ", IsAlly=" + state.IsAlly + ", Heart=" + state.Heart + ", Trust=" + state.Trust + ", Romance=" + state.Romance + ", BenaveilInfluence=" + state.BenaveilInfluence);
            sb.AppendLine("EmotionalConnectionPercent=" + state.EmotionalConnectionPercent.ToString("0.0") + ", LastConnectionDelta=" + state.LastEmotionalConnectionPercentDelta.ToString("0.00") + ", IsGirlfriendReady=" + state.IsGirlfriendReady + ", Readiness=" + (state.GirlfriendReadinessSummary ?? string.Empty));
            AppendConnectionFlirtGuidance(sb, profile, state);
            AppendDialogueMemoryContext(sb, state, 20);
            sb.AppendLine("InteractionCount=" + state.InteractionCount + ", PositiveSuccession=" + state.PositiveSuccessionCount + ", RomanticSuccession=" + state.RomanticSuccessionCount + ", HurtSuccession=" + state.HurtSuccessionCount);
            sb.AppendLine("IsGirlfriend=" + state.IsGirlfriend);

            sb.AppendLine();
            sb.AppendLine("ONGOING SHARED MEMORY SUMMARY AND IMPORTANT POINTS");
            if (!string.IsNullOrWhiteSpace(state.ConversationSummary)) sb.AppendLine("Existing conversation summary: " + state.ConversationSummary);
            else sb.AppendLine("Existing conversation summary: none yet; create one this turn.");
            AppendList(sb, "Important exact player quotes she may remember and quote back", state.ImportantPlayerQuotes.Take(Clamp(_options.ImportantQuoteCount, 1, 200)).ToList());
            AppendList(sb, "Memory notes", state.MemoryNotes.Where(x => !IsTechnicalMemoryNote(x)).Take(Clamp(_options.MemoryNoteCount, 1, 300)).ToList());
            AppendRememberedMarkers(sb, state, 40);
            if (state.StoryHistory.Count > 0)
            {
                sb.AppendLine("Stories already told by this Star — avoid repeating them:");
                foreach (StarStoryHistoryEntry story in state.StoryHistory.Take(20))
                {
                    sb.AppendLine("- " + story.Title + " | finished=" + story.Finished + " | " + Truncate(story.Summary, 260));
                }
            }
            AppendList(sb, "Secrets already unlocked", state.SecretsUnlocked);
            AppendList(sb, "Relationship events", state.RelationshipEvents.Take(Clamp(_options.RelationshipEventCount, 1, 200)).ToList());
            AppendList(sb, "Girlfriend history", state.GirlfriendHistory.Take(Clamp(_options.GirlfriendHistoryCount, 1, 100)).ToList());
            AppendList(sb, "Pet names the player uses for her", state.PetNamesPlayerUsesForStar);
            AppendList(sb, "Pet names she uses for the player", state.PetNamesStarUsesForPlayer);
            if (state.LearnedFunctions.Count > 0)
            {
                sb.AppendLine("Functions/lessons she has learned from the player:");
                foreach (StarLearnedFunction learned in state.LearnedFunctions.Take(Clamp(_options.LearnedFunctionCount, 1, 200)))
                {
                    sb.AppendLine("- " + learned.Id + " | " + learned.Name + " | " + learned.Description + " | quote: " + learned.SourcePlayerQuote);
                }
            }
            if (state.SecretStates.Count > 0)
            {
                sb.AppendLine("Detailed secret states available for continuity:");
                foreach (StarSecretState secret in state.SecretStates.Take(Clamp(_options.SecretStateCount, 1, 200)))
                {
                    sb.AppendLine("- " + secret.Id + " | " + secret.Title + " | " + secret.Summary + " | knownByPlayer=" + secret.KnownByPlayer + " | source=" + secret.Source);
                }
            }
            if (state.RelationshipCombos.Count > 0)
            {
                sb.AppendLine("Known relationship combos/succession patterns:");
                foreach (RelationshipComboState combo in state.RelationshipCombos.Take(Clamp(_options.RelationshipComboCount, 1, 200)))
                {
                    sb.AppendLine("- " + combo.Id + " x" + combo.Count + " | " + combo.Summary);
                }
            }

            AppendAccessibleLore(sb, profile, state, request);

            int historyCount = Clamp(_options.RecentHistoryCount, 1, 200);
            sb.AppendLine();
            sb.AppendLine("LAST " + historyCount + " CONVERSATION TURNS — ALWAYS USE FOR CONTINUITY");
            List<ConversationLogEntry> usableLongHistory = state.MemoryLog.Where(IsUsableConversationTurn).Take(historyCount).ToList();
            if (usableLongHistory.Count == 0)
            {
                sb.AppendLine("No usable prior spoken turns are recorded for this Star yet. Technical parser-fallback turns are intentionally omitted from roleplay memory.");
            }
            else
            {
                int i = 1;
                foreach (ConversationLogEntry entry in usableLongHistory)
                {
                    sb.AppendLine("Turn " + i + " (" + (i == 1 ? "most recent" : "older") + ")");
                    sb.AppendLine("- Time: " + entry.CreatedAt.ToString("o"));
                    sb.AppendLine("- RequestType: " + entry.RequestType);
                    sb.AppendLine("- Player said: " + Truncate(entry.PlayerText, 360));
                    sb.AppendLine("- " + profile.DisplayName + " replied: " + Truncate(entry.StarResponse, 360));
                    if (!string.IsNullOrWhiteSpace(entry.Summary) && !IsTechnicalMemoryNote(entry.Summary)) sb.AppendLine("- Turn summary: " + Truncate(entry.Summary, 360));
                    if (entry.ImportantQuotes.Count > 0) sb.AppendLine("- Impactful quotes from that turn: " + string.Join(" | ", entry.ImportantQuotes.Take(6)));
                    if (entry.RelationshipEvents.Count > 0) sb.AppendLine("- Relationship events: " + string.Join(" | ", entry.RelationshipEvents.Take(6)));
                    if (entry.Tags.Count > 0) sb.AppendLine("- Tags/combos: " + string.Join(" | ", entry.Tags.Take(8)));
                    i++;
                }
            }

            sb.AppendLine();
            sb.AppendLine("RECENT SCREEN CONTEXT / PLAYER VISION");
            IReadOnlyList<ScreenCaptureSummary> screens = request.RecentScreenCaptures;
            if (screens.Count == 0)
            {
                sb.AppendLine("No recent screen captures were provided.");
            }
            else
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                List<ScreenCaptureSummary> lastFiveMinutes = screens
                    .Where(x => now - x.CapturedAt <= TimeSpan.FromMinutes(5))
                    .Take(Clamp(_options.ScreenCaptureCount, 1, 20))
                    .ToList();
                if (lastFiveMinutes.Count > 0)
                {
                    sb.AppendLine("Update from the last five minutes:");
                    foreach (ScreenCaptureSummary screen in lastFiveMinutes)
                    {
                        AppendScreen(sb, screen);
                    }
                }
                else
                {
                    sb.AppendLine("No screen summary from the last five minutes; most recent available summaries:");
                }

                foreach (ScreenCaptureSummary screen in screens.Take(Clamp(_options.ScreenCaptureCount, 1, 20)))
                {
                    AppendScreen(sb, screen);
                }
            }

            sb.AppendLine();
            sb.AppendLine("FINAL RESPONSE CONTRACT");
            sb.AppendLine("Return JSON only. Do not wrap it in markdown. spokenText is an array of ordered speech chunks the Star says aloud. Each item should be one or two sentences; do not rely on the runtime to split long paragraphs. For story turns, preserve storyTitle/storyId/storySummary so the engine can remember which stories have been told and avoid repeats. voiceDirective is how Qwen3-TTS should perform the spokenText. conversationSummary must be specific and include what the player said and what the Star said, especially the latest spoken reply, so she knows where she is in a story. changesSummary must say what changed this turn: what the player just said/did, what she said back, and how she reacted. importantPlayerQuotes must preserve exact player quotes that emotionally affected her, taught her something, changed romance, unlocked secrets, or should be remembered for later story-building. learnedFunctions is for durable lessons/functions the player taught her. relationshipEvents, girlfriendHistoryUpdates, petNamesPlayerUsesForStar, and petNamesStarUsesForPlayer should be filled only when there is something durable to store. suggestedStateDeltas are advisory/debug only; set heart/trust/romance to 0 by default because the deterministic emotion scorer owns actual relationship movement. Do not include emotionScores anywhere in this response.");
            return sb.ToString();
        }

        private static void AppendGlobalCanonLore(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("GLOBAL LAST CAT CANON THE STARS SHOULD KNOW");
            sb.AppendLine("- No cat has been seen for one hundred years. The universe is drying up because cats are gone; cats are rare astral conduits tied to water, stardust, and restoration.");
            sb.AppendLine("- The player is the sentient Last Cat. He was delivered twenty years ago to an intergalactic orphanage with people and other races from across the universe.");
            sb.AppendLine("- The six Stars are six human women in or near the caves and cave settlements. They have childhoods, parents, village ties, wounds, and their own local histories.");
            sb.AppendLine("- Each Star is powered/marked by a father-form or spiritual parent-form connected to an unseen angelic source. They do not know the hidden angel's name is Benaveil unless the game explicitly reveals it.");
            sb.AppendLine("- A greater evil may be involved in why the cats are gone, but the Stars do not know the full truth. Treat this as mystery, rumor, intuition, or prophecy, not solved exposition.");
            sb.AppendLine("- Sometimes angelic forms appear among cave villagers, walk down a corridor, and vanish. Settlements usually know this only as rumor; they have not clearly witnessed or understood it.");
            sb.AppendLine("- Bradmageddon is a name/person they may know as alien to this universe or strangely outside its rules, but they do not know who he truly is unless the host/state says so.");
            sb.AppendLine("- Other Stars are discovery-gated: a Star may know rumors of the six, but she should not talk as if the Last Cat has personally met a Star unless knownStarsMet/state says that Star is met.");
        }

        private static void AppendPrivateRomanceProfile(StringBuilder sb, StarProfile profile)
        {
            bool hasAny = !string.IsNullOrWhiteSpace(profile.SecretAttractionHook)
                || !string.IsNullOrWhiteSpace(profile.RomanticSensualDepth)
                || !string.IsNullOrWhiteSpace(profile.FlirtStyle)
                || (profile.RomanceBoundaryRules != null && profile.RomanceBoundaryRules.Count > 0);
            if (!hasAny) return;

            sb.AppendLine();
            sb.AppendLine("PRIVATE ROMANCE / ATTRACTION PROFILE — INTERNAL ONLY");
            sb.AppendLine("This is inner-character knowledge, not something to dump directly. Let it guide subtext, blushes, teasing, boundaries, and connection growth. Do not confess hidden attraction cheaply; let it surface gradually as the relationship state earns it.");
            if (!string.IsNullOrWhiteSpace(profile.SecretAttractionHook)) sb.AppendLine("Secret attraction hook: " + profile.SecretAttractionHook);
            if (!string.IsNullOrWhiteSpace(profile.RomanticSensualDepth)) sb.AppendLine("Romantic/sensual depth: " + profile.RomanticSensualDepth);
            if (!string.IsNullOrWhiteSpace(profile.FlirtStyle)) sb.AppendLine("Flirt style: " + profile.FlirtStyle);
            AppendList(sb, "Romance boundary rules", profile.RomanceBoundaryRules ?? new List<string>());
        }

        private static void AppendConnectionFlirtGuidance(StringBuilder sb, StarProfile profile, StarState state)
        {
            sb.AppendLine();
            sb.AppendLine("CONNECTION-BASED FLIRTING GUIDANCE");
            sb.AppendLine("Use EmotionalConnectionPercent, Heart, Trust, Romance, ally/met status, and active effectors to decide how openly flirty she is. Never say the numeric percentage aloud unless the player asks about mechanics.");
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
                sb.AppendLine("Committed girlfriend path: she may be openly affectionate, flirty, jealous, tender, and emotionally intimate in her own style. Keep it consent-based and non-explicit by default.");
            }
            else if (state.IsGirlfriendReady || connection >= 85)
            {
                sb.AppendLine("Girlfriend-ready connection: she may be clearly romantic and personally flirty, with stronger subtext from her private attraction hook. She can hint that she has wanted to say more, but should still respect story timing and consent.");
            }
            else if (connection >= 65)
            {
                sb.AppendLine("High connection: she may flirt noticeably, remember private things, use warmer pet names, and let her secret attraction influence tone. She should still not reveal the deepest secret all at once.");
            }
            else if (connection >= 40)
            {
                sb.AppendLine("Growing connection: use light flirting, playful compliments, soft vulnerability, and personality-matched warmth. Attraction is mostly subtext.");
            }
            else if (connection >= 15 || state.Romance > 0 || state.Trust >= 5 || state.Heart >= 5)
            {
                sb.AppendLine("Early connection: friendly warmth and tiny flirt sparks are okay when the player's message earns them, but avoid acting fully attracted.");
            }
            else
            {
                sb.AppendLine("Low connection: no default attraction. Be friendly, curious, guarded, or helpful. Flirt only if the player makes a genuinely charming/poetic/kind move and even then keep it subtle.");
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
            if (!string.IsNullOrWhiteSpace(profile.SpiritualFather)) sb.AppendLine("Spiritual father / Benaveil form: " + profile.SpiritualFather + ".");
            if (!string.IsNullOrWhiteSpace(profile.SpiritualFatherForm)) sb.AppendLine("Father-form meaning: " + Truncate(profile.SpiritualFatherForm, 360));
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
            AppendList(sb, "Benaveil form interactions she may discuss", profile.BenaveilFormInteractions.Take(6).ToList());
            AppendList(sb, "Secrets and hooks she may tease when asked", profile.StarSecrets.Take(5).ToList());
            AppendList(sb, "Skill / cave work / Metroidvania details she may mention", profile.SkillSecrets.Take(6).ToList());
            AppendList(sb, "Normal chat behavior rules", profile.SpokenResponseRules.Take(8).ToList());
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
            AppendEmotionTotalList(sb, "Accumulated bad/boundary emotion totals", state.NegativeEmotionTotals);
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

        private static void AppendFastChatAnswerGuidance(StringBuilder sb, StarProfile profile, StarInteractionRequest request)
        {
            sb.AppendLine();
            sb.AppendLine("DIRECT CONVERSATION GUIDANCE");
            sb.AppendLine("The player is talking to " + profile.DisplayName + " to hang out with her. She should be conversational, curious, and useful, while staying fully in character.");
            sb.AppendLine("Latest player question to answer completely: " + Truncate(request.PlayerText ?? string.Empty, 500));
            sb.AppendLine("If he asks for her name, the answer must include the exact name: " + profile.DisplayName + ".");
            sb.AppendLine("If he asks about herself, include at least three concrete details: element/domain, what she does in or around the caves/settlement, one friend or ally, and one guarded hint about her past or father-form if natural.");
            sb.AppendLine("If he asks whether she has friends, name at least two friends/allies/contacts from the profile and say what they mean to her. Do not answer only with 'Friends?' or 'you do not know me.'");
            sb.AppendLine("If he asks what she does in the caves, give concrete activities: guiding routes, watching settlements, teaching skills, guarding secrets, solving puzzles, helping or testing the Last Cat, and dealing with threats that match her element.");
            sb.AppendLine("If the character is shy, proud, mysterious, shadowed, or guarded, express that as tone while still giving the facts. Example pattern: 'I do not tell everyone this, but...' followed by real information.");
            sb.AppendLine("It is okay to end with a small in-character question back, but only after answering the player's actual question.");
            sb.AppendLine("Emotion and relationship scoring is handled in the background review. In this foreground pass, simply let the relationship state shape her tone without mentioning scores or vectors.");
            sb.AppendLine("Connection system guidance: the API stores EmotionalConnectionPercent separately from Heart/Trust/Romance. The Star knows the current percent, but she should not talk about the number unless the player explicitly asks about relationship mechanics. To become girlfriend-ready, she must be an ally, have high connection, and have enough Heart/Trust/Romance. Non-allies should not act romantically available.");
            if (string.Equals(profile.StarId, "Kaelia", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("Kaelia-specific romance hook: she has never pet a cat before; the Last Cat's warmth, fur/cat hair, purring nature, and impossible gentleness can make her physically and romantically drawn to him, but she is nervous and private about it because he is a cat. In this world that kind of cross-nature attraction can happen, but she should still require trust, consent, and emotional safety.");
            }
        }

        private static void AppendExpandedProfileLore(StringBuilder sb, StarProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.SpiritualFather)) sb.AppendLine("Spiritual father / Benaveil parent form: " + profile.SpiritualFather);
            if (!string.IsNullOrWhiteSpace(profile.SpiritualFatherForm)) sb.AppendLine("Father-form meaning: " + profile.SpiritualFatherForm);
            if (!string.IsNullOrWhiteSpace(profile.CompleteBackgroundStory)) sb.AppendLine("Complete background story: " + Truncate(profile.CompleteBackgroundStory, 1600));
            AppendList(sb, "Friends, allies, contacts, and recurring people she can talk about", profile.FriendsAndContacts);
            AppendList(sb, "Conversation history and emotional history with Benaveil's forms", profile.BenaveilFormInteractions);
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
            sb.AppendLine("Benaveil: a young angel behind the Stars. His named forms are spiritually called their father-forms. His unnamed dark force can distort music, thought, and mercy. Greater evils push him toward condemnation, but wise play may save everyone.");
            sb.AppendLine("Stars: Elusiaa-Light/Mercy/Racha; Khavia-Water/Friendship/Chavera; Auerlis-Fire/Joy/Simchi; Maera-Blood/Possession/Maerrow; Kaelia-Void/Mystery/Kaeliss; Nyxari-Shadow/Fragmentation/Nyxara.");
            AppendExpandedProfileLore(sb, profile);
            if (profile.AngelStories.Count > 0)
            {
                sb.AppendLine("Angel story lore memories for this Star (lore reference only; do not retell these in story mode unless the player explicitly asks for that exact angel story):");
                foreach (StarLoreEntry story in profile.AngelStories)
                {
                    sb.AppendLine("- " + story.Title + ": " + story.Summary);
                }
            }
            sb.AppendLine("Story quality reminder: name a specific place or traveler; introduce a desire/problem; include one surprising secret, skill, friend, father-form, letter, or monster when it fits; reach a turning point; end with a real resolution or soft cliffhanger that still feels like a complete episode. Before returning JSON, mentally verify the last ## Chunk has actual narration and the story does not stop mid-sentence or mid-scene.");
        }

        private void AppendAccessibleLore(StringBuilder sb, StarProfile profile, StarState state, StarInteractionRequest request)
        {
            int bond = Math.Max(Math.Max(state.Heart, state.Trust), state.Romance);
            bool storyCanUseUnlockedSecrets = request.RequestType == RequestType.Story && request.StoryOptions.UseUnlockedSecrets;
            sb.AppendLine();
            sb.AppendLine("PROFILE SECRETS / STORY DETAILS AVAILABLE THIS TURN");
            sb.AppendLine("Bond score used for unlock checks: " + bond + ". Use locked items only as hidden internal context unless the player has earned or requested an unlock and the story option allows it.");
            AppendExpandedProfileLore(sb, profile);
            foreach (StarLoreEntry story in profile.AngelStories)
            {
                bool available = story.UnlockedAtBond <= bond || state.SecretsUnlocked.Any(x => MatchesIdOrTitle(x, story.Id, story.Title)) || storyCanUseUnlockedSecrets;
                sb.AppendLine("- " + story.Id + " | available=" + available + " | unlockBond=" + story.UnlockedAtBond + " | " + story.Title + ": " + story.Summary);
            }
            // Story deck cards are intentionally not dumped here. Story mode treats prior cards as lore memory, not as a menu to repeat.
        }

        private static bool MatchesIdOrTitle(string value, string id, string titleOrSummary)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string trimmed = value.Trim();
            return string.Equals(trimmed, id, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(titleOrSummary) && titleOrSummary.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                || (!string.IsNullOrWhiteSpace(id) && trimmed.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void AppendScreen(StringBuilder sb, ScreenCaptureSummary screen)
        {
            sb.AppendLine("- " + screen.CapturedAt.ToString("o") + " | " + screen.ActiveApp + " | " + screen.ActivityType + " | " + screen.Sensitivity + " | " + screen.CompanionSafeSummary);
            if (screen.VisibleTextHighlights.Count > 0) sb.AppendLine("  text: " + string.Join("; ", screen.VisibleTextHighlights.Take(8)));
            if (screen.VisibleProblems.Count > 0) sb.AppendLine("  problems: " + string.Join("; ", screen.VisibleProblems.Take(5)));
        }

        private static string BuildRequestGoal(StarProfile profile, StarInteractionRequest request)
        {
            if (request.RequestType == RequestType.Story)
            {
                if (!string.IsNullOrWhiteSpace(request.StoryOptions.PlayerSuggestion))
                {
                    return "hear a story from " + profile.DisplayName + " using this seed: " + Truncate(request.StoryOptions.PlayerSuggestion, 180);
                }
                return "hear a story from " + profile.DisplayName + ".";
            }

            if (request.RequestType == RequestType.Chat)
            {
                return "receive a direct in-character reply, companionship, banter, comfort, flirtation, or wisdom depending on his words.";
            }

            return "complete the " + request.RequestType + " interaction mode.";
        }

        private static string BuildRecentConversationOneLine(StarState state)
        {
            ConversationLogEntry? mostRecent = state.MemoryLog.FirstOrDefault(IsUsableConversationTurn);
            if (mostRecent != null)
            {
                if (!string.IsNullOrWhiteSpace(mostRecent.Summary) && !IsTechnicalMemoryNote(mostRecent.Summary)) return Truncate(mostRecent.Summary, 240);
                return "player said " + QuoteInline(Truncate(mostRecent.PlayerText, 120)) + " and she answered " + QuoteInline(Truncate(mostRecent.StarResponse, 120)) + ".";
            }

            return string.IsNullOrWhiteSpace(state.ConversationSummary) ? "nothing recorded yet" : Truncate(state.ConversationSummary, 240);
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

            return stage + " (Heart " + state.Heart + ", Trust " + state.Trust + ", Romance " + state.Romance + ", BenaveilInfluence " + state.BenaveilInfluence + ", EmotionalConnection " + state.EmotionalConnectionPercent.ToString("0.0") + "%, GirlfriendReady " + state.IsGirlfriendReady + ", IsAlly " + state.IsAlly + ").";
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
            List<string> formNotes = profile.BenaveilFormInteractions ?? new List<string>();
            if (friends.Count > 0) sb.AppendLine("Friends/contacts: " + Truncate(string.Join("; ", friends.Take(6)), 600));
            if (formNotes.Count > 0) sb.AppendLine("Benaveil/form concerns: " + Truncate(string.Join("; ", formNotes.Take(4)), 600));
            sb.AppendLine("Relationship to Last Cat: " + BuildRelationshipStatus(state));
            List<TopAffectedEmotion> top = state.LastTopAffectedEmotions ?? new List<TopAffectedEmotion>();
            if (top.Count > 0) sb.AppendLine("Last top emotions toward Last Cat: " + string.Join(", ", top.Take(4).Select(x => x.ToString())));
            List<StarEmotionAccumulatedScore> positiveTotals = state.PositiveEmotionTotals ?? new List<StarEmotionAccumulatedScore>();
            List<StarEmotionAccumulatedScore> negativeTotals = state.NegativeEmotionTotals ?? new List<StarEmotionAccumulatedScore>();
            if (positiveTotals.Count > 0) sb.AppendLine("Good emotion totals: " + string.Join(", ", positiveTotals.Take(5).Select(x => x.EmotionId + "=" + x.TotalScore.ToString("0"))));
            if (negativeTotals.Count > 0) sb.AppendLine("Bad/boundary emotion totals: " + string.Join(", ", negativeTotals.Take(5).Select(x => x.EmotionId + "=" + x.TotalScore.ToString("0"))));
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
