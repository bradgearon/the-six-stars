using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Ai
{
    public sealed class StructuredStarAgent : IStarAgent, IStarTurnAnalyzer, IStarRequestIntentClassifier
    {
        private readonly ILocalChatModelClient _client;
        private readonly StarPromptBuilder _promptBuilder;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly int _speechMaxTokens;
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
            _storyPlanMaxTokens = ResolveStoryPlanMaxTokens();
            _reviewMaxTokens = ResolveReviewMaxTokens(reviewMaxTokens);
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
            _jsonOptions.Converters.Add(new LenientStoryKindJsonConverter());
            _jsonOptions.Converters.Add(new JsonStringEnumConverter());
        }


        public async Task<StarRequestIntentResult> ClassifyRequestIntentAsync(StarProfile profile, StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
        {
            if (request.RequestType == RequestType.Story)
            {
                return new StarRequestIntentResult
                {
                    IsStoryRequest = true,
                    Confidence = 1,
                    StorySeed = request.StoryOptions?.PlayerSuggestion ?? request.PlayerText ?? string.Empty,
                    Reason = "Caller already selected Story request type.",
                    Source = "caller"
                };
            }

            try
            {
                System.Diagnostics.Debug.WriteLine("[StructuredStarAgent] Sending QUICK story intent classifier request...");
                string json = await _client.CompleteJsonAsync(
                    _promptBuilder.BuildStoryIntentMessages(profile, request, state),
                    StarResponseJsonSchema.CreateStoryIntentResponseFormat(),
                    0,
                    "story-intent-classifier",
                    cancellationToken).ConfigureAwait(false);

                StoryIntentModel? model = JsonSerializer.Deserialize<StoryIntentModel>(ExtractJson(json), _jsonOptions);
                if (model == null) throw new InvalidOperationException("Story intent classifier returned empty JSON.");
                return new StarRequestIntentResult
                {
                    IsStoryRequest = model.IsStoryRequest,
                    Confidence = Clamp01(model.Confidence),
                    StorySeed = model.IsStoryRequest ? (model.StorySeed ?? string.Empty).Trim() : string.Empty,
                    Reason = model.Reason ?? string.Empty,
                    Source = "llm"
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                bool fallback = LooksLikeStoryRequest(request.PlayerText);
                return new StarRequestIntentResult
                {
                    IsStoryRequest = fallback,
                    Confidence = fallback ? 0.62 : 0.25,
                    StorySeed = fallback ? (request.PlayerText ?? string.Empty).Trim() : string.Empty,
                    Reason = "Story intent LLM check failed; heuristic fallback used: " + ex.Message,
                    Source = "heuristic-fallback"
                };
            }
        }

        public async Task<StarInteractionResponse> GenerateAsync(StarProfile profile, StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine($"[StructuredStarAgent] Sending FAST spoken response request to LLM for {request.StarId}...");
            System.Diagnostics.Debug.WriteLine($"[StructuredStarAgent] Player text: {request.PlayerText?.Substring(0, Math.Min(60, request.PlayerText?.Length ?? 0))}...");

            bool isStory = request.RequestType == RequestType.Story;
            bool isStoryContinuation = isStory && request.StoryOptions != null && request.StoryOptions.ContinueActiveStory;
            object responseFormat = isStory
                ? StarResponseJsonSchema.CreateStorySpeechResponseFormat(isStoryContinuation)
                : StarResponseJsonSchema.CreateSpeechResponseFormat();
            int maxTokens = isStory && !isStoryContinuation ? _storyPlanMaxTokens : _speechMaxTokens;
            string purpose = isStory
                ? (isStoryContinuation ? "story-continuation-spoken-response" : "story-plan-and-first-spoken-response")
                : "fast-spoken-response";

            string json = await _client.CompleteJsonAsync(
                _promptBuilder.BuildSpeechMessages(profile, request, state),
                responseFormat,
                maxTokens,
                purpose,
                cancellationToken).ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine($"[StructuredStarAgent] Received fast LLM speech response ({json.Length} chars)");

            StarSpeechModelResponse model = ParseSpeech(json, profile, request);
            StarInteractionResponse response = new StarInteractionResponse
            {
                StarId = string.IsNullOrWhiteSpace(model.StarId) ? profile.StarId : model.StarId,
                EmotionalPurpose = "Foreground spoken reply only; background emotion/memory review pending.",
                EmotionalEffectVector = model.EmotionalEffectVector ?? string.Empty,
                NegativeEmotionVector = model.NegativeEmotionVector ?? string.Empty,
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
                Debug = new DebugInfo
                {
                    DetectedIntent = request.RequestType.ToString(),
                    WisdomScore = 0
                }
            };
            response.SetSpokenParts(model.SpokenParts);
            // Do not ask the foreground LLM pass for heavy emotion objects.
            // It returns only the compact emotionalEffectVector; the background review computes impactful emotion scores and deterministic relationship updates.
            response.Debug.Notes.Add("StructuredStarAgent used REAL LLM fast foreground speech pass.");
            response.Debug.Notes.Add("Heavy emotion/memory analysis waits until the voice pipeline reports foreground voice generation ready.");
            response.Debug.Notes.Add("No per-pass max_tokens cap is sent unless explicitly configured; output size is controlled by compact schema and prompts.");
            response.Debug.Notes.Add($"Speech parts: {response.SpokenParts.Count}; joined length: {response.SpokenText.Length} characters.");
            response.Debug.Notes.Add($"Foreground emotionalEffectVector length: {response.EmotionalEffectVector.Length} characters.");
            response.Debug.Notes.Add($"Foreground negativeEmotionVector length: {response.NegativeEmotionVector.Length} characters.");
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
            StarStructuredModelResponse model = Parse(json, profile, request, state);
            NormalizeModelCollections(model);
            if (model.SpokenParts.Count == 0)
            {
                model.SpokenParts.AddRange(foregroundResponse.SpokenParts.Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            StarInteractionResponse response = BuildInteractionResponseFromModel(profile, request, state, model);
            response.EmotionalEffectVector = foregroundResponse.EmotionalEffectVector ?? string.Empty;
            response.NegativeEmotionVector = string.IsNullOrWhiteSpace(model.NegativeEmotionVector) ? (foregroundResponse.NegativeEmotionVector ?? string.Empty) : model.NegativeEmotionVector;
            PreserveForegroundStoryFields(foregroundResponse, response);
            response.Debug.Notes.Add("StructuredStarAgent used REAL LLM background emotion/memory review pass.");
            return response;
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
                VoiceDirective = model.VoiceDirective ?? VoiceDirective.Empty(),
                SuggestedStateDeltas = model.SuggestedStateDeltas ?? RelationshipDelta.Zero(),
                ConversationSummary = string.IsNullOrWhiteSpace(model.ConversationSummary)
                    ? BuildFallbackConversationSummary(state, request, model.SpokenText)
                    : model.ConversationSummary,
                ChangesSummary = string.IsNullOrWhiteSpace(model.ChangesSummary)
                    ? BuildFallbackChangesSummary(request, model.SpokenText)
                    : model.ChangesSummary,
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
            response.ImportantPlayerQuotes.AddRange(model.ImportantPlayerQuotes);
            response.MemoryNotes.AddRange(model.MemoryNotes);
            response.RememberedMarkers.AddRange(model.RememberedMarkers);
            response.ReferencedMemoryMarkerIds.AddRange(model.ReferencedMemoryMarkerIds);
            response.SecretsUnlocked.AddRange(model.SecretsUnlocked);
            response.LearnedFunctions.AddRange(model.LearnedFunctions);
            response.RelationshipEvents.AddRange(model.RelationshipEvents);
            response.GirlfriendHistoryUpdates.AddRange(model.GirlfriendHistoryUpdates);
            response.PetNamesPlayerUsesForStar.AddRange(model.PetNamesPlayerUsesForStar);
            response.PetNamesStarUsesForPlayer.AddRange(model.PetNamesStarUsesForPlayer);
            response.Debug.LoreUsed.AddRange(model.LoreUsed);
            response.Debug.RiskFlags.AddRange(model.RiskFlags);
            TrimEmotionScoresToImpactfulSet(response, 9);
            response.Debug.Notes.Add("StructuredStarAgent used REAL LLM compact background emotion/memory review.");
            response.Debug.Notes.Add($"Impact emotions evaluated: {response.EmotionScores.Count} emotions");
            response.Debug.Notes.Add($"Background kept foreground emotionalEffectVector length: {response.EmotionalEffectVector.Length}; negativeEmotionVector length: {response.NegativeEmotionVector.Length}.");
            response.Debug.Notes.Add($"Remembered markers proposed: {response.RememberedMarkers.Count}; referenced marker ids: {response.ReferencedMemoryMarkerIds.Count}.");
            response.Debug.Notes.Add($"Response length: {response.SpokenText.Length} characters");
            return response;
        }

        private static int ResolveSpeechMaxTokens(int configuredValue)
        {
            string? env = Environment.GetEnvironmentVariable("SIX_STARS_LLM_SPEECH_MAX_TOKENS");
            if (int.TryParse(env, out int value) && value >= 0) return value;
            return configuredValue > 0 ? configuredValue : 0;
        }

        private static int ResolveStoryPlanMaxTokens()
        {
            string? env = Environment.GetEnvironmentVariable("SIX_STARS_LLM_STORY_PLAN_MAX_TOKENS");
            if (int.TryParse(env, out int value) && value >= 0) return value;
            return 0;
        }

        private static int ResolveReviewMaxTokens(int configuredValue)
        {
            string? env = Environment.GetEnvironmentVariable("SIX_STARS_LLM_REVIEW_MAX_TOKENS");
            if (int.TryParse(env, out int value) && value >= 0) return value;
            return configuredValue > 0 ? configuredValue : 0;
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

        private StarSpeechModelResponse ParseSpeech(string rawContent, StarProfile profile, StarInteractionRequest request)
        {
            string json = ExtractJson(rawContent);
            try
            {
                StarSpeechModelResponse? model = JsonSerializer.Deserialize<StarSpeechModelResponse>(json, _jsonOptions);
                if (model == null) throw new InvalidOperationException("Fast speech model response could not be deserialized.");
                ApplySpeechJsonAliases(json, model);
                model.SpokenParts = CleanSpeechParts(model.SpokenParts);
                if (model.SpokenParts.Count == 0)
                {
                    model.SpokenParts.AddRange(ExtractLikelySpokenParts(rawContent));
                }
                if (model.SpokenParts.Count == 0)
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
                model.SpokenParts.AddRange(ExtractLikelySpokenParts(rawContent));
                if (model.SpokenParts.Count == 0)
                {
                    model.SpokenParts.Add(profile.DisplayName + " heard you, little one, but my answer came back in a strange shape. Stay with me and try again.");
                }
                ApplyStorySpeechGuards(model, request);
                return model;
            }
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
            }

            bool continuation = options.ContinueActiveStory;
            if (!continuation && string.IsNullOrWhiteSpace(model.StoryMarkdown) && model.SpokenParts.Count > 0)
            {
                // Some local OpenAI-compatible backends may ignore the storyMarkdown field under
                // response_format pressure. Preserve a valid local-autoplay shape rather than
                // allowing story mode to collapse into an intro or empty story. The prompt still
                // asks for a full story, but this salvage prevents a hard failure.
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
                // A new story always speaks Chunk 0 first. The desktop host now auto-plays
                // Chunk 1+ locally from storyMarkdown without sending continuation LLM requests.
                model.StoryPartIndex = 0;
                if (!string.IsNullOrWhiteSpace(ExtractMarkdownChunk(model.StoryMarkdown, 1)))
                {
                    model.StoryIsFinished = false;
                }
            }
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
            model.LoreUsed ??= new List<string>();
            model.RiskFlags ??= new List<string>();
        }

        private static List<string> CleanSpeechParts(IEnumerable<string>? parts)
        {
            return (parts ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => TruncateForSpeech(x.Replace("\r", " ").Replace("\n", " ").Trim(), 600))
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

        private StarStructuredModelResponse Parse(string rawContent, StarProfile profile, StarInteractionRequest request, StarState state)
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
                return BuildSalvagedModelResponse(rawContent, profile, request, state, ex.Message);
            }
        }

        private static bool LooksLikeStoryRequest(string? text)
        {
            return StoryIntentLexicon.ContainsStoryIntentTrigger(text);
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value)) return 0;
            if (value < 0) return 0;
            if (value > 1) return 1;
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
            if (!string.IsNullOrWhiteSpace(request.PlayerText)) model.ImportantPlayerQuotes.Add(request.PlayerText.Trim());
            model.MemoryNotes.Add("Real LLM raw response was salvaged because structured JSON parsing failed: " + reason);
            model.RiskFlags.Add("llm_structured_json_parse_failed_salvaged");
            model.RiskFlags.Add("llm_raw_response_used_not_mock");
            model.LoreUsed.Add("local-llm-raw-content");
            return model;
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
            }
            catch
            {
            }
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

        private static string TruncateForSpeech(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string trimmed = value.Trim();
            if (trimmed.Length <= maxLength) return trimmed;
            return trimmed.Substring(0, maxLength).Trim() + "...";
        }

        private sealed class StoryIntentModel
        {
            public bool IsStoryRequest { get; set; }
            public double Confidence { get; set; }
            public string? StorySeed { get; set; }
            public string? Reason { get; set; }
        }

        private static void TrimEmotionScoresToImpactfulSet(StarInteractionResponse response, int maxCount)
        {
            if (response.EmotionScores == null || response.EmotionScores.Count <= maxCount)
            {
                return;
            }

            response.EmotionScores = response.EmotionScores
                .Where(x => x.Value != null)
                .OrderByDescending(x => x.Value.Score * Math.Max(0.01, x.Value.Confidence))
                .Take(Math.Max(1, maxCount))
                .ToDictionary(x => x.Key, x => x.Value);
        }
    }
}
