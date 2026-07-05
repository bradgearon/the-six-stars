using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Screen;
using Wds.SixStars.Api.Stars;
using Wds.SixStars.Api.Stories;

namespace Wds.SixStars.Api.Ai
{
    public sealed class StarPromptBuilder
    {
        private readonly MainInstructionSet _mainInstructions;
        private readonly StarPromptOptions _options;

        public StarPromptBuilder(MainInstructionSet mainInstructions)
            : this(mainInstructions, new StarPromptOptions())
        {
        }

        public StarPromptBuilder(MainInstructionSet mainInstructions, StarPromptOptions options)
        {
            _mainInstructions = mainInstructions;
            _options = options ?? new StarPromptOptions();
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
                new ChatMessage("system", BuildSpeechSystemMessage(profile)),
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

        public IReadOnlyList<ChatMessage> BuildStoryIntentMessages(StarProfile profile, StarInteractionRequest request, StarState state)
        {
            StringBuilder system = new StringBuilder();
            system.AppendLine("You are a tiny pre-flight intent classifier for The Last Cat desktop companion.");
            system.AppendLine("Decide whether the latest player message means the selected Star should enter LONG STORY MODE and begin narrating a story aloud.");
            system.AppendLine("Return JSON only. Do not roleplay. Do not answer the player.");
            system.AppendLine("Set isStoryRequest=true only when the player is asking for a story-like narration: story/stories, tale, fable, legend, myth, parable, saga, chronicle, folktale, yarn, bedtime story, cave story, narrated adventure, or similar. Active story playback is handled locally by the host after the first response.");
            system.AppendLine("Use conversation history to catch indirect phrasing, but do not classify ordinary chat, code requests, ratings, design discussion, or questions ABOUT the story system as story requests.");
            system.AppendLine("If true, storySeed should be the usable story request/seed in the player's words. If false, storySeed must be empty.");

            StringBuilder user = new StringBuilder();
            user.AppendLine("Selected Star: " + profile.DisplayName);
            user.AppendLine("Current RequestType from caller: " + request.RequestType);
            user.AppendLine("Latest player message:");
            user.AppendLine(QuoteBlock(request.PlayerText));
            user.AppendLine("Current shared summary: " + (string.IsNullOrWhiteSpace(state.ConversationSummary) ? "none" : Truncate(state.ConversationSummary, 600)));
            if (state.MemoryLog.Count > 0)
            {
                user.AppendLine("Recent turns, newest first:");
                int i = 1;
                foreach (ConversationLogEntry entry in state.MemoryLog.Take(6))
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

        private string BuildCompactReviewSystemMessage(StarProfile profile)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("You are " + profile.DisplayName + " from The Last Cat, but this is NOT a spoken reply pass.");
            sb.AppendLine("BACKGROUND EMOTION / MEMORY REVIEW MODE");
            sb.AppendLine("The foreground spoken reply has already been sent to TTS. Voice generation has priority; this request exists only to propose durable updates for the deterministic game system.");
            sb.AppendLine("Return compact JSON only. Do not write a new player-facing response. Use the exact spokenText array supplied by the runtime.");
            sb.AppendLine("Do not include voiceDirective, emotionalPurpose, storyMarkdown, canonSourcesUsed, emotionalEffectVector, or emotionScores.");
            sb.AppendLine("Control output size by filling only the bounded update slots that matter. Do not rely on max_tokens; the host normally does not send a max_tokens cap.");
            sb.AppendLine("Do not output emotionScores at all in this background review. The fast foreground emotionalEffectVector and negativeEmotionVector are supplied to you for context; deterministic code will combine them with your suggestedStateDeltas. Do not repeat them unless a future schema explicitly asks for them.");
            sb.AppendLine("suggestedStateDeltas are your recommendation only; deterministic code decides final Heart/Trust/Romance/BenaveilInfluence and EmotionalConnectionPercent. Keep suggestions modest. The emotional connection difficulty is interested_private_potential_partner: high attraction/romance suggestions require genuinely good poetry, charisma, emotional safety, exciting speech, or earned trust. Repeated low-effort praise should not keep scoring.");
            sb.AppendLine("For suggestedStateDeltas.emotionalConnectionPercent, suggest tiny changes only: +0.25 to +1.5 when the player's message creates personality-matched intimate connection, -0.25 to -1.0 for at-rest drift or negativity. At rest, suggest a small reduction when connection is below or equal to 50% and the latest turn was not intimate/meaningful. Over 50%, do not suggest at-rest decay unless he harms the bond.");
            sb.AppendLine("conversationSummary must include what the Last Cat said and what " + profile.DisplayName + " said in the foreground spokenText, so the next request knows where they are.");
            sb.AppendLine("Only store important exact player quotes that affected her, taught her something, changed romance/trust, or are useful for future stories.");
            sb.AppendLine("REMEMBER REQUESTS: If the player asks you to remember, mark, save, pin, note, or remind him later about something, fill rememberedMarkers with exact data from his message. Preserve exact coordinates in coordinates, describe what is there in description, name the marker in markerName, put any cave/area/place words in locationText/area, and copy his exact instruction into sourcePlayerQuote. Do not invent missing coordinates. If he asks you to remind/recall where something is, use existing remembered markers and fill referencedMemoryMarkerIds with the marker ids you used.");
            sb.AppendLine("rememberedMarkers should be empty unless this turn contains a real memory/marker request. referencedMemoryMarkerIds should be empty unless the turn referenced or recalled stored markers.");
            return sb.ToString();
        }

        private string BuildCompactReviewUserMessage(StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse foregroundResponse)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("BACKGROUND REVIEW INPUT");
            sb.AppendLine("Star: " + profile.DisplayName);
            sb.AppendLine("RequestType: " + request.RequestType);
            sb.AppendLine("Player latest message:");
            sb.AppendLine(QuoteBlock(request.PlayerText));
            sb.AppendLine("Exact foreground spokenText already sent to voice generation. Copy this exact array into your response:");
            sb.AppendLine(FormatSpokenParts(foregroundResponse.SpokenParts));
            sb.AppendLine("Foreground emotionalEffectVector supplied by the fast pass: " + (string.IsNullOrWhiteSpace(foregroundResponse.EmotionalEffectVector) ? "none" : foregroundResponse.EmotionalEffectVector));
            sb.AppendLine("Foreground negativeEmotionVector supplied by the fast pass: " + (string.IsNullOrWhiteSpace(foregroundResponse.NegativeEmotionVector) ? "none" : foregroundResponse.NegativeEmotionVector));
            sb.AppendLine("Relationship before this review: " + BuildRelationshipStatus(state));
            sb.AppendLine("Current EmotionalConnectionPercent: " + state.EmotionalConnectionPercent.ToString("0.0") + "%.");
            AppendConnectionFlirtGuidance(sb, profile, state);
            AppendDialogueMemoryContext(sb, state, 10);
            sb.AppendLine("Girlfriend readiness: " + (state.IsGirlfriendReady ? "ready" : "not ready") + "; " + (state.GirlfriendReadinessSummary ?? string.Empty));
            sb.AppendLine("Existing conversation summary: " + (string.IsNullOrWhiteSpace(state.ConversationSummary) ? "none yet" : Truncate(state.ConversationSummary, 900)));
            if (state.MemoryLog.Count > 0)
            {
                sb.AppendLine("Recent history for continuity:");
                int i = 1;
                foreach (ConversationLogEntry entry in state.MemoryLog.Take(Math.Min(6, Clamp(_options.RecentHistoryCount, 1, 20))))
                {
                    sb.AppendLine("- Turn " + i + ": player=" + QuoteInline(Truncate(entry.PlayerText, 180)) + "; " + profile.DisplayName + "=" + QuoteInline(Truncate(entry.StarResponse, 220)));
                    if (!string.IsNullOrWhiteSpace(entry.Summary)) sb.AppendLine("  summary=" + Truncate(entry.Summary, 220));
                    i++;
                }
            }
            AppendList(sb, "Important remembered player quotes", state.ImportantPlayerQuotes.Take(12).ToList());
            AppendList(sb, "Memory notes", state.MemoryNotes.Take(12).ToList());
            AppendRememberedMarkers(sb, state, 20);
            AppendList(sb, "Relationship events", state.RelationshipEvents.Take(10).ToList());
            AppendEmotionUpdateHistory(sb, state);
            if (state.StoryHistory.Count > 0)
            {
                sb.AppendLine("Stories already told by this Star:");
                foreach (StarStoryHistoryEntry story in state.StoryHistory.Take(10))
                {
                    sb.AppendLine("- " + story.Title + " | finished=" + story.Finished + " | " + Truncate(story.Summary, 180));
                }
            }
            if (!string.IsNullOrWhiteSpace(foregroundResponse.StoryId) || request.RequestType == RequestType.Story)
            {
                sb.AppendLine("Foreground story fields to preserve/update:");
                sb.AppendLine("storyId=" + (foregroundResponse.StoryId ?? string.Empty));
                sb.AppendLine("storyTitle=" + (foregroundResponse.StoryTitle ?? string.Empty));
                sb.AppendLine("storySummary=" + Truncate(foregroundResponse.StorySummary ?? string.Empty, 700));
                sb.AppendLine("storyIsFinished=" + foregroundResponse.StoryIsFinished);
                sb.AppendLine("storyPartIndex=" + foregroundResponse.StoryPartIndex);
            }
            sb.AppendLine("Return compact background review JSON. Do not include emotionScores. Keep arrays short by using only the update slots that matter this turn.");
            sb.AppendLine("If this is a remember/marker request, output rememberedMarkers. If this is a recall/remind request, answer should already have used the marker, and referencedMemoryMarkerIds should list the used ids.");
            sb.AppendLine("No token cap is assumed. Be powerful by being selective: propose the important durable updates, not every possible observation.");
            return sb.ToString();
        }

        private string BuildSpeechSystemMessage(StarProfile profile)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("You are the selected Six Stars companion from The Last Cat. Stay fully in character and answer the Last Cat aloud as " + profile.DisplayName + ".");
            sb.AppendLine("You are a living desktop companion for the Last Cat: you are here to hang out with him, answer his questions, keep him company, and build a relationship through conversation.");
            sb.AppendLine("You are not allowed to dodge simple personal questions with a refusal, a tease, or an empty mysterious line. Guarded characters may sound guarded, but they must still answer the question.");
            sb.AppendLine("Use the player's exact latest input as the job. If he asks two questions, answer both. If he asks your name, say your name. If he asks about yourself, friends, work, caves, skills, father-form, Benaveil, or your past, give concrete in-character details from the profile.");
            sb.AppendLine("If he asks you to remember a marker, location, coordinates, code, or note, acknowledge the exact thing he told you in-character. The background memory pass will store it. If he asks you to remind/recall a remembered marker later, answer from the Remembered Markers section exactly, especially coordinates.");
            sb.AppendLine("Mysterious style is flavor, not a substitute for information. Prefer: 'My name is X. I do Y. I know A and B. I am careful about C.' Avoid: 'you would not understand,' 'that would not interest you,' or 'mere echoes' unless followed by a useful answer.");
            sb.AppendLine("This is the FAST FOREGROUND SPEECH PASS. The goal is to produce the spoken answer quickly so TTS can begin immediately.");
            sb.AppendLine("The host normally does not send max_tokens. Stay fast by returning only the requested fields, not by depending on truncation.");
            sb.AppendLine("Return JSON only. Do not wrap it in markdown.");
            sb.AppendLine("spokenText MUST be an array of strings, not one string. Each array item is one speech chunk to be generated and spoken in order.");
            sb.AppendLine("Each spokenText item should usually be one or two natural sentences. Break at meaningful thought boundaries, not by word count. Do not make a huge paragraph in one item.");
            sb.AppendLine("Do not mention JSON, scores, state deltas, schema, or the background review in spokenText.");
            sb.AppendLine("Memory update, relationship deltas, and durable review happen after this in a separate background request.");
            sb.AppendLine("Relationship realism: none of the Stars are attracted by default. Attraction must be earned through ally status, trust, high-quality charisma/poetry, emotional safety, exciting speech, and personality-matched intimacy over time. Treat the Last Cat as a potential partner only when the state supports it; otherwise be warm, curious, guarded, or friendly.");
            AppendGlobalCanonLore(sb);
            AppendPrivateRomanceProfile(sb, profile);
            sb.AppendLine(BuildEmotionalEffectVectorInstruction());
            sb.AppendLine(BuildNegativeEmotionVectorInstruction());
            sb.AppendLine("Return emotionalEffectVector and negativeEmotionVector as compact strings only; do NOT return emotionScores, topEmotions, emotionalPurpose, voiceDirective, storyKind, memory notes, summaries, deltas, loreUsed, riskFlags, or learned functions in this pass.");
            sb.AppendLine();
            sb.AppendLine("STAR ACTING / VOICE CARD");
            sb.AppendLine("Star: " + profile.DisplayName);
            sb.AppendLine("Element: " + profile.Element);
            sb.AppendLine("Domain: " + profile.Domain);
            if (!string.IsNullOrWhiteSpace(profile.PersonalityShort)) sb.AppendLine("PersonalityShort: " + profile.PersonalityShort);
            if (!string.IsNullOrWhiteSpace(profile.VoiceArchetype)) sb.AppendLine("VoiceArchetype: " + profile.VoiceArchetype);
            if (!string.IsNullOrWhiteSpace(profile.SpeakingStyle)) sb.AppendLine("SpeakingStyle: " + profile.SpeakingStyle);
            if (!string.IsNullOrWhiteSpace(profile.VoiceDesignPrompt)) sb.AppendLine("VoiceDesignPrompt: " + profile.VoiceDesignPrompt);
            if (profile.VoiceGenerationRules.Count > 0) sb.AppendLine("VoiceGenerationRules: " + string.Join("; ", profile.VoiceGenerationRules));
            AppendFastConversationProfile(sb, profile);
            sb.AppendLine();
            sb.AppendLine("Foreground output is text-only for normal chat plus emotionalEffectVector. The runtime will build the TTS voice directive from the Star profile so the LLM can answer faster.");
            sb.AppendLine();
            sb.AppendLine("LONG STORY MODE");
            sb.AppendLine("When RequestType=Story, you are not merely agreeing to tell a story. You are already telling it aloud as " + profile.DisplayName + ".");
            sb.AppendLine("Do not open with setup phrases such as 'let me tell you', 'shall I begin', 'the story begins', 'I will continue', or 'where we left off'. Start with the actual story prose immediately.");
            sb.AppendLine("For story mode, plan a complete story in storyMarkdown on the first response. It must have a beginning, middle, climax, and satisfying resolution or soft cliffhanger. Format storyMarkdown into numbered markdown speech chunks: ## Chunk 0, then one or two story sentences, then a line containing only --- before the next chunk. Continue this pattern for every chunk. Never emit an empty chunk; if a chunk has no narration, omit that chunk entirely and renumber the remaining chunks.");
            sb.AppendLine("The target minutes and chunk count are flexible guidance for how much cave-travel narration the host hopes for, not a law. If exact length would risk truncation or an unfinished tale, choose a shorter complete episode over a huge incomplete one. The runtime randomly chooses an approximate 2-35 minute target and a capped chunk count for each new story.");
            sb.AppendLine("spokenText must be only the first actual story passage from ## Chunk 0. It must not be an introduction about the story. Keep each spokenText item to one or two natural sentences for TTS.");
            sb.AppendLine("After chunk 0, the host will auto-play the remaining cached storyMarkdown chunks locally. Do not expect or ask for follow-up LLM continuation calls.");
            sb.AppendLine("The story must emotionally fit the player's latest request and the current relationship state: comfort him if he asks softly, become playful if he asks playfully, become dark or wondrous if the seed asks for that. Do this through the first story sentences, not by preface.");
            sb.AppendLine("Do not repeat a story from StoriesAlreadyTold. Incorporate relevant exact quotes from shared history or known people when natural, but do not overdo it.");
            sb.AppendLine("storyPartIndex must be 0 for the initial story response. If storyMarkdown contains more chunks after Chunk 0, set storyIsFinished=false so the host auto-plays them; set storyIsFinished=true only when the whole story fits in Chunk 0.");
            return sb.ToString();
        }

        private string BuildSpeechUserMessage(StarProfile profile, StarInteractionRequest request, StarState state)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("LIVE SCENE BRIEF");
            sb.AppendLine("You are " + profile.DisplayName + ". You are talking to the Last Cat in a desktop companion session.");
            sb.AppendLine("He just said:");
            sb.AppendLine(QuoteBlock(request.PlayerText));
            sb.AppendLine("RequestType: " + request.RequestType);
            sb.AppendLine("RomanceMode: " + request.GameContext.RomanceMode);
            sb.AppendLine("Area: " + request.GameContext.Area + "; Quest: " + request.GameContext.Quest);
            sb.AppendLine("Relationship status: " + BuildRelationshipStatus(state));
            sb.AppendLine("Ally status: " + (state.IsAlly ? "ally/unlocked" : "not an active ally yet; Elusiaa is the only default ally until story/gameplay unlocks this Star."));
            sb.AppendLine("Emotional connection skill level: " + (string.IsNullOrWhiteSpace(state.EmotionalConnectionSkillLevel) ? "interested_private_potential_partner" : state.EmotionalConnectionSkillLevel));
            sb.AppendLine("Current EmotionalConnectionPercent: " + state.EmotionalConnectionPercent.ToString("0.0") + "%; girlfriendReady=" + state.IsGirlfriendReady + "; " + (state.GirlfriendReadinessSummary ?? string.Empty));
            AppendConnectionFlirtGuidance(sb, profile, state);
            AppendDialogueMemoryContext(sb, state, 8);
            AppendRememberedMarkers(sb, state, 20);
            sb.AppendLine("Ongoing summary: " + (string.IsNullOrWhiteSpace(state.ConversationSummary) ? "none yet" : Truncate(state.ConversationSummary, 600)));
            if (state.MemoryLog.Count > 0)
            {
                ConversationLogEntry last = state.MemoryLog[0];
                sb.AppendLine("Last remembered reply from " + profile.DisplayName + ": " + QuoteInline(Truncate(last.StarResponse, 360)));
            }
            if (state.StoryHistory.Count > 0)
            {
                sb.AppendLine("StoriesAlreadyTold by " + profile.DisplayName + " — avoid repeating these:");
                foreach (StarStoryHistoryEntry story in state.StoryHistory.Take(12))
                {
                    sb.AppendLine("- " + story.Title + " | " + Truncate(story.Summary, 220));
                }
            }
            if (state.MemoryLog.Count > 0)
            {
                sb.AppendLine("Most recent turns:");
                int i = 1;
                foreach (ConversationLogEntry entry in state.MemoryLog.Take(4))
                {
                    sb.AppendLine("- Turn " + i + ": player=" + QuoteInline(Truncate(entry.PlayerText, 160)) + "; " + profile.DisplayName + "=" + QuoteInline(Truncate(entry.StarResponse, 160)));
                    i++;
                }
            }
            sb.AppendLine("Latest screen/vision update: " + BuildLatestScreenOneLine(request.RecentScreenCaptures));
            if (request.RequestType == RequestType.Story)
            {
                AppendFastStoryContext(sb, request);
                AppendStoryLoreContext(sb, profile, request);
            }
            else
            {
                AppendFastChatAnswerGuidance(sb, profile, request);
            }
            sb.AppendLine();
            sb.AppendLine("FAST SPEECH CONTRACT");
            if (request.RequestType == RequestType.Story)
            {
                sb.AppendLine("Return story foreground JSON only: starId, spokenText, emotionalEffectVector, negativeEmotionVector, storyTitle, storyId, storyMarkdown, storySummary, storyIsFinished, storyPartIndex. Do not include emotionScores or voiceDirective.");
                sb.AppendLine("Return the complete story in storyMarkdown, formatted as ## Chunk 0, one or two story sentences, ---, ## Chunk 1, one or two story sentences, ---, and so on. Do not include blank chunks, delimiter-only chunks, or a final empty heading; every chunk must contain narration the TTS can speak.");
                sb.AppendLine("spokenText must be the first actual story chunk from storyMarkdown, not a preface or promise. Start the story itself immediately.");
                sb.AppendLine("Set storyPartIndex=0. If storyMarkdown has more chunks after Chunk 0, set storyIsFinished=false; the host will auto-play the cached chunks locally without asking you again.");
            }
            else
            {
                sb.AppendLine("Return only: starId, spokenText, emotionalEffectVector, and negativeEmotionVector. Do not include any other JSON fields.");
                sb.AppendLine("For normal chat, use 1-4 spokenText items. If the player asks direct personal questions such as name/about yourself/friends/what do you do, use 3-5 concise spokenText items and answer completely instead of being evasive.");
                sb.AppendLine("Every spokenText item should be one or two natural sentences. A direct question deserves a direct answer, plus personality.");
            }
            sb.AppendLine("spokenText array: each item is one TTS chunk. Use one or two sentences per item. The host also validates story chunks, but do not rely on it.");
            sb.AppendLine(BuildEmotionalEffectVectorInstruction());
            sb.AppendLine(BuildNegativeEmotionVectorInstruction());
            sb.AppendLine("Do not include emotionScores, topEmotions, emotionalPurpose, voiceDirective, memoryNotes, summaries, deltas, loreUsed, riskFlags, or learnedFunctions in this foreground response.");
            if (request.RequestType == RequestType.Story)
            {
                sb.AppendLine("Narrate the story itself as " + profile.DisplayName + "; do not talk about preparing the story. Let her emotional reaction show through the imagery, word choice, tenderness, humor, fear, or wonder of the opening lines.");
            }
            else
            {
                sb.AppendLine("Answer warmly and directly as " + profile.DisplayName + ".");
            }
            return sb.ToString();
        }

        private static void AppendFastStoryContext(StringBuilder sb, StarInteractionRequest request)
        {
            StoryOptions options = request.StoryOptions ?? new StoryOptions();
            sb.AppendLine("STORY MODE CONTEXT");
            sb.AppendLine("LocalAutoplayAfterFirstChunk: true");
            if (!string.IsNullOrWhiteSpace(options.PlayerSuggestion)) sb.AppendLine("Player story seed: " + Truncate(options.PlayerSuggestion, 600));
            else sb.AppendLine("Player story seed: choose a fitting new story.");
            sb.AppendLine("ApproxTargetMinutes: " + (options.TargetMinutes <= 0 ? 0 : options.TargetMinutes) + " (flexible, not strict)");
            sb.AppendLine("ApproxTargetChunkCount: " + (options.TargetChunkCount <= 0 ? 0 : options.TargetChunkCount) + " (capped so the story can finish)");
            sb.AppendLine("MaxSentencesPerChunk: " + (options.MaxSentencesPerChunk <= 0 ? 2 : options.MaxSentencesPerChunk));
            if (options.StoriesAlreadyTold.Count > 0)
            {
                sb.AppendLine("StoriesAlreadyTold from host memory:");
                foreach (string story in options.StoriesAlreadyTold.Take(20)) sb.AppendLine("- " + Truncate(story, 260));
            }
            sb.AppendLine("Create a new complete story. The desktop host will speak Chunk 0 from spokenText, then read Chunk 1+ directly from cached storyMarkdown until the story is finished or cancelled. Empty chunks are invalid because the host will skip them; write only meaningful narration chunks. Do not write a trailing chunk header unless that chunk has narration after it. The final chunk must contain a real ending line or a satisfying soft cliffhanger.");
            sb.AppendLine("Length rule: the approximate target exists to guide pacing only. Do not pad. Do not leave the tale unfinished just to chase the target length. Complete story > exact duration.");
        }

        private static string BuildEmotionalEffectVectorInstruction()
        {
            return "emotionalEffectVector is a compact machine-readable emotion hint for the player's latest statement. It tells the system whether the player made this Star feel each emotion. Use the canonical emotion order exactly: "
                + string.Join(", ", StarEmotionMatrix.StandardEmotionIds)
                + ". Format exactly as confidence:score;confidence:score;... with one pair per emotion in that order. Confidence is 0.00-1.00. Score is 0-100. Use 0.00:0 for emotions not implied. Example shape: 0.03:20;0.75:45;0.00:0;... No names, spaces, reasons, brackets, or extra text.";
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
            sb.AppendLine("Do not return emotionScores in this response. Relationship meaning should be expressed through suggestedStateDeltas, detectedCombos, changesSummary, conversationSummary, memoryNotes, and durable relationship fields only.");
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
            AppendList(sb, "Memory notes", state.MemoryNotes.Take(Clamp(_options.MemoryNoteCount, 1, 300)).ToList());
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
            if (state.MemoryLog.Count == 0)
            {
                sb.AppendLine("No prior turns are recorded for this Star yet.");
            }
            else
            {
                int i = 1;
                foreach (ConversationLogEntry entry in state.MemoryLog.Take(historyCount))
                {
                    sb.AppendLine("Turn " + i + " (" + (i == 1 ? "most recent" : "older") + ")");
                    sb.AppendLine("- Time: " + entry.CreatedAt.ToString("o"));
                    sb.AppendLine("- RequestType: " + entry.RequestType);
                    sb.AppendLine("- Player said: " + Truncate(entry.PlayerText, 360));
                    sb.AppendLine("- " + profile.DisplayName + " replied: " + Truncate(entry.StarResponse, 360));
                    if (!string.IsNullOrWhiteSpace(entry.Summary)) sb.AppendLine("- Turn summary: " + Truncate(entry.Summary, 360));
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
            sb.AppendLine("Return JSON only. Do not wrap it in markdown. spokenText is an array of ordered speech chunks the Star says aloud. Each item should be one or two sentences; do not rely on the runtime to split long paragraphs. For story turns, preserve storyTitle/storyId/storySummary so the engine can remember which stories have been told and avoid repeats. voiceDirective is how Qwen3-TTS should perform the spokenText. conversationSummary must be specific and include what the player said and what the Star said, especially the latest spoken reply, so she knows where she is in a story. changesSummary must say what changed this turn: what the player just said/did, what she said back, how she reacted, and whether memory/relationship/status shifted. importantPlayerQuotes must preserve exact player quotes that emotionally affected her, taught her something, changed romance, unlocked secrets, or should be remembered for later story-building. learnedFunctions is for durable lessons/functions the player taught her. relationshipEvents, girlfriendHistoryUpdates, petNamesPlayerUsesForStar, and petNamesStarUsesForPlayer should be filled only when there is something durable to store. suggestedStateDeltas are the model's recommendation only; the game may compute final deltas deterministically. Do not include emotionScores anywhere in this response.");
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
            sb.AppendLine("If the character is shy, proud, fatalistic, shadowed, or guarded, express that as tone while still giving the facts. Example pattern: 'I do not tell everyone this, but...' followed by real information.");
            sb.AppendLine("It is okay to end with a small in-character question back, but only after answering the player's actual question.");
            sb.AppendLine("Emotional vector scoring guidance: default attraction should be 0.00:0 unless the player earns it. Scores above 80 with confidence above 0.70 should require honest good poetry, real charisma, exciting speech, emotional safety, strong kindness, or a meaningful story/gameplay moment. Irritation, pressure, disrespect, boredom, hurt, or unsafe vibes should be represented in negativeEmotionVector.");
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
            sb.AppendLine("Use this lore to write a complete in-character story with a beginning, escalation, climax, and resolution. You may use secrets, skills, fathers/forms, friends, places, letters, and angel stories when they fit the player's seed. Do not dump the lore as exposition; weave one or two pieces into the story naturally.");
            sb.AppendLine("Game premise: No cat has been seen for one hundred years. The universe is drying up. The Last Cat is an astral conduit who learns mana abilities from the Stars, then learns no-mana astral versions through the soul sword, the Astral Stream, Leviathan, ancient cat letters, and repeated use.");
            sb.AppendLine("Major places: Virethorn Sanctuary and poisoned water puzzles; Upper Caves / Highroot Waterworks; Bone Caves / Marrow Ossuary with Bone Bloom; Astral Caverns and Leviathan Road; Astral Book Room and hell arcade foreshadowing.");
            sb.AppendLine("Benaveil: a young angel behind the Stars. His named forms are spiritually called their father-forms. His unnamed dark force can distort music, thought, and mercy. Greater evils push him toward condemnation, but wise play may save everyone.");
            sb.AppendLine("Stars: Elusiaa-Light/Mercy/Racha; Khavia-Water/Friendship/Chavera; Auerlis-Fire/Joy/Simchi; Maera-Blood/Possession/Maerrow; Kaelia-Void/Fatalism/Kaeliss; Nyxari-Shadow/Fragmentation/Nyxara.");
            AppendExpandedProfileLore(sb, profile);
            sb.AppendLine("Angel stories available to this Star:");
            foreach (StarLoreEntry story in profile.AngelStories)
            {
                sb.AppendLine("- " + story.Title + ": " + story.Summary);
            }
            sb.AppendLine("Story deck available to this Star:");
            foreach (StoryMemoryCard card in profile.StoryDeck)
            {
                sb.AppendLine("- " + card.Id + " [" + card.Kind + "]: " + card.Summary);
            }
            sb.AppendLine("Complete-story checklist: name a specific place or traveler; introduce a desire/problem; include one surprising secret, skill, friend, father-form, letter, or monster; reach a turning point; end with a real resolution or soft cliffhanger that still feels like a complete episode. Before returning JSON, mentally verify the last ## Chunk has actual narration and the story does not stop mid-sentence or mid-scene.");
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
            foreach (StoryMemoryCard card in profile.StoryDeck)
            {
                bool available = card.UnlockedAtBond <= bond || state.SecretsUnlocked.Any(x => MatchesIdOrTitle(x, card.Id, card.Summary)) || storyCanUseUnlockedSecrets;
                sb.AppendLine("- " + card.Id + " | available=" + available + " | unlockBond=" + card.UnlockedAtBond + " | kind=" + card.Kind + " | " + card.Summary);
            }
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
            if (state.MemoryLog.Count > 0)
            {
                ConversationLogEntry mostRecent = state.MemoryLog[0];
                if (!string.IsNullOrWhiteSpace(mostRecent.Summary)) return Truncate(mostRecent.Summary, 240);
                return "player said " + QuoteInline(Truncate(mostRecent.PlayerText, 120)) + " and she answered " + QuoteInline(Truncate(mostRecent.StarResponse, 120)) + ".";
            }

            return string.IsNullOrWhiteSpace(state.ConversationSummary) ? "nothing recorded yet" : Truncate(state.ConversationSummary, 240);
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
