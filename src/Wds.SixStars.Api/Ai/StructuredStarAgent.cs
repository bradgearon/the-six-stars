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
using Wds.SixStars.Api.SpeechIntents;
using Wds.SixStars.Api.Writings;
using Wds.SixStars.Api.XPosting;

namespace Wds.SixStars.Api.Ai
{
    public sealed class StructuredStarAgent : IStarAgent, IStarDecisionScorer, IStarTurnAnalyzer, IStarInformationNeedAnalyzer, IStarDailyStateGenerator, IStarGirlfriendHourlyStateGenerator, IInterstarUpdateAgent, IStarWritingAgent
    {
        private const int WritingPerformanceCandidateLimit = 25;

        private readonly ILocalChatModelClient _client;
        private readonly StarPromptBuilder _promptBuilder;
        private readonly PromptTemplateRenderer _templates;
        private readonly StarWritingValueBaselineCatalog _valueBaselines;
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
            _templates = PromptTemplateRenderer.CreateDefault();
            _valueBaselines = StarWritingValueBaselineCatalog.CreateDefault();
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


        public async Task<StarWritingGenerationResult> GenerateWritingCandidatesAsync(
            StarProfile profile,
            StarWritingNotesState state,
            IReadOnlyList<string> seedWords,
            string writingMethod,
            CancellationToken cancellationToken)
        {
            writingMethod = StarWritingMethods.Normalize(writingMethod);
            if (string.Equals(writingMethod, StarWritingMethods.VerbNounDisplacement, StringComparison.OrdinalIgnoreCase))
            {
                return await GenerateVerbNounDisplacementAsync(profile, state, seedWords, cancellationToken).ConfigureAwait(false);
            }

            string allUsedWordsText = string.Join(", ", (state.AllUsedWords ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => PromptText(x, 120))
                .Distinct(StringComparer.OrdinalIgnoreCase));

            object wordPairData = new
            {
                starId = PromptText(profile.StarId, 80),
                displayName = PromptText(profile.DisplayName, 120),
                element = PromptText(profile.Element, 80),
                domain = PromptText(profile.Domain, 120),
                coreQuote = PromptText(profile.CoreQuote, 500),
                personalityShort = PromptText(profile.PersonalityShort, 900),
                deepPersonality = PromptText(profile.DeepPersonality, 1800),
                characterAttributes = BuildCharacterAttributeSummary(profile),
                behaviors = PromptWordSelectionBehaviorList(profile.Behaviors, 10, 700),
                relevantSecrets = PromptList(
                    (profile.StarSecrets ?? new List<string>())
                        .Concat(profile.LibraryCanonNotes ?? new List<string>()),
                    8,
                    600),
                allUsedWordsText,
                seedWords = NormalizeSeedWords(seedWords),
                recentGeneratedWordSets = (state.RecentGeneratedWordSets ?? new List<StarWritingGeneratedWordSet>())
                    .Where(x => x != null)
                    .OrderByDescending(x => x.GeneratedAt)
                    .Take(10)
                    .Select((set, index) => new
                    {
                        index = index + 1,
                        generatedAt = set.GeneratedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture),
                        pairs = (set.WordPairs ?? new List<List<string>>())
                            .Where(pair => pair != null && pair.Count >= 2)
                            .Take(5)
                            .Select(pair => new
                            {
                                first = PromptText(pair[0], 80),
                                second = PromptText(pair[1], 80)
                            })
                            .ToList()
                    })
                    .ToList(),
                topUsedWords = (state.GeneratedWordUsageCounts ?? new Dictionary<string, int>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Value > 0)
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .Select(x => new
                    {
                        word = PromptText(x.Key, 80),
                        count = x.Value
                    })
                    .ToList()
            };

            IReadOnlyList<ChatMessage> wordPairMessages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("star_writing_word_pairs_system.sbn", wordPairData)),
                new ChatMessage("user", _templates.Render("star_writing_word_pairs_user.sbn", wordPairData))
            };

            string wordPairJson = await _client.CompleteJsonAsync(
                wordPairMessages,
                StarResponseJsonSchema.CreateStarWritingWordPairResponseFormat(),
                0,
                "star-writing-word-pairs",
                cancellationToken).ConfigureAwait(false);
            StarWritingWordPairResult? wordPairResult = JsonSerializer.Deserialize<StarWritingWordPairResult>(ExtractJson(wordPairJson), _jsonOptions);
            if (wordPairResult == null) throw new InvalidOperationException("Writing word-pair selection returned empty JSON.");
            wordPairResult.StarId = string.IsNullOrWhiteSpace(wordPairResult.StarId) ? profile.StarId : wordPairResult.StarId;
            wordPairResult.WordPairs = NormalizeWritingWordPairs(wordPairResult.WordPairs);
            wordPairResult.RawModelJson = wordPairJson;

            List<StarWritingValueReference> sparkValueReferences = BuildSharedReaderEmotionCalibrationReferences(
                StarWritingOutputTypes.Spark,
                5);

            object compositionData = new
            {
                starId = PromptText(profile.StarId, 80),
                displayName = PromptText(profile.DisplayName, 120),
                element = PromptText(profile.Element, 80),
                domain = PromptText(profile.Domain, 120),
                coreQuote = PromptText(profile.CoreQuote, 500),
                personalityShort = PromptText(profile.PersonalityShort, 900),
                deepPersonality = PromptText(profile.DeepPersonality, 1800),
                backstorySummary = PromptText(profile.BackstorySummary, 1400),
                completeBackgroundStory = PromptText(profile.CompleteBackgroundStory, 2200),
                voiceArchetype = PromptText(profile.VoiceArchetype, 700),
                speakingStyle = PromptText(profile.SpeakingStyle, 1200),
                voiceDirection = PromptText(profile.VoiceDirection, 900),
                characterAttributes = BuildCharacterAttributeSummary(profile),
                characterAttributeNotes = PromptList(profile.CharacterAttributeNotes, 8, 700),
                behaviors = PromptWritingBehaviorList(profile.Behaviors, 10, 700),
                storytellingRules = PromptList(profile.StorytellingRules, 8, 700),
                quotes = PromptList(profile.Quotes, 10, 700),
                relevantSecrets = PromptList(
                    (profile.StarSecrets ?? new List<string>())
                        .Concat(profile.LibraryCanonNotes ?? new List<string>()),
                    10,
                    800),
                recentWritings = (state.PersistedWritings ?? new List<PersistedStarWriting>())
                    .Where(x => x != null
                        && string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), StarWritingOutputTypes.Spark, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.PersistedAt)
                    .ThenByDescending(x => x.GeneratedAt)
                    .Take(10)
                    .Select(x => new
                    {
                        generatedAt = x.GeneratedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture),
                        words = string.Join(" + ", (x.WordsUsed ?? new List<string>()).Where(w => !string.IsNullOrWhiteSpace(w)).Take(2)),
                        writing = PromptText(x.Writing, 1200)
                    })
                    .ToList(),
                referenceValue = _valueBaselines.ReferenceValue,
                vectorOrder = StarWritingReaderEmotionVector.CategoryOrder,
                valueReferences = sparkValueReferences,
                wordPairs = wordPairResult.WordPairs
                    .Select((pair, index) => new
                    {
                        index = index + 1,
                        first = PromptText(pair[0], 80),
                        second = PromptText(pair[1], 80)
                    })
                    .ToList()
            };

            IReadOnlyList<ChatMessage> compositionMessages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("star_writing_generate_system.sbn", compositionData)),
                new ChatMessage("user", _templates.Render("star_writing_generate_user.sbn", compositionData))
            };

            string compositionJson = await _client.CompleteJsonAsync(
                compositionMessages,
                StarResponseJsonSchema.CreateStarWritingCompositionResponseFormat(),
                0,
                "star-writing-compose",
                cancellationToken).ConfigureAwait(false);
            StarWritingGenerationResult? result = JsonSerializer.Deserialize<StarWritingGenerationResult>(ExtractJson(compositionJson), _jsonOptions);
            if (result == null) throw new InvalidOperationException("Writing composition returned empty JSON.");
            result.StarId = string.IsNullOrWhiteSpace(result.StarId) ? profile.StarId : result.StarId;
            result.WritingMethod = StarWritingMethods.SemanticCollision;
            result.UsedWords = wordPairResult.WordPairs.Select(pair => pair.ToList()).ToList();
            result.SeedWords = NormalizeSeedWords(seedWords);
            result.RawModelJson = "--- SEMANTIC COLLISION WORD PAIR SELECTION ---" + Environment.NewLine
                + wordPairJson + Environment.NewLine
                + "--- SEMANTIC COLLISION COMPOSITION ---" + Environment.NewLine
                + compositionJson;
            return result;
        }

        private async Task<StarWritingGenerationResult> GenerateVerbNounDisplacementAsync(
            StarProfile profile,
            StarWritingNotesState state,
            IReadOnlyList<string> seedWords,
            CancellationToken cancellationToken)
        {
            List<string> normalizedSeedNouns = NormalizeSeedWords(seedWords);
            object objectActionData = new
            {
                starId = PromptText(profile.StarId, 80),
                displayName = PromptText(profile.DisplayName, 120),
                element = PromptText(profile.Element, 80),
                domain = PromptText(profile.Domain, 120),
                coreQuote = PromptText(profile.CoreQuote, 500),
                personalityShort = PromptText(profile.PersonalityShort, 900),
                deepPersonality = PromptText(profile.DeepPersonality, 1800),
                characterAttributes = BuildCharacterAttributeSummary(profile),
                behaviors = PromptWordSelectionBehaviorList(profile.Behaviors, 10, 700),
                relevantSecrets = PromptList(
                    (profile.StarSecrets ?? new List<string>())
                        .Concat(profile.LibraryCanonNotes ?? new List<string>()),
                    8,
                    600),
                seedNouns = normalizedSeedNouns,
                recentGeneratedNouns = (state.RecentGeneratedNouns ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => PromptText(x, 80))
                    .Take(50)
                    .ToList(),
                topUsedNouns = (state.GeneratedNounUsageCounts ?? new Dictionary<string, int>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Value > 0)
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(100)
                    .Select(x => new
                    {
                        noun = PromptText(x.Key, 80),
                        count = x.Value
                    })
                    .ToList()
            };

            List<ChatMessage> objectActionMessages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("star_writing_displacement_objects_system.sbn", objectActionData)),
                new ChatMessage("user", _templates.Render("star_writing_displacement_objects_user.sbn", objectActionData))
            };

            string objectActionJson = string.Empty;
            StarWritingObjectActionResult? objectActionResult = null;
            Exception? objectActionError = null;
            for (int attempt = 0; attempt < 2 && objectActionResult == null; attempt++)
            {
                List<ChatMessage> attemptMessages = objectActionMessages.ToList();
                if (attempt > 0)
                {
                    attemptMessages.Add(new ChatMessage(
                        "user",
                        "Regenerate the entire JSON set. The previous response violated the uniqueness contract. Use five distinct nouns and fifteen globally distinct ordinary-action verbs/verb phrases; no normal action may repeat anywhere in the five objects."));
                }

                objectActionJson = await _client.CompleteJsonAsync(
                    attemptMessages,
                    StarResponseJsonSchema.CreateStarWritingObjectActionResponseFormat(),
                    0,
                    "star-writing-displacement-objects" + (attempt == 0 ? string.Empty : "-retry"),
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    StarWritingObjectActionResult? parsed = JsonSerializer.Deserialize<StarWritingObjectActionResult>(ExtractJson(objectActionJson), _jsonOptions);
                    if (parsed == null) throw new InvalidOperationException("Verb/noun displacement object selection returned empty JSON.");
                    parsed.StarId = string.IsNullOrWhiteSpace(parsed.StarId) ? profile.StarId : parsed.StarId;
                    parsed.Objects = NormalizeObjectActionItems(parsed.Objects);
                    parsed.RawModelJson = objectActionJson;
                    objectActionResult = parsed;
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
                {
                    objectActionError = ex;
                }
            }
            if (objectActionResult == null)
            {
                throw new InvalidOperationException(
                    "Verb/noun displacement object selection could not produce five nouns with fifteen unique ordinary actions after one retry.",
                    objectActionError);
            }

            List<StarWritingValueReference> sparkValueReferences = BuildSharedReaderEmotionCalibrationReferences(
                StarWritingOutputTypes.Spark,
                5);

            object compositionData = new
            {
                starId = PromptText(profile.StarId, 80),
                displayName = PromptText(profile.DisplayName, 120),
                element = PromptText(profile.Element, 80),
                domain = PromptText(profile.Domain, 120),
                coreQuote = PromptText(profile.CoreQuote, 500),
                personalityShort = PromptText(profile.PersonalityShort, 900),
                deepPersonality = PromptText(profile.DeepPersonality, 1800),
                backstorySummary = PromptText(profile.BackstorySummary, 1400),
                completeBackgroundStory = PromptText(profile.CompleteBackgroundStory, 2200),
                voiceArchetype = PromptText(profile.VoiceArchetype, 700),
                speakingStyle = PromptText(profile.SpeakingStyle, 1200),
                voiceDirection = PromptText(profile.VoiceDirection, 900),
                characterAttributes = BuildCharacterAttributeSummary(profile),
                characterAttributeNotes = PromptList(profile.CharacterAttributeNotes, 8, 700),
                behaviors = PromptWritingBehaviorList(profile.Behaviors, 10, 700),
                storytellingRules = PromptList(profile.StorytellingRules, 8, 700),
                quotes = PromptList(profile.Quotes, 10, 700),
                relevantSecrets = PromptList(
                    (profile.StarSecrets ?? new List<string>())
                        .Concat(profile.LibraryCanonNotes ?? new List<string>()),
                    10,
                    800),
                recentWritings = (state.PersistedWritings ?? new List<PersistedStarWriting>())
                    .Where(x => x != null
                        && string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), StarWritingOutputTypes.Spark, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.PersistedAt)
                    .ThenByDescending(x => x.GeneratedAt)
                    .Take(10)
                    .Select(x => new
                    {
                        generatedAt = x.GeneratedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture),
                        words = string.Join(" + ", (x.WordsUsed ?? new List<string>()).Where(w => !string.IsNullOrWhiteSpace(w)).Take(2)),
                        writing = PromptText(x.Writing, 1200)
                    })
                    .ToList(),
                referenceValue = _valueBaselines.ReferenceValue,
                vectorOrder = StarWritingReaderEmotionVector.CategoryOrder,
                valueReferences = sparkValueReferences,
                objects = objectActionResult.Objects
                    .Select((item, index) => new
                    {
                        index = index + 1,
                        @object = PromptText(item.Object, 80),
                        normalActions = (item.NormalActions ?? new List<string>()).Select(x => PromptText(x, 80)).ToList()
                    })
                    .ToList()
            };

            List<ChatMessage> compositionMessages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("star_writing_displacement_generate_system.sbn", compositionData)),
                new ChatMessage("user", _templates.Render("star_writing_displacement_generate_user.sbn", compositionData))
            };

            string compositionJson = string.Empty;
            StarWritingDisplacementCompositionResult? composition = null;
            Exception? compositionError = null;
            for (int attempt = 0; attempt < 2 && composition == null; attempt++)
            {
                List<ChatMessage> attemptMessages = compositionMessages.ToList();
                if (attempt > 0)
                {
                    attemptMessages.Add(new ChatMessage(
                        "user",
                        "Regenerate all five items. The previous response reused a verb. Every displacedVerb must be unique across the five writings and must not duplicate any of the fifteen supplied normal-action verbs."));
                }

                compositionJson = await _client.CompleteJsonAsync(
                    attemptMessages,
                    StarResponseJsonSchema.CreateStarWritingDisplacementCompositionResponseFormat(),
                    0,
                    "star-writing-displacement-compose" + (attempt == 0 ? string.Empty : "-retry"),
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    StarWritingDisplacementCompositionResult? parsed = JsonSerializer.Deserialize<StarWritingDisplacementCompositionResult>(ExtractJson(compositionJson), _jsonOptions);
                    if (parsed == null) throw new InvalidOperationException("Verb/noun displacement composition returned empty JSON.");
                    parsed.StarId = string.IsNullOrWhiteSpace(parsed.StarId) ? profile.StarId : parsed.StarId;
                    parsed.Items = NormalizeDisplacementCompositionItems(parsed.Items, objectActionResult.Objects);
                    parsed.RawModelJson = compositionJson;
                    composition = parsed;
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
                {
                    compositionError = ex;
                }
            }
            if (composition == null)
            {
                throw new InvalidOperationException(
                    "Verb/noun displacement composition could not produce five unique displaced verbs after one retry.",
                    compositionError);
            }

            return new StarWritingGenerationResult
            {
                StarId = profile.StarId,
                WritingMethod = StarWritingMethods.VerbNounDisplacement,
                Jokes = composition.Items.Select(x => x.Writing).ToList(),
                UsedWords = composition.Items.Select(x => new List<string> { x.Noun, x.DisplacedVerb }).ToList(),
                SeedWords = normalizedSeedNouns,
                RawModelJson = "--- VERB/NOUN DISPLACEMENT OBJECTS ---" + Environment.NewLine
                    + objectActionJson + Environment.NewLine
                    + "--- VERB/NOUN DISPLACEMENT COMPOSITION ---" + Environment.NewLine
                    + compositionJson
            };
        }

        public async Task<StarWritingImprovementResult> ImproveWritingCandidatesAsync(
            StarProfile profile,
            StarWritingNotesState state,
            StarWritingGenerationResult generation,
            int passNumber,
            int totalPasses,
            CancellationToken cancellationToken)
        {
            string allUsedWordsText = string.Join(", ", (state.AllUsedWords ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => PromptText(x, 120))
                .Distinct(StringComparer.OrdinalIgnoreCase));

            object data = new
            {
                starId = PromptText(profile.StarId, 80),
                displayName = PromptText(profile.DisplayName, 120),
                element = PromptText(profile.Element, 80),
                domain = PromptText(profile.Domain, 120),
                coreQuote = PromptText(profile.CoreQuote, 500),
                personalityShort = PromptText(profile.PersonalityShort, 900),
                deepPersonality = PromptText(profile.DeepPersonality, 1800),
                speakingStyle = PromptText(profile.SpeakingStyle, 1200),
                voiceDirection = PromptText(profile.VoiceDirection, 900),
                characterAttributes = BuildCharacterAttributeSummary(profile),
                behaviors = PromptWritingBehaviorList(profile.Behaviors, 10, 700),
                relevantSecrets = PromptList(
                    (profile.StarSecrets ?? new List<string>())
                        .Concat(profile.LibraryCanonNotes ?? new List<string>()),
                    10,
                    800),
                allUsedWordsText,
                seedWords = NormalizeSeedWords(generation.SeedWords),
                passNumber = Math.Max(2, passNumber),
                totalPasses = Math.Max(Math.Max(2, passNumber), totalPasses),
                referenceValue = _valueBaselines.ReferenceValue,
                vectorOrder = StarWritingReaderEmotionVector.CategoryOrder,
                valueReferences = BuildSharedReaderEmotionCalibrationReferences(StarWritingOutputTypes.Spark, 5),
                candidates = (generation.Items ?? new List<StarWritingCandidate>())
                    .Take(5)
                    .Select(item => new
                    {
                        id = PromptText(item.Id, 80),
                        words = string.Join(" + ", (item.WordsUsed ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(2)),
                        writing = PromptText(item.Writing, 1600)
                    })
                    .ToList()
            };

            bool displacementMethod = string.Equals(
                StarWritingMethods.Normalize(generation.WritingMethod),
                StarWritingMethods.VerbNounDisplacement,
                StringComparison.OrdinalIgnoreCase);
            string improveSystemTemplate = displacementMethod
                ? "star_writing_displacement_improve_system.sbn"
                : "star_writing_improve_system.sbn";
            string improveUserTemplate = displacementMethod
                ? "star_writing_displacement_improve_user.sbn"
                : "star_writing_improve_user.sbn";

            IReadOnlyList<ChatMessage> messages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render(improveSystemTemplate, data)),
                new ChatMessage("user", _templates.Render(improveUserTemplate, data))
            };

            string improvementRequestName = (displacementMethod
                ? "star-writing-displacement-improve-pass-"
                : "star-writing-improve-pass-")
                + passNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string json = await _client.CompleteJsonAsync(
                messages,
                StarResponseJsonSchema.CreateStarWritingImprovementResponseFormat(),
                0,
                improvementRequestName,
                cancellationToken).ConfigureAwait(false);

            List<StarWritingImprovementReplacement>? replacements;
            try
            {
                replacements = JsonSerializer.Deserialize<List<StarWritingImprovementReplacement>>(ExtractJsonArray(json), _jsonOptions);
            }
            catch (JsonException)
            {
                // A malformed optional improvement pass must not discard the valid working set.
                // Treat it as no replacements and allow later passes/final scoring to continue.
                replacements = new List<StarWritingImprovementReplacement>();
            }

            return new StarWritingImprovementResult
            {
                Replacements = replacements ?? new List<StarWritingImprovementReplacement>(),
                RawModelJson = json
            };
        }

        public async Task<StarWritingReviewResult> RateWritingCandidatesAsync(
            StarProfile profile,
            StarWritingNotesState state,
            StarWritingGenerationResult generation,
            CancellationToken cancellationToken)
        {
            object data = new
            {
                starId = PromptText(profile.StarId, 80),
                displayName = PromptText(profile.DisplayName, 120),
                element = PromptText(profile.Element, 80),
                domain = PromptText(profile.Domain, 120),
                coreQuote = PromptText(profile.CoreQuote, 500),
                personalityShort = PromptText(profile.PersonalityShort, 900),
                deepPersonality = PromptText(profile.DeepPersonality, 1800),
                speakingStyle = PromptText(profile.SpeakingStyle, 1200),
                behaviors = PromptWritingBehaviorList(profile.Behaviors, 10, 700),
                reviewDiscipline = StarWritingReviewDisciplines.ReaderEmotionIuV1,
                vectorOrder = StarWritingReaderEmotionVector.CategoryOrder,
                vectorCount = StarWritingReaderEmotionVector.Categories.Length,
                emotionBuckets = StarWritingStoryEmotionFeedback.Buckets.Select(x => new { emotion = x.Emotion, discipline = x.Discipline }).ToList(),
                referenceValue = _valueBaselines.ReferenceValue,
                valueReferences = BuildSharedReaderEmotionCalibrationReferences(StarWritingOutputTypes.Spark, 5),
                candidates = (generation.Items ?? new List<StarWritingCandidate>())
                    .Take(5)
                    .Select((item, index) => new
                    {
                        index,
                        words = string.Join(" + ", (item.WordsUsed ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(2)),
                        writing = PromptText(item.Writing, 1600)
                    })
                    .ToList()
            };

            IReadOnlyList<ChatMessage> messages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("star_writing_review_system.sbn", data)),
                new ChatMessage("user", _templates.Render("star_writing_review_user.sbn", data))
            };

            string json = await _client.CompleteJsonAsync(
                messages,
                StarResponseJsonSchema.CreateStarWritingReviewResponseFormat(),
                0,
                "star-writing-rate",
                cancellationToken).ConfigureAwait(false);
            StarWritingReviewResult result = ParseWritingReviewResult(json, profile.StarId);

            if (HasCompleteWritingReview(result) && HasSuspiciousCollapsedWritingReview(result))
            {
                List<ChatMessage> calibrationRepairMessages = messages.ToList();
                calibrationRepairMessages.Add(new ChatMessage(
                    "user",
                    "CALIBRATION RE-REVIEW: The previous response collapsed different writings into the same reader-emotion vector. Read each candidate independently. The author identity must not affect magnitude; the same text under another actor must receive the same values. Re-evaluate all five from the shared IU scale and return only the compact JSON contract."));
                string calibrationRepairJson = await _client.CompleteJsonAsync(
                    calibrationRepairMessages,
                    StarResponseJsonSchema.CreateStarWritingReviewResponseFormat(),
                    0,
                    "star-writing-rate-calibration-repair",
                    cancellationToken).ConfigureAwait(false);
                StarWritingReviewResult calibrationRepaired = ParseWritingReviewResult(calibrationRepairJson, profile.StarId);
                calibrationRepaired.RawModelJson = json + Environment.NewLine
                    + "--- CALIBRATION COLLAPSE RE-REVIEW ---" + Environment.NewLine
                    + calibrationRepairJson;
                // Prefer the independent rereview even if one vector is malformed. The targeted
                // single-item repair below can fix that one vector without falling back to the
                // suspiciously collapsed original batch.
                result = calibrationRepaired;
            }

            if (!HasCompleteWritingReview(result))
            {
                // Do not throw away four good vectors because one local-model string was off by one.
                // A five-vector repair asks the model to count 155 positional values again and can turn
                // one malformed item into several malformed items. Preserve every valid exact-index
                // rating and rerate only the missing/malformed candidate, one 31-value vector at a time.
                result = await RepairIncompleteWritingReviewAsync(
                    profile,
                    generation,
                    result,
                    json,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!HasCompleteWritingReview(result))
            {
                string missingIndexes = string.Join(",", GetIncompleteWritingReviewIndexes(result));
                throw new InvalidOperationException(
                    "Writing review still had malformed 31-entry reader-emotion IU vectors after single-item repair" +
                    (missingIndexes.Length > 0 ? " at candidate index(es) " + missingIndexes : string.Empty) +
                    ". No new writing batch was saved; try the generation again.");
            }

            foreach (StarWritingRating rating in result.Ratings.Where(x => x != null))
            {
                if (StarWritingReaderEmotionVector.TryNormalizeComplete(
                        rating.EmotionalVector,
                        out string normalizedVector,
                        out Dictionary<string, StarWritingEmotionValue> values))
                {
                    rating.ReviewDiscipline = StarWritingReviewDisciplines.ReaderEmotionIuV1;
                    rating.EmotionalVector = normalizedVector;
                    rating.OverallValue = StarWritingReaderEmotionVector.ReadValue(values, "Overall");
                    rating.EmotionFeedback = new List<StarWritingStoryEmotionValue>();
                }
            }

            result.StarId = string.IsNullOrWhiteSpace(result.StarId) ? profile.StarId : result.StarId;
            if (string.IsNullOrWhiteSpace(result.RawModelJson)) result.RawModelJson = json;
            return result;
        }

        private async Task<StarWritingReviewResult> RepairIncompleteWritingReviewAsync(
            StarProfile profile,
            StarWritingGenerationResult generation,
            StarWritingReviewResult original,
            string originalRawJson,
            CancellationToken cancellationToken)
        {
            Dictionary<int, StarWritingRating> validByIndex = GetValidWritingRatingsByIndex(original);
            List<string> rawParts = new List<string>();
            string initialRaw = string.IsNullOrWhiteSpace(original.RawModelJson) ? originalRawJson : original.RawModelJson;
            if (!string.IsNullOrWhiteSpace(initialRaw)) rawParts.Add(initialRaw);

            List<StarWritingCandidate> candidates = (generation.Items ?? new List<StarWritingCandidate>()).Take(5).ToList();
            List<StarWritingValueReference> references = BuildSharedReaderEmotionCalibrationReferences(StarWritingOutputTypes.Spark, 5);

            foreach (int index in Enumerable.Range(0, 5).Where(i => !validByIndex.ContainsKey(i)).ToList())
            {
                if (index >= candidates.Count) continue;

                StarWritingCandidate candidate = candidates[index];
                StarWritingRating? repaired = null;
                for (int attempt = 1; attempt <= 2 && repaired == null; attempt++)
                {
                    IReadOnlyList<ChatMessage> repairMessages = BuildSingleWritingReviewRepairMessages(
                        candidate,
                        index,
                        references,
                        attempt);
                    string label = attempt == 1
                        ? $"star-writing-rate-repair-item-{index}"
                        : $"star-writing-rate-repair-item-{index}-retry";
                    string repairJson = await _client.CompleteJsonAsync(
                        repairMessages,
                        StarResponseJsonSchema.CreateStarWritingSingleReviewRepairResponseFormat(),
                        0,
                        label,
                        cancellationToken).ConfigureAwait(false);

                    rawParts.Add($"--- SINGLE ITEM RATING FORMAT REPAIR index={index} attempt={attempt} ---");
                    rawParts.Add(repairJson);
                    repaired = ParseSingleWritingReviewRepair(repairJson, index);
                }

                if (repaired != null)
                {
                    validByIndex[index] = repaired;
                }
            }

            return new StarWritingReviewResult
            {
                StarId = string.IsNullOrWhiteSpace(original.StarId) ? profile.StarId : original.StarId,
                Ratings = validByIndex.OrderBy(x => x.Key).Select(x => x.Value).ToList(),
                RawModelJson = string.Join(Environment.NewLine, rawParts)
            };
        }

        private IReadOnlyList<ChatMessage> BuildSingleWritingReviewRepairMessages(
            StarWritingCandidate candidate,
            int index,
            IReadOnlyList<StarWritingValueReference> references,
            int attempt)
        {
            string referenceText = string.Join(
                Environment.NewLine,
                (references ?? Array.Empty<StarWritingValueReference>())
                    .Take(5)
                    .Select(item =>
                        $"Overall={item.OverallValue:0.##} IU | Vector={item.ValueVector} | Writing={PromptText(item.Writing, 500)}"));

            string bucketText = string.Join(
                Environment.NewLine,
                StarWritingStoryEmotionFeedback.Buckets.Select(bucket => $"- {bucket.Emotion}: {bucket.Discipline}"));
            string zeroVectorExample = string.Join(";", Enumerable.Repeat("0", StarWritingReaderEmotionVector.Categories.Length));

            string system =
                "Act as a neutral reader-response evaluator repairing ONE malformed Spark review. " +
                "Rate only the supplied writing. Use the same shared IU ruler as the normal review. " +
                "Return exactly one compact JSON property named valueVector. Its string must contain exactly 31 " +
                "non-negative integer IU values separated by exactly 30 semicolons, with no spaces, names, colons, " +
                "brackets, commentary, or markdown. Overall is the 31st and final value. Do not copy a generic shape. " +
                "IU is open-ended: 0 absent, 5 trace, 15 faint, 30 noticeable, 50 strong, 70 very strong, " +
                "85 exceptional, 100 reference excellence, 125 rare extraordinary, 150+ landmark force.";

            string user =
                "REPAIR ONE SPARK REVIEW" + Environment.NewLine +
                $"Candidate index: {index}" + Environment.NewLine +
                $"Repair attempt: {attempt}" + Environment.NewLine + Environment.NewLine +
                "EXACT ORDER (31)" + Environment.NewLine +
                StarWritingReaderEmotionVector.CategoryOrder + Environment.NewLine +
                "Overall is LAST." + Environment.NewLine + Environment.NewLine +
                "BUCKET MEANINGS" + Environment.NewLine +
                bucketText + Environment.NewLine + Environment.NewLine +
                "WRITING" + Environment.NewLine +
                $"Words: {string.Join(" + ", (candidate.WordsUsed ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(2))}" + Environment.NewLine +
                PromptText(candidate.Writing, 1600) + Environment.NewLine + Environment.NewLine +
                "SHARED CALIBRATION REFERENCES" + Environment.NewLine +
                (referenceText.Length == 0 ? $"Reference excellence: {_valueBaselines.ReferenceValue:0.##} IU." : referenceText) + Environment.NewLine + Environment.NewLine +
                "Count the 31 positions before returning. Return only one JSON object in this shape:" + Environment.NewLine +
                "{\"valueVector\":\"" + zeroVectorExample + "\"}" + Environment.NewLine +
                "Replace the zeros with your 31 IU judgments; keep exactly 30 semicolons.";

            return new List<ChatMessage>
            {
                new ChatMessage("system", system),
                new ChatMessage("user", user)
            };
        }

        private static StarWritingRating? ParseSingleWritingReviewRepair(string rawContent, int index)
        {
            string json = ExtractJson(rawContent);
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !TryGetPropertyIgnoreCase(root, "valueVector", out JsonElement vectorElement))
                {
                    return null;
                }

                string vector = ReadWritingVector(vectorElement);
                if (!StarWritingReaderEmotionVector.TryNormalizeComplete(
                        vector,
                        out string normalizedVector,
                        out Dictionary<string, StarWritingEmotionValue> values))
                {
                    return null;
                }

                return new StarWritingRating
                {
                    Index = index,
                    ReviewDiscipline = StarWritingReviewDisciplines.ReaderEmotionIuV1,
                    EmotionalVector = normalizedVector,
                    EmotionFeedback = new List<StarWritingStoryEmotionValue>(),
                    OverallValue = StarWritingReaderEmotionVector.ReadValue(values, "Overall")
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static Dictionary<int, StarWritingRating> GetValidWritingRatingsByIndex(StarWritingReviewResult result)
        {
            Dictionary<int, StarWritingRating> valid = new Dictionary<int, StarWritingRating>();
            foreach (StarWritingRating rating in result?.Ratings ?? new List<StarWritingRating>())
            {
                if (rating == null || rating.Index < 0 || rating.Index > 4 || valid.ContainsKey(rating.Index)) continue;
                if (!StarWritingReaderEmotionVector.TryNormalizeComplete(
                        rating.EmotionalVector,
                        out string normalizedVector,
                        out Dictionary<string, StarWritingEmotionValue> values))
                {
                    continue;
                }

                rating.ReviewDiscipline = StarWritingReviewDisciplines.ReaderEmotionIuV1;
                rating.EmotionalVector = normalizedVector;
                rating.EmotionFeedback = new List<StarWritingStoryEmotionValue>();
                rating.OverallValue = StarWritingReaderEmotionVector.ReadValue(values, "Overall");
                valid[rating.Index] = rating;
            }

            return valid;
        }

        private static List<int> GetIncompleteWritingReviewIndexes(StarWritingReviewResult result)
        {
            Dictionary<int, StarWritingRating> valid = GetValidWritingRatingsByIndex(result);
            return Enumerable.Range(0, 5).Where(index => !valid.ContainsKey(index)).ToList();
        }

        public async Task<StarWritingStoryTermContributionResult> ContributeStoryTermsAsync(
            StarProfile profile,
            StarWritingNotesState viewpointState,
            IReadOnlyList<string> nounSeeds,
            IReadOnlyList<string> verbSeeds,
            IReadOnlyList<string> subjectsSoFar,
            IReadOnlyList<string> verbsSoFar,
            IReadOnlyList<StarWritingStoryWish> storyWishesSoFar,
            int phaseNumber,
            CancellationToken cancellationToken)
        {
            object data = new
            {
                starId = PromptText(profile.StarId, 80),
                displayName = PromptText(profile.DisplayName, 120),
                element = PromptText(profile.Element, 80),
                domain = PromptText(profile.Domain, 120),
                coreQuote = PromptText(profile.CoreQuote, 500),
                personalityShort = PromptText(profile.PersonalityShort, 900),
                deepPersonality = PromptText(profile.DeepPersonality, 1800),
                actionBattlePreference = PromptText(profile.ActionBattlePreference, 700),
                characterAttributes = BuildCharacterAttributeSummary(profile),
                behaviors = PromptWordSelectionBehaviorList(profile.Behaviors, 10, 700),
                relevantSecrets = PromptList(
                    (profile.StarSecrets ?? new List<string>()).Concat(profile.LibraryCanonNotes ?? new List<string>()),
                    8,
                    600),
                phaseNumber = Math.Max(1, phaseNumber),
                nounSeeds = NormalizeSeedWords(nounSeeds),
                verbSeeds = NormalizeSeedWords(verbSeeds),
                subjectsSoFar = NormalizeStoryTerms(subjectsSoFar, 40),
                verbsSoFar = NormalizeStoryTerms(verbsSoFar, 40),
                storyWishesSoFar = FormatStoryWishes(storyWishesSoFar),
                recentStoryNouns = NormalizeStoryTerms(viewpointState.RecentStoryNouns, 50),
                recentStoryVerbs = NormalizeStoryTerms(viewpointState.RecentStoryVerbs, 50),
                topStoryNouns = BuildTopStoryTermCounts(viewpointState.StoryNounUsageCounts),
                topStoryVerbs = BuildTopStoryTermCounts(viewpointState.StoryVerbUsageCounts)
            };

            IReadOnlyList<ChatMessage> messages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("star_writing_story_terms_system.sbn", data)),
                new ChatMessage("user", _templates.Render("star_writing_story_terms_user.sbn", data))
            };
            string json = await _client.CompleteJsonAsync(
                messages,
                StarResponseJsonSchema.CreateStarWritingStoryTermContributionResponseFormat(),
                0,
                "star-writing-story-terms-" + PromptText(profile.StarId, 40).ToLowerInvariant(),
                cancellationToken).ConfigureAwait(false);
            StarWritingStoryTermContributionResult? result = JsonSerializer.Deserialize<StarWritingStoryTermContributionResult>(ExtractJson(json), _jsonOptions);
            if (result == null) throw new InvalidOperationException("Constellation Forge term contribution returned empty JSON.");
            result.StarId = string.IsNullOrWhiteSpace(result.StarId) ? profile.StarId : result.StarId.Trim();
            HashSet<string> usedSubjects = new HashSet<string>(subjectsSoFar ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            usedSubjects.UnionWith(viewpointState.RecentStoryNouns ?? new List<string>());
            HashSet<string> usedVerbs = new HashSet<string>(verbsSoFar ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            usedVerbs.UnionWith(viewpointState.RecentStoryVerbs ?? new List<string>());
            result.Subjects = NormalizeStoryTermsUnbounded(result.Subjects).Where(x => !usedSubjects.Contains(x)).ToList();
            result.Verbs = NormalizeStoryTermsUnbounded(result.Verbs).Where(x => !usedVerbs.Contains(x)).ToList();
            result.StoryWishes = NormalizeStoryTermsUnbounded(result.StoryWishes).ToList();
            if (result.StoryWishes.Count != 5)
            {
                throw new InvalidOperationException(
                    "Constellation Forge story-wish contribution from " + profile.StarId
                    + " returned " + result.StoryWishes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " usable wishes; exactly five are required so every active Star gets the same creative voice in the seed pass. Raw response preview: "
                    + PromptText(json, 1400));
            }
            // Subject/verb counts remain instruction-guided rather than hard-gated; the active roster naturally accumulates them.
            result.RawModelJson = json;
            return result;
        }

        public async Task<StarWritingStoryOutlineResult> CreateStoryOutlineAsync(
            StarProfile viewpointProfile,
            StarWritingNotesState state,
            IReadOnlyList<string> subjects,
            IReadOnlyList<string> verbs,
            IReadOnlyList<StarWritingStoryWish> storyWishes,
            IReadOnlyList<string> seedWords,
            CancellationToken cancellationToken)
        {
            object data = new
            {
                starId = PromptText(viewpointProfile.StarId, 80),
                displayName = PromptText(viewpointProfile.DisplayName, 120),
                element = PromptText(viewpointProfile.Element, 80),
                domain = PromptText(viewpointProfile.Domain, 120),
                coreQuote = PromptText(viewpointProfile.CoreQuote, 500),
                personalityShort = PromptText(viewpointProfile.PersonalityShort, 900),
                deepPersonality = PromptText(viewpointProfile.DeepPersonality, 1800),
                actionBattlePreference = PromptText(viewpointProfile.ActionBattlePreference, 700),
                backstorySummary = PromptText(viewpointProfile.BackstorySummary, 1400),
                completeBackgroundStory = PromptText(viewpointProfile.CompleteBackgroundStory, 2200),
                speakingStyle = PromptText(viewpointProfile.SpeakingStyle, 1200),
                characterAttributes = BuildCharacterAttributeSummary(viewpointProfile),
                behaviors = PromptWritingBehaviorList(viewpointProfile.Behaviors, 10, 700),
                storytellingRules = PromptList(viewpointProfile.StorytellingRules, 10, 700),
                subjects = NormalizeStoryTerms(subjects, 40),
                verbs = NormalizeStoryTerms(verbs, 40),
                storyWishes = FormatStoryWishes(storyWishes),
                seedWords = NormalizeSeedWords(seedWords),
                referenceValue = _valueBaselines.ReferenceValue,
                vectorOrder = StarWritingReaderEmotionVector.CategoryOrder,
                valueReferences = BuildSharedReaderEmotionCalibrationReferences(StarWritingOutputTypes.ConstellationStory, 3)
            };
            IReadOnlyList<ChatMessage> messages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("star_writing_story_outline_system.sbn", data)),
                new ChatMessage("user", _templates.Render("star_writing_story_outline_user.sbn", data))
            };
            string json = await _client.CompleteJsonAsync(
                messages,
                StarResponseJsonSchema.CreateStarWritingStoryOutlineResponseFormat(),
                0,
                "star-writing-story-outline",
                cancellationToken).ConfigureAwait(false);
            StarWritingStoryOutlineResult? result = ParseAndNormalizeStoryOutline(json, viewpointProfile.StarId);
            if (result == null)
            {
                throw new InvalidOperationException("Constellation Forge outline returned empty or unreadable JSON.");
            }

            List<string> missing = GetMissingStoryOutlineFields(result);
            if (missing.Count > 0)
            {
                List<ChatMessage> completionMessages = messages.ToList();
                completionMessages.Add(new ChatMessage(
                    "assistant",
                    ExtractJson(json)));
                completionMessages.Add(new ChatMessage(
                    "user",
                    "The outline is structurally incomplete because these required story components are empty: "
                    + string.Join(", ", missing)
                    + ". Complete this SAME outline now. Preserve every useful existing field, fill the missing components, and return the full JSON object again. "
                    + "The characters, storySubjects, and beats properties must be JSON arrays of plain strings. Characters should be named, memorable story characters; storySubjects should describe what the story is actually about. "
                    + "Each beat string may be as detailed as needed. Do not put objects inside these arrays. "
                    + "Do not shorten the story architecture to satisfy a count or size target; let the story determine its structure."));

                string completionJson = await _client.CompleteJsonAsync(
                    completionMessages,
                    StarResponseJsonSchema.CreateStarWritingStoryOutlineResponseFormat(),
                    0,
                    "star-writing-story-outline-completion",
                    cancellationToken).ConfigureAwait(false);
                StarWritingStoryOutlineResult? completed = ParseAndNormalizeStoryOutline(completionJson, viewpointProfile.StarId);
                if (completed != null)
                {
                    MergeStoryOutline(result, completed);
                    result.RawModelJson = json + Environment.NewLine + "--- OUTLINE COMPLETION ---" + Environment.NewLine + completionJson;
                }
                missing = GetMissingStoryOutlineFields(result);
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "Constellation Forge outline remained incomplete after asking the viewpoint Star to complete the same outline. Missing: "
                    + string.Join(", ", missing)
                    + ". This is a generation/instruction failure, not a length-limit rejection. Raw outline response preview: "
                    + PromptText(result.RawModelJson, 1600));
            }

            if (string.IsNullOrWhiteSpace(result.RawModelJson)) result.RawModelJson = json;
            return result;
        }

        public async Task<StarWritingStoryBuildResult> BuildStoryPhaseAsync(
            StarProfile contributorProfile,
            StarProfile viewpointProfile,
            StarWritingNotesState viewpointState,
            StarWritingStoryOutlineResult outline,
            string currentStory,
            IReadOnlyList<string> subjects,
            IReadOnlyList<string> verbs,
            IReadOnlyList<StarWritingStoryWish> storyWishes,
            IReadOnlyList<string> seedWords,
            IReadOnlyList<string> priorContributorStarIds,
            IReadOnlyList<string> eligibleContributorStarIds,
            int phaseNumber,
            bool pendingFinish,
            string finishRequestedByStarId,
            CancellationToken cancellationToken)
        {
            object data = new
            {
                starId = PromptText(contributorProfile.StarId, 80),
                displayName = PromptText(contributorProfile.DisplayName, 120),
                element = PromptText(contributorProfile.Element, 80),
                domain = PromptText(contributorProfile.Domain, 120),
                coreQuote = PromptText(contributorProfile.CoreQuote, 500),
                personalityShort = PromptText(contributorProfile.PersonalityShort, 900),
                deepPersonality = PromptText(contributorProfile.DeepPersonality, 1800),
                actionBattlePreference = PromptText(contributorProfile.ActionBattlePreference, 700),
                backstorySummary = PromptText(contributorProfile.BackstorySummary, 1400),
                speakingStyle = PromptText(contributorProfile.SpeakingStyle, 1200),
                characterAttributes = BuildCharacterAttributeSummary(contributorProfile),
                behaviors = PromptWritingBehaviorList(contributorProfile.Behaviors, 10, 700),
                storytellingRules = PromptList(contributorProfile.StorytellingRules, 10, 700),
                relevantSecrets = PromptList(
                    (contributorProfile.StarSecrets ?? new List<string>()).Concat(contributorProfile.LibraryCanonNotes ?? new List<string>()),
                    10,
                    800),
                viewpointDisplayName = PromptText(viewpointProfile.DisplayName, 120),
                viewpointElement = PromptText(viewpointProfile.Element, 80),
                viewpointDomain = PromptText(viewpointProfile.Domain, 120),
                viewpointCoreQuote = PromptText(viewpointProfile.CoreQuote, 500),
                viewpointPersonality = PromptText(viewpointProfile.DeepPersonality, 1600),
                viewpointActionBattlePreference = PromptText(viewpointProfile.ActionBattlePreference, 700),
                title = PromptText(outline.Title, 0),
                premise = PromptText(outline.Premise, 0),
                beats = (outline.Beats ?? new List<string>()).Select(x => PromptText(x, 0)).ToList(),
                characters = NormalizeStoryTermsUnbounded(outline.Characters),
                storySubjects = NormalizeStoryTermsUnbounded(outline.StorySubjects),
                currentStory = PromptText(currentStory, 0),
                subjects = NormalizeStoryTerms(subjects, 40),
                verbs = NormalizeStoryTerms(verbs, 40),
                storyWishes = FormatStoryWishes(storyWishes),
                seedWords = NormalizeSeedWords(seedWords),
                priorContributorStarIds = (priorContributorStarIds ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => PromptText(x, 80)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                eligibleContributorStarIds = (eligibleContributorStarIds ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => PromptText(x, 80)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                referenceValue = _valueBaselines.ReferenceValue,
                vectorOrder = StarWritingReaderEmotionVector.CategoryOrder,
                valueReferences = BuildSharedReaderEmotionCalibrationReferences(StarWritingOutputTypes.ConstellationStory, 3),
                phaseNumber = Math.Max(1, phaseNumber),
                pendingFinish,
                finishRequestedByStarId = PromptText(finishRequestedByStarId, 80)
            };
            IReadOnlyList<ChatMessage> messages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("star_writing_story_build_system.sbn", data)),
                new ChatMessage("user", _templates.Render("star_writing_story_build_user.sbn", data))
            };
            string json = await _client.CompleteJsonAsync(
                messages,
                StarResponseJsonSchema.CreateStarWritingStoryBuildResponseFormat(),
                0,
                "star-writing-story-build-" + phaseNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
            StarWritingStoryBuildResult? result = ParseAndNormalizeStoryBuildResult(json, contributorProfile.StarId, outline, pendingFinish);
            if (result == null) throw new InvalidOperationException("Constellation Forge build phase returned empty or unreadable JSON.");
            if (string.IsNullOrWhiteSpace(result.Story)) throw new InvalidOperationException("Constellation Forge build phase returned no story draft. Raw response preview: " + PromptText(json, 1600));
            return result;
        }

        public async Task<StarWritingStoryFinalResult> FinalizeStoryAsync(
            StarProfile viewpointProfile,
            StarWritingNotesState state,
            StarWritingStoryOutlineResult outline,
            string currentStory,
            IReadOnlyList<string> subjects,
            IReadOnlyList<string> verbs,
            IReadOnlyList<StarWritingStoryWish> storyWishes,
            IReadOnlyList<string> contributorStarIds,
            IReadOnlyList<string> seedWords,
            CancellationToken cancellationToken)
        {
            object data = new
            {
                starId = PromptText(viewpointProfile.StarId, 80),
                displayName = PromptText(viewpointProfile.DisplayName, 120),
                element = PromptText(viewpointProfile.Element, 80),
                domain = PromptText(viewpointProfile.Domain, 120),
                coreQuote = PromptText(viewpointProfile.CoreQuote, 500),
                personalityShort = PromptText(viewpointProfile.PersonalityShort, 900),
                deepPersonality = PromptText(viewpointProfile.DeepPersonality, 1800),
                actionBattlePreference = PromptText(viewpointProfile.ActionBattlePreference, 700),
                backstorySummary = PromptText(viewpointProfile.BackstorySummary, 1400),
                speakingStyle = PromptText(viewpointProfile.SpeakingStyle, 1200),
                characterAttributes = BuildCharacterAttributeSummary(viewpointProfile),
                behaviors = PromptWritingBehaviorList(viewpointProfile.Behaviors, 12, 700),
                storytellingRules = PromptList(viewpointProfile.StorytellingRules, 10, 700),
                title = PromptText(outline.Title, 0),
                premise = PromptText(outline.Premise, 0),
                beats = (outline.Beats ?? new List<string>()).Select(x => PromptText(x, 0)).ToList(),
                characters = NormalizeStoryTermsUnbounded(outline.Characters),
                storySubjects = NormalizeStoryTermsUnbounded(outline.StorySubjects),
                currentStory = PromptText(currentStory, 0),
                subjectsText = string.Join(", ", NormalizeStoryTerms(subjects, 40)),
                verbsText = string.Join(", ", NormalizeStoryTerms(verbs, 40)),
                storyWishes = FormatStoryWishes(storyWishes),
                contributorStarIds = (contributorStarIds ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => PromptText(x, 80)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                seedWords = NormalizeSeedWords(seedWords),
                referenceValue = _valueBaselines.ReferenceValue,
                vectorOrder = StarWritingReaderEmotionVector.CategoryOrder,
                valueReferences = BuildSharedReaderEmotionCalibrationReferences(StarWritingOutputTypes.ConstellationStory, 3)
            };
            List<ChatMessage> messages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("star_writing_story_final_system.sbn", data)),
                new ChatMessage("user", _templates.Render("star_writing_story_final_user.sbn", data))
            };
            string json = await _client.CompleteJsonAsync(
                messages,
                StarResponseJsonSchema.CreateStarWritingStoryFinalResponseFormat(),
                0,
                "star-writing-story-final",
                cancellationToken).ConfigureAwait(false);
            StarWritingStoryFinalResult? result = JsonSerializer.Deserialize<StarWritingStoryFinalResult>(ExtractJson(json), _jsonOptions);
            if (result == null)
            {
                throw new InvalidOperationException("Constellation Forge final viewpoint revision returned empty or unreadable JSON. Raw response preview: " + PromptText(json, 1600));
            }
            result.StarId = string.IsNullOrWhiteSpace(result.StarId) ? viewpointProfile.StarId : result.StarId.Trim();
            result.Title = string.IsNullOrWhiteSpace(result.Title) ? outline.Title : PromptText(result.Title, 0);
            result.Story = (result.Story ?? string.Empty).Trim();
            result.RawModelJson = json;
            if (!result.FinalStep || string.IsNullOrWhiteSpace(result.Story))
            {
                throw new InvalidOperationException(
                    "Constellation Forge final viewpoint revision violated its generation contract: finalStep must be true and story must be non-empty. Fix the final-revision instructions/model behavior rather than silently retrying or substituting output. Raw response preview: "
                    + PromptText(json, 1600));
            }
            return result;
        }

        public async Task<StarWritingRating> RateStoryAsync(
            StarProfile viewpointProfile,
            StarWritingNotesState state,
            string title,
            string story,
            IReadOnlyList<string> contributorStarIds,
            CancellationToken cancellationToken)
        {
            object data = new
            {
                displayName = PromptText(viewpointProfile.DisplayName, 120),
                element = PromptText(viewpointProfile.Element, 80),
                domain = PromptText(viewpointProfile.Domain, 120),
                coreQuote = PromptText(viewpointProfile.CoreQuote, 500),
                personalityShort = PromptText(viewpointProfile.PersonalityShort, 900),
                deepPersonality = PromptText(viewpointProfile.DeepPersonality, 1800),
                speakingStyle = PromptText(viewpointProfile.SpeakingStyle, 1200),
                behaviors = PromptWritingBehaviorList(viewpointProfile.Behaviors, 10, 700),
                contributorStarIds = (contributorStarIds ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => PromptText(x, 80)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                reviewDiscipline = StarWritingReviewDisciplines.ReaderEmotionIuV1,
                vectorOrder = StarWritingReaderEmotionVector.CategoryOrder,
                vectorCount = StarWritingReaderEmotionVector.Categories.Length,
                emotionBuckets = StarWritingStoryEmotionFeedback.Buckets.Select(x => new { emotion = x.Emotion, discipline = x.Discipline }).ToList(),
                referenceValue = _valueBaselines.ReferenceValue,
                valueReferences = BuildSharedReaderEmotionCalibrationReferences(StarWritingOutputTypes.ConstellationStory, 3),
                title = PromptText(title, 0),
                story = PromptText(story, 0)
            };
            IReadOnlyList<ChatMessage> messages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("star_writing_story_review_system.sbn", data)),
                new ChatMessage("user", _templates.Render("star_writing_story_review_user.sbn", data))
            };

            string lastJson = string.Empty;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                List<ChatMessage> attemptMessages = messages.ToList();
                if (attempt > 0)
                {
                    attemptMessages.Add(new ChatMessage(
                        "user",
                        "FORMAT REPAIR: Return only {\"valueVector\":\"...\"}. valueVector must contain exactly "
                        + StarWritingReaderEmotionVector.Categories.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " non-negative IU values separated by exactly 30 semicolons in the supplied order, with Overall last. Count every position before returning. Do not return an emotions array or per-emotion objects."));
                }

                lastJson = await _client.CompleteJsonAsync(
                    attemptMessages,
                    StarResponseJsonSchema.CreateStarWritingStoryReviewResponseFormat(),
                    0,
                    attempt == 0
                        ? "star-writing-story-emotion-vector-rate"
                        : attempt == 1
                            ? "star-writing-story-emotion-vector-rate-repair"
                            : "star-writing-story-emotion-vector-rate-repair-2",
                    cancellationToken).ConfigureAwait(false);

                try
                {
                    using JsonDocument document = JsonDocument.Parse(ExtractJson(lastJson));
                    JsonElement root = document.RootElement;
                    if (TryGetPropertyIgnoreCase(root, "valueVector", out JsonElement vectorElement))
                    {
                        string vector = ReadWritingVector(vectorElement);
                        if (StarWritingReaderEmotionVector.TryNormalizeComplete(
                                vector,
                                out string normalizedVector,
                                out Dictionary<string, StarWritingEmotionValue> values))
                        {
                            return new StarWritingRating
                            {
                                Index = 0,
                                ReviewDiscipline = StarWritingReviewDisciplines.ReaderEmotionIuV1,
                                EmotionalVector = normalizedVector,
                                EmotionFeedback = new List<StarWritingStoryEmotionValue>(),
                                OverallValue = StarWritingReaderEmotionVector.ReadValue(values, "Overall")
                            };
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }

            throw new InvalidOperationException(
                "Constellation Story review did not return one complete compact ReaderEmotionIuV1 vector after a format-repair retry. "
                + "The vector must contain every canonical reader-emotion bucket once in positional order plus Overall last. Raw response preview: "
                + PromptText(lastJson, 1800));
        }

        private StarWritingStoryBuildResult? ParseAndNormalizeStoryBuildResult(
            string json,
            string fallbackStarId,
            StarWritingStoryOutlineResult outline,
            bool pendingFinish)
        {
            string extracted = ExtractJson(json);
            try
            {
                using JsonDocument document = JsonDocument.Parse(extracted);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

                JsonElement root = document.RootElement;
                StarWritingStoryBuildResult result = new StarWritingStoryBuildResult
                {
                    StarId = ReadLooseJsonString(root, "starId"),
                    Title = ReadLooseJsonString(root, "title"),
                    Characters = ReadLooseJsonStringList(root, "characters"),
                    StorySubjects = ReadLooseJsonStringList(root, "storySubjects"),
                    RevisedBeats = ReadStoryBeatList(root, "revisedBeats"),
                    Story = ReadLooseJsonString(root, "story"),
                    AddedLayers = ReadLooseJsonStringList(root, "addedLayers"),
                    CompletionDecision = ReadLooseJsonString(root, "completionDecision"),
                    CompletionReason = ReadLooseJsonString(root, "completionReason"),
                    RawModelJson = json
                };

                result.StarId = string.IsNullOrWhiteSpace(result.StarId) ? fallbackStarId : result.StarId.Trim();
                result.Title = string.IsNullOrWhiteSpace(result.Title) ? outline.Title : PromptText(result.Title, 0);
                result.Characters = NormalizeStoryTermsUnbounded(result.Characters);
                if (result.Characters.Count == 0) result.Characters = NormalizeStoryTermsUnbounded(outline.Characters);
                result.StorySubjects = NormalizeStoryTermsUnbounded(result.StorySubjects);
                if (result.StorySubjects.Count == 0) result.StorySubjects = NormalizeStoryTermsUnbounded(outline.StorySubjects);
                result.RevisedBeats = (result.RevisedBeats ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => PromptText(x, 0))
                    .ToList();
                if (result.RevisedBeats.Count == 0) result.RevisedBeats = (outline.Beats ?? new List<string>()).ToList();
                result.Story = (result.Story ?? string.Empty).Trim();
                result.AddedLayers = (result.AddedLayers ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => PromptText(x, 0))
                    .ToList();
                result.CompletionDecision = StarWritingStoryCompletionDecisions.Normalize(result.CompletionDecision, pendingFinish);
                result.CompletionReason = PromptText(result.CompletionReason, 0);
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static List<string> ReadLooseJsonStringList(JsonElement root, string propertyName)
        {
            if (!TryGetPropertyIgnoreCase(root, propertyName, out JsonElement value)) return new List<string>();
            List<string> result = new List<string>();

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value.EnumerateArray())
                {
                    string text = FlattenStoryBeat(item);
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
                }
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    string text = FlattenStoryBeat(property.Value);
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
                }
            }
            else
            {
                string text = FlattenStoryBeat(value);
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
            }

            return result;
        }

        private StarWritingStoryOutlineResult? ParseAndNormalizeStoryOutline(string json, string fallbackStarId)
        {
            string extracted = ExtractJson(json);
            try
            {
                using JsonDocument document = JsonDocument.Parse(extracted);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

                JsonElement root = document.RootElement;
                StarWritingStoryOutlineResult result = new StarWritingStoryOutlineResult
                {
                    StarId = ReadLooseJsonString(root, "starId"),
                    Title = ReadLooseJsonString(root, "title"),
                    Premise = ReadLooseJsonString(root, "premise"),
                    Characters = ReadLooseJsonStringList(root, "characters"),
                    StorySubjects = ReadLooseJsonStringList(root, "storySubjects"),
                    Beats = ReadStoryBeatList(root),
                    RawModelJson = json
                };

                result.StarId = string.IsNullOrWhiteSpace(result.StarId) ? fallbackStarId : result.StarId.Trim();
                result.Title = PromptText(result.Title, 0);
                result.Premise = PromptText(result.Premise, 0);
                result.Characters = NormalizeStoryTermsUnbounded(result.Characters);
                result.StorySubjects = NormalizeStoryTermsUnbounded(result.StorySubjects);
                result.Beats = (result.Beats ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => PromptText(x, 0))
                    .ToList();
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static string ReadLooseJsonString(JsonElement root, string propertyName)
        {
            if (!TryGetPropertyIgnoreCase(root, propertyName, out JsonElement value)) return string.Empty;
            return JsonScalarText(value);
        }

        private static List<string> ReadStoryBeatList(JsonElement root, string propertyName = "beats")
        {
            if (!TryGetPropertyIgnoreCase(root, propertyName, out JsonElement beats)) return new List<string>();

            List<string> result = new List<string>();
            if (beats.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in beats.EnumerateArray())
                {
                    string text = FlattenStoryBeat(item);
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
                }
                return result;
            }

            // Some local models naturally express an outline as an object keyed by beat number/name.
            // Preserve that generated architecture rather than treating it as an empty outline.
            if (beats.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in beats.EnumerateObject())
                {
                    string text = FlattenStoryBeat(property.Value);
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
                }
                return result;
            }

            string single = FlattenStoryBeat(beats);
            if (!string.IsNullOrWhiteSpace(single)) result.Add(single.Trim());
            return result;
        }

        private static string FlattenStoryBeat(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String) return element.GetString() ?? string.Empty;
            if (element.ValueKind == JsonValueKind.Number || element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
            {
                return JsonScalarText(element);
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                return string.Join("; ", element.EnumerateArray()
                    .Select(FlattenStoryBeat)
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            if (element.ValueKind != JsonValueKind.Object) return string.Empty;

            List<string> pieces = new List<string>();
            foreach (JsonProperty property in element.EnumerateObject())
            {
                // Sequence metadata is redundant because array order already preserves it.
                if (string.Equals(property.Name, "index", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "number", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "beatNumber", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = FlattenStoryBeat(property.Value);
                if (string.IsNullOrWhiteSpace(value)) continue;

                if (string.Equals(property.Name, "beat", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "text", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "summary", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "description", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "content", StringComparison.OrdinalIgnoreCase))
                {
                    pieces.Add(value.Trim());
                }
                else
                {
                    pieces.Add(property.Name + ": " + value.Trim());
                }
            }

            return string.Join(" — ", pieces.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static string JsonScalarText(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString() ?? string.Empty;
                case JsonValueKind.Number:
                    return element.GetRawText();
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                default:
                    return string.Empty;
            }
        }

        private static List<string> GetMissingStoryOutlineFields(StarWritingStoryOutlineResult outline)
        {
            List<string> missing = new List<string>();
            if (string.IsNullOrWhiteSpace(outline?.Title)) missing.Add("title");
            if (string.IsNullOrWhiteSpace(outline?.Premise)) missing.Add("premise");
            if (outline?.Characters == null || outline.Characters.Count == 0) missing.Add("characters");
            if (outline?.StorySubjects == null || outline.StorySubjects.Count == 0) missing.Add("storySubjects");
            if (outline?.Beats == null || outline.Beats.Count == 0) missing.Add("beats");
            return missing;
        }

        private static void MergeStoryOutline(StarWritingStoryOutlineResult target, StarWritingStoryOutlineResult source)
        {
            if (target == null || source == null) return;
            if (!string.IsNullOrWhiteSpace(source.StarId)) target.StarId = source.StarId.Trim();
            if (!string.IsNullOrWhiteSpace(source.Title)) target.Title = source.Title.Trim();
            if (!string.IsNullOrWhiteSpace(source.Premise)) target.Premise = source.Premise.Trim();
            if (source.Characters != null && source.Characters.Count > 0) target.Characters = source.Characters.ToList();
            if (source.StorySubjects != null && source.StorySubjects.Count > 0) target.StorySubjects = source.StorySubjects.ToList();
            if (source.Beats != null && source.Beats.Count > 0) target.Beats = source.Beats.ToList();
        }

        private static List<string> NormalizeStoryTermsUnbounded(IEnumerable<string>? terms)
        {
            return (terms ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => PromptText(x, 0))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> NormalizeStoryTerms(IEnumerable<string>? terms, int limit)
        {
            return (terms ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => PromptText(x, 80))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        private static List<object> BuildTopStoryTermCounts(IDictionary<string, int>? counts)
        {
            return (counts ?? new Dictionary<string, int>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Value > 0)
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .Select(x => (object)new { word = PromptText(x.Key, 80), count = x.Value })
                .ToList();
        }

        private StarWritingReviewResult ParseWritingReviewResult(string rawContent, string fallbackStarId)
        {
            StarWritingReviewResult result = new StarWritingReviewResult
            {
                StarId = fallbackStarId,
                RawModelJson = rawContent
            };
            string json = ExtractJson(rawContent);
            if (string.IsNullOrWhiteSpace(json)) return result;

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetPropertyIgnoreCase(root, "starId", out JsonElement starIdElement)
                        && starIdElement.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(starIdElement.GetString()))
                    {
                        result.StarId = starIdElement.GetString()!.Trim();
                    }

                    if (TryGetPropertyIgnoreCase(root, "ratings", out JsonElement ratingsElement))
                    {
                        ReadWritingRatings(ratingsElement, result.Ratings);
                    }

                    // Compact preferred shape: {"0":"vector",...,"4":"vector"}.
                    if (result.Ratings.Count == 0)
                    {
                        ReadNumericWritingRatingProperties(root, result.Ratings);
                    }
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    ReadWritingRatings(root, result.Ratings);
                }
            }
            catch (JsonException)
            {
                // The caller will make one strict repair request. Do not silently create zero vectors.
            }

            return result;
        }

        private static void ReadWritingRatings(JsonElement element, List<StarWritingRating> ratings)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                int fallbackIndex = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    AddWritingRating(item, fallbackIndex++, ratings);
                }
                return;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                ReadNumericWritingRatingProperties(element, ratings);
            }
        }

        private static void ReadNumericWritingRatingProperties(JsonElement element, List<StarWritingRating> ratings)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!int.TryParse(
                    property.Name,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int index)
                    || index < 0
                    || index > 4)
                {
                    continue;
                }

                AddWritingRating(property.Value, index, ratings);
            }
        }

        private static void AddWritingRating(JsonElement item, int fallbackIndex, List<StarWritingRating> ratings)
        {
            int index = fallbackIndex;
            string vector = ReadWritingVector(item);

            if (item.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(item, "index", out JsonElement indexElement)
                    && TryReadJsonInt(indexElement, out int explicitIndex))
                {
                    index = explicitIndex;
                }
                else if (TryGetPropertyIgnoreCase(item, "statementId", out JsonElement statementIdElement)
                    && TryReadJsonInt(statementIdElement, out int statementIndex))
                {
                    index = statementIndex;
                }
                else if (TryGetPropertyIgnoreCase(item, "starId", out JsonElement legacyIndexElement)
                    && TryReadJsonInt(legacyIndexElement, out int legacyIndex))
                {
                    // Older local-model output confused the zero-based writing index with starId.
                    index = legacyIndex;
                }

                if (TryGetPropertyIgnoreCase(item, "emotionalVector", out JsonElement vectorElement)
                    || TryGetPropertyIgnoreCase(item, "vector", out vectorElement)
                    || TryGetPropertyIgnoreCase(item, "value", out vectorElement))
                {
                    vector = ReadWritingVector(vectorElement);
                }
            }

            if (!string.IsNullOrWhiteSpace(vector))
            {
                ratings.Add(new StarWritingRating
                {
                    Index = index,
                    EmotionalVector = vector.Trim()
                });
            }
        }

        private static string ReadWritingVector(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return CompleteNearExpectedWritingVector(element.GetString() ?? string.Empty);
            }

            if (element.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            JsonElement[] entries = element.EnumerateArray().ToArray();

            // Compatibility only. New review prompts require compact strings, but older local-model
            // responses may still contain arrays. ReaderEmotionIuV1 is 31 values; legacy literary
            // vectors are 27 values (or 54 alternating score/confidence values).
            bool flatNumericVector = entries.Length > 0 && entries.All(x => TryReadJsonDouble(x, out _));
            if (flatNumericVector)
            {
                List<string> scores = new List<string>();
                if ((entries.Length >= 26 && entries.Length <= 28) || entries.Length == StarWritingReaderEmotionVector.Categories.Length)
                {
                    foreach (JsonElement entry in entries)
                    {
                        if (!TryReadJsonDouble(entry, out double score)
                            || !TryNormalizeWritingValue(score.ToString(System.Globalization.CultureInfo.InvariantCulture), out string normalizedScore))
                        {
                            return string.Empty;
                        }
                        scores.Add(normalizedScore);
                    }
                }
                else if (entries.Length >= 52 && entries.Length <= 56 && entries.Length % 2 == 0)
                {
                    for (int i = 0; i < entries.Length; i += 2)
                    {
                        if (!TryReadJsonDouble(entries[i], out double score)
                            || !TryNormalizeWritingValue(score.ToString(System.Globalization.CultureInfo.InvariantCulture), out string normalizedScore))
                        {
                            return string.Empty;
                        }
                        scores.Add(normalizedScore);
                    }
                }
                else
                {
                    return string.Empty;
                }

                return CompleteNearExpectedWritingVector(string.Join(";", scores));
            }

            // Compatibility with arrays of strings and old nested [score, confidence] pairs.
            List<string> arrayScores = new List<string>();
            foreach (JsonElement entry in entries)
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    if (!TryNormalizeWritingValue(entry.GetString() ?? string.Empty, out string score)) return string.Empty;
                    arrayScores.Add(score);
                    continue;
                }

                if (entry.ValueKind == JsonValueKind.Array)
                {
                    JsonElement[] numbers = entry.EnumerateArray().Take(2).ToArray();
                    if (numbers.Length >= 1
                        && TryReadJsonDouble(numbers[0], out double score)
                        && TryNormalizeWritingValue(score.ToString(System.Globalization.CultureInfo.InvariantCulture), out string normalizedScore))
                    {
                        arrayScores.Add(normalizedScore);
                        continue;
                    }
                }

                return string.Empty;
            }

            return CompleteNearExpectedWritingVector(string.Join(";", arrayScores));
        }

        private static string CompleteNearExpectedWritingVector(string raw)
        {
            string text = (raw ?? string.Empty).Trim().Trim('"').Trim('[', ']');
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            List<string> scores = new List<string>();
            string[] separated = text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (separated.Length == 1)
            {
                separated = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            }

            if (separated.Length > 1)
            {
                foreach (string item in separated)
                {
                    if (!TryNormalizeWritingValue(item, out string score)) return string.Empty;
                    scores.Add(score);
                }
            }
            else
            {
                string[] tokens = text.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                if ((tokens.Length >= 26 && tokens.Length <= 28) || tokens.Length == StarWritingReaderEmotionVector.Categories.Length)
                {
                    // Defensive compatibility if a model uses colons between value-only items.
                    foreach (string token in tokens)
                    {
                        if (!TryNormalizeWritingValue(token, out string score)) return string.Empty;
                        scores.Add(score);
                    }
                }
                else if (tokens.Length >= 52 && tokens.Length <= 56 && tokens.Length % 2 == 0)
                {
                    // Legacy compatibility for one colon chain containing alternating
                    // score/confidence values. Confidence values are discarded.
                    for (int i = 0; i < tokens.Length; i += 2)
                    {
                        if (!TryNormalizeWritingValue(tokens[i], out string score)) return string.Empty;
                        scores.Add(score);
                    }
                }
                else
                {
                    return string.Empty;
                }
            }

            // Legacy compatibility only: old 26-value literary responses occasionally omitted Overall.
            // New ReaderEmotionIuV1 reviews are never synthesized or truncated; malformed 31-value
            // output must go through the explicit repair request.
            if (scores.Count == 26)
            {
                scores.Add(DeriveOverallWritingValue(scores));
            }

            return scores.Count == 27 || scores.Count == StarWritingReaderEmotionVector.Categories.Length
                ? string.Join(";", scores)
                : string.Empty;
        }

        private static bool TryNormalizeWritingValue(string raw, out string normalized)
        {
            normalized = string.Empty;
            string text = (raw ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(text)) return false;

            // Former output used score:confidence. Retain only the score.
            int colon = text.IndexOf(':');
            if (colon >= 0) text = text.Substring(0, colon).Trim();

            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)) return false;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return false;
            normalized = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        private static string DeriveOverallWritingValue(IReadOnlyList<string> scores)
        {
            int[] qualityIndices = { 4, 5, 6, 8, 9, 19, 20, 21, 22, 23, 24, 25 };
            List<double> values = new List<double>();
            foreach (int index in qualityIndices)
            {
                if (index < 0 || index >= scores.Count) continue;
                if (!double.TryParse(scores[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)) continue;
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) continue;
                values.Add(value);
            }

            double overall = values.Count == 0 ? 50 : values.Average();
            return overall.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool TryReadJsonDouble(JsonElement element, out double value)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value)) return true;
            if (element.ValueKind == JsonValueKind.String)
            {
                return double.TryParse(
                    element.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value);
            }

            value = 0;
            return false;
        }

        private static bool TryReadJsonInt(JsonElement element, out int value)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value)) return true;
            if (element.ValueKind == JsonValueKind.String)
            {
                return int.TryParse(
                    element.GetString(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value);
            }

            value = 0;
            return false;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static bool HasSuspiciousCollapsedWritingReview(StarWritingReviewResult result)
        {
            if (result?.Ratings == null || result.Ratings.Count < 5) return false;

            List<string> vectors = new List<string>();
            foreach (StarWritingRating rating in result.Ratings.Where(x => x != null).Take(5))
            {
                if (!StarWritingReaderEmotionVector.TryNormalizeComplete(rating.EmotionalVector, out string normalized, out _))
                {
                    return false;
                }
                vectors.Add(normalized);
            }

            // Four or five byte-for-byte identical 31-value judgments across different writings is
            // a strong sign that the local model copied a persona/template vector instead of reading.
            return vectors.GroupBy(x => x, StringComparer.Ordinal).Any(group => group.Count() >= 4);
        }

        private static bool HasCompleteWritingReview(StarWritingReviewResult result)
        {
            return GetIncompleteWritingReviewIndexes(result).Count == 0;
        }

        public async Task<StarXPostSelectionResult> PrepareXPostsAsync(
            StarProfile profile,
            StarWritingNotesState state,
            IReadOnlyList<PersistedStarWriting> unpostedWritings,
            int requestedCount,
            CancellationToken cancellationToken)
        {
            List<PersistedStarWriting> candidates = (unpostedWritings ?? Array.Empty<PersistedStarWriting>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Writing))
                .Take(500)
                .ToList();

            object data = new
            {
                starId = PromptText(profile.StarId, 80),
                displayName = PromptText(profile.DisplayName, 120),
                element = PromptText(profile.Element, 80),
                domain = PromptText(profile.Domain, 120),
                coreQuote = PromptText(profile.CoreQuote, 500),
                personalityShort = PromptText(profile.PersonalityShort, 900),
                deepPersonality = PromptText(profile.DeepPersonality, 1800),
                backstorySummary = PromptText(profile.BackstorySummary, 1600),
                speakingStyle = PromptText(profile.SpeakingStyle, 1200),
                voiceDirection = PromptText(profile.VoiceDirection, 900),
                characterAttributes = BuildCharacterAttributeSummary(profile),
                behaviors = PromptWritingBehaviorList(profile.Behaviors, 12, 700),
                totalUnpostedCount = (state.PersistedWritings ?? new List<PersistedStarWriting>()).Count(x => x != null
                    && !x.PostedAt.HasValue
                    && string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), StarWritingOutputTypes.Spark, StringComparison.OrdinalIgnoreCase)),
                candidateCount = candidates.Count,
                requestedCount = Math.Max(1, Math.Min(5, requestedCount)),
                unpostedWritings = candidates.Select(x => new
                {
                    id = PromptText(x.Id, 160),
                    generatedAt = x.GeneratedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    words = string.Join(" + ", (x.WordsUsed ?? new List<string>()).Where(w => !string.IsNullOrWhiteSpace(w)).Take(2)),
                    writing = PromptText(x.Writing, 600)
                }).ToList()
            };

            IReadOnlyList<ChatMessage> messages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("star_writing_x_post_system.sbn", data)),
                new ChatMessage("user", _templates.Render("star_writing_x_post_user.sbn", data))
            };

            string json = await _client.CompleteJsonAsync(
                messages,
                StarResponseJsonSchema.CreateStarXPostSelectionResponseFormat(),
                0,
                "star-writing-x-post-select",
                cancellationToken).ConfigureAwait(false);
            StarXPostSelectionResult? result = JsonSerializer.Deserialize<StarXPostSelectionResult>(ExtractJson(json), _jsonOptions);
            if (result == null) throw new InvalidOperationException("X post selection returned empty JSON.");
            result.StarId = string.IsNullOrWhiteSpace(result.StarId) ? profile.StarId : result.StarId;
            result.Posts ??= new List<StarXPostSelectionItem>();
            result.RawModelJson = json;
            return result;
        }

        public async Task<StarWritingPerformancePlan> PrepareWritingPerformanceAsync(
            StarProfile profile,
            StarWritingNotesState state,
            IReadOnlyList<PersistedStarWriting> pendingWritings,
            CancellationToken cancellationToken)
        {
            // The service supplies a compact mixed listening set from the top-100 IU pool. Presentation
            // order is intentionally shuffled; the saved IU values and the writing itself are the useful
            // evidence. Keeping this request to 25 complete Sparks leaves the model room to actually read
            // and compare the strongest pieces inside a 16k context window.
            List<PersistedStarWriting> performanceCandidates = (pendingWritings ?? Array.Empty<PersistedStarWriting>())
                .Where(x => x != null
                    && !string.IsNullOrWhiteSpace(x.Id)
                    && !string.IsNullOrWhiteSpace(x.Writing)
                    && string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), StarWritingOutputTypes.Spark, StringComparison.OrdinalIgnoreCase))
                .Take(WritingPerformanceCandidateLimit)
                .ToList();

            object data = new
            {
                starId = PromptText(profile.StarId, 80),
                displayName = PromptText(profile.DisplayName, 120),
                element = PromptText(profile.Element, 80),
                domain = PromptText(profile.Domain, 120),
                coreQuote = PromptText(profile.CoreQuote, 500),
                personalityShort = PromptText(profile.PersonalityShort, 900),
                deepPersonality = PromptText(profile.DeepPersonality, 1800),
                speakingStyle = PromptText(profile.SpeakingStyle, 1200),
                voiceDirection = PromptText(profile.VoiceDirection, 900),
                totalPendingCount = state?.PendingCount ?? (pendingWritings ?? Array.Empty<PersistedStarWriting>()).Count(x => x != null),
                candidateCount = performanceCandidates.Count,
                persistedWritings = performanceCandidates
                    .Select(x => new
                    {
                        id = PromptText(x.Id, 160),
                        generatedAt = x.GeneratedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        persistedAt = x.PersistedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        toldCount = x.ToldCount,
                        words = string.Join(" + ", (x.WordsUsed ?? new List<string>()).Where(w => !string.IsNullOrWhiteSpace(w)).Take(2)),
                        valueUnit = StarWritingValueUnits.InformationUnits,
                        overallValue = Math.Round(StarWritingDeterministicRanker.OverallValue(x), 2),
                        deterministicPublicValue = Math.Round(StarWritingDeterministicRanker.PublicSharingValue(x), 2),
                        writing = (x.Writing ?? string.Empty).Trim()
                    })
                    .ToList()
            };

            IReadOnlyList<ChatMessage> messages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("star_writing_performance_system.sbn", data)),
                new ChatMessage("user", _templates.Render("star_writing_performance_user.sbn", data))
            };

            // Build 15O: the Spark performance pass no longer asks the local model to build a
            // JSON object/arrays. Stream tiny positional records instead: exact-id^spokenText§.
            // This keeps the selection contract cheap while retaining the deterministic validation
            // and fallback in StarWritingService. Qwen still waits for Gemma EOS so they never
            // compete for the GPU.
            StreamingSpokenTextParser parser = new StreamingSpokenTextParser(null);
            string streamed = await _client.CompleteTextStreamingAsync(
                messages,
                0,
                "star-writing-perform-stream",
                parser.Append,
                cancellationToken).ConfigureAwait(false);
            parser.Complete();

            List<string> selectedIds = new List<string>();
            List<string> spokenText = new List<string>();
            foreach (string record in parser.Parts)
            {
                int separator = record.IndexOf('^');
                if (separator <= 0 || separator >= record.Length - 1) continue;
                string id = record.Substring(0, separator).Trim();
                string spoken = record.Substring(separator + 1).Trim();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(spoken)) continue;
                selectedIds.Add(id);
                spokenText.Add(spoken);
            }

            // Defensive fallback for a client that returns a final body but never forwards SSE
            // deltas. Parsing the final body through the same delimiter parser keeps one contract.
            if (selectedIds.Count == 0 && !string.IsNullOrWhiteSpace(streamed))
            {
                StreamingSpokenTextParser finalParser = new StreamingSpokenTextParser(null);
                finalParser.Append(streamed);
                finalParser.Complete();
                foreach (string record in finalParser.Parts)
                {
                    int separator = record.IndexOf('^');
                    if (separator <= 0 || separator >= record.Length - 1) continue;
                    string id = record.Substring(0, separator).Trim();
                    string spoken = record.Substring(separator + 1).Trim();
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(spoken)) continue;
                    selectedIds.Add(id);
                    spokenText.Add(spoken);
                }
            }

            return new StarWritingPerformancePlan
            {
                StarId = profile.StarId,
                SelectedWritingIds = selectedIds,
                SpokenText = spokenText,
                RawModelJson = streamed
            };
        }

        public async Task<StarWritingPerformancePlan> PrepareConstellationStoryPerformanceAsync(
            StarProfile profile,
            StarWritingNotesState state,
            PersistedStarWriting story,
            CancellationToken cancellationToken)
        {
            if (story == null) throw new ArgumentNullException(nameof(story));
            if (string.IsNullOrWhiteSpace(story.Id)) throw new InvalidOperationException("Constellation Story playback requires a persisted writing ID.");
            if (string.IsNullOrWhiteSpace(story.Writing)) throw new InvalidOperationException("Constellation Story playback requires story text.");

            object data = new
            {
                starId = PromptText(profile.StarId, 80),
                displayName = PromptText(profile.DisplayName, 120),
                storyId = PromptText(story.Id, 160),
                title = string.IsNullOrWhiteSpace(story.Title) ? "Untitled Constellation Story" : story.Title.Trim(),
                valueUnit = StarWritingValueUnits.InformationUnits,
                overallValue = Math.Round(StarWritingDeterministicRanker.OverallValue(story), 2),
                publicValue = Math.Round(StarWritingDeterministicRanker.PublicSharingValue(story), 2),
                story = story.Writing.Trim()
            };

            IReadOnlyList<ChatMessage> messages = new List<ChatMessage>
            {
                new ChatMessage("system", _templates.Render("star_constellation_story_performance_system.sbn", data)),
                new ChatMessage("user", _templates.Render("star_constellation_story_performance_user.sbn", data))
            };

            string json = await _client.CompleteJsonAsync(
                messages,
                StarResponseJsonSchema.CreateConstellationStoryPerformanceResponseFormat(),
                0,
                "star-writing-constellation-perform",
                cancellationToken).ConfigureAwait(false);
            StarWritingPerformancePlan? result = JsonSerializer.Deserialize<StarWritingPerformancePlan>(ExtractJson(json), _jsonOptions);
            if (result == null) throw new InvalidOperationException("Constellation Story playback formatting returned empty JSON.");
            result.StarId = string.IsNullOrWhiteSpace(result.StarId) ? profile.StarId : result.StarId;
            result.RawModelJson = json;
            return result;
        }

        private static List<object> FormatStoryWishes(IEnumerable<StarWritingStoryWish>? storyWishes)
        {
            return (storyWishes ?? Enumerable.Empty<StarWritingStoryWish>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Wish))
                .Select(x => (object)new
                {
                    sourceActorId = PromptText(x.SourceActorId, 120),
                    sourceDisplayName = PromptText(x.SourceDisplayName, 120),
                    sourceActorType = PromptText(x.SourceActorType, 80),
                    wish = PromptText(x.Wish, 1000)
                })
                .ToList();
        }

        private static List<string> NormalizeSeedWords(IEnumerable<string>? seedWords)
        {
            return (seedWords ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => PromptText(x, 80))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(100)
                .ToList();
        }

        private static List<StarWritingObjectActionItem> NormalizeObjectActionItems(IEnumerable<StarWritingObjectActionItem>? items)
        {
            List<StarWritingObjectActionItem> normalized = (items ?? Enumerable.Empty<StarWritingObjectActionItem>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Object))
                .Select(x => new StarWritingObjectActionItem
                {
                    Object = PromptText(x.Object, 80),
                    NormalActions = (x.NormalActions ?? new List<string>())
                        .Where(action => !string.IsNullOrWhiteSpace(action))
                        .Select(action => PromptText(action, 80))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToList()
                })
                .Take(5)
                .ToList();

            if (normalized.Count != 5
                || normalized.Any(x => string.IsNullOrWhiteSpace(x.Object) || x.NormalActions.Count != 3)
                || normalized.Select(x => x.Object).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 5
                || normalized.SelectMany(x => x.NormalActions).Select(NormalizeVerbComparisonKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 15)
            {
                throw new InvalidOperationException("Verb/noun displacement setup must contain exactly five distinct objects with three globally distinct ordinary actions each (15 unique verbs/actions total).");
            }

            return normalized;
        }

        private static List<StarWritingDisplacementCompositionItem> NormalizeDisplacementCompositionItems(
            IEnumerable<StarWritingDisplacementCompositionItem>? items,
            IReadOnlyList<StarWritingObjectActionItem> sourceObjects)
        {
            List<StarWritingDisplacementCompositionItem> raw = (items ?? Enumerable.Empty<StarWritingDisplacementCompositionItem>())
                .Where(x => x != null)
                .Take(5)
                .ToList();
            if (raw.Count != 5 || sourceObjects == null || sourceObjects.Count != 5)
            {
                throw new InvalidOperationException("Verb/noun displacement composition must return exactly five writings.");
            }

            HashSet<string> ordinaryVerbKeys = new HashSet<string>(
                sourceObjects.SelectMany(x => x.NormalActions ?? new List<string>()).Select(NormalizeVerbComparisonKey),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> displacedVerbKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<StarWritingDisplacementCompositionItem> normalized = new List<StarWritingDisplacementCompositionItem>();
            for (int i = 0; i < 5; i++)
            {
                StarWritingObjectActionItem source = sourceObjects[i];
                StarWritingDisplacementCompositionItem item = raw[i];
                string noun = PromptText(item.Noun, 80);
                string normalVerb = PromptText(item.NormalVerb, 80);
                string displacedVerb = PromptText(item.DisplacedVerb, 80);
                string writing = PromptText(item.Writing, 1400);

                if (!string.Equals(noun, source.Object, StringComparison.OrdinalIgnoreCase))
                {
                    noun = source.Object;
                }
                if (!source.NormalActions.Contains(normalVerb, StringComparer.OrdinalIgnoreCase))
                {
                    normalVerb = source.NormalActions[0];
                }
                if (string.IsNullOrWhiteSpace(displacedVerb)
                    || string.Equals(displacedVerb, normalVerb, StringComparison.OrdinalIgnoreCase)
                    || ordinaryVerbKeys.Contains(NormalizeVerbComparisonKey(displacedVerb))
                    || !displacedVerbKeys.Add(NormalizeVerbComparisonKey(displacedVerb))
                    || string.IsNullOrWhiteSpace(writing))
                {
                    throw new InvalidOperationException("Verb/noun displacement composition must use five unique displaced verbs, none of which duplicates any ordinary action verb, and complete every writing.");
                }

                normalized.Add(new StarWritingDisplacementCompositionItem
                {
                    Noun = noun,
                    NormalVerb = normalVerb,
                    DisplacedVerb = displacedVerb,
                    Writing = writing
                });
            }

            return normalized;
        }

        private static string NormalizeVerbComparisonKey(string? value)
        {
            string text = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (text.StartsWith("to ", StringComparison.Ordinal))
            {
                text = text.Substring(3).TrimStart();
            }
            string[] tokens = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return string.Empty;

            HashSet<string> auxiliaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "is", "are", "was", "were", "be", "being", "been",
                "can", "could", "may", "might", "will", "would", "shall", "should",
                "do", "does", "did"
            };
            int verbIndex = tokens.Length > 1 && auxiliaries.Contains(tokens[0]) ? 1 : 0;
            return tokens[verbIndex].Trim('"', '\'', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}');
        }

        private static List<List<string>> NormalizeWritingWordPairs(IEnumerable<List<string>>? wordPairs)
        {
            List<List<string>> normalized = (wordPairs ?? Enumerable.Empty<List<string>>())
                .Where(pair => pair != null)
                .Select(pair => pair
                    .Where(word => !string.IsNullOrWhiteSpace(word))
                    .Select(word => word.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToList())
                .Take(5)
                .ToList();

            if (normalized.Count != 5 || normalized.Any(pair => pair.Count != 2))
            {
                throw new InvalidOperationException("Writing word-pair selection must contain exactly five pairs of two non-empty words.");
            }

            return normalized;
        }

        private static string BuildCharacterAttributeSummary(StarProfile profile)
        {
            List<int> values = StarCharacterAttributeSchema.ParseCompactScores(
                profile.CharacterAttributes,
                StarCharacterAttributeSchema.AttributeOrder.Length);
            while (values.Count < StarCharacterAttributeSchema.AttributeOrder.Length) values.Add(0);
            return string.Join(", ", StarCharacterAttributeSchema.AttributeOrder.Select((name, index) => name + "=" + values[index]));
        }

        private static List<string> PromptList(IEnumerable<string>? values, int maximumItems, int maximumCharactersPerItem)
        {
            return (values ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => PromptText(x, maximumCharactersPerItem))
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, maximumItems))
                .ToList();
        }

        private static List<string> PromptWritingBehaviorList(IEnumerable<string>? values, int maximumItems, int maximumCharactersPerItem)
        {
            return PromptList(
                (values ?? Enumerable.Empty<string>()).Where(x =>
                    x.IndexOf("romance mode", StringComparison.OrdinalIgnoreCase) < 0
                    && x.IndexOf("girlfriend", StringComparison.OrdinalIgnoreCase) < 0
                    && x.IndexOf("relationship mode", StringComparison.OrdinalIgnoreCase) < 0),
                maximumItems,
                maximumCharactersPerItem);
        }

        private static List<string> PromptWordSelectionBehaviorList(IEnumerable<string>? values, int maximumItems, int maximumCharactersPerItem)
        {
            return PromptWritingBehaviorList(values, maximumItems, maximumCharactersPerItem)
                .Where(x => x.IndexOf("joke", StringComparison.OrdinalIgnoreCase) < 0
                    && x.IndexOf("poem", StringComparison.OrdinalIgnoreCase) < 0
                    && x.IndexOf("poet", StringComparison.OrdinalIgnoreCase) < 0
                    && x.IndexOf("writing", StringComparison.OrdinalIgnoreCase) < 0
                    && x.IndexOf("story", StringComparison.OrdinalIgnoreCase) < 0
                    && x.IndexOf("aphorism", StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();
        }

        private List<StarWritingValueReference> BuildSharedReaderEmotionCalibrationReferences(
            string outputType,
            int maximum)
        {
            // IU is one shared currency. Never calibrate a reviewer from the current actor's
            // previously persisted shelf: that creates a self-reinforcing per-actor exchange rate.
            // The curated pack is deliberately actor-neutral and identical for every reviewer.
            return _valueBaselines.GetSharedReaderEmotionCalibrationReferences(outputType, maximum);
        }

        private static bool IsTruthyFlag(StarInteractionRequest request, string key)
        {
            if (request?.GameContext?.Flags == null || string.IsNullOrWhiteSpace(key)) return false;
            if (!request.GameContext.Flags.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value)) return false;
            return value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private static string PromptText(string? value, int maximumCharacters)
        {
            string text = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (maximumCharacters <= 0 || text.Length <= maximumCharacters) return text;
            return text.Substring(0, maximumCharacters).TrimEnd() + "…";
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
                    .Where(x => x != null && IsInformationNeedSubject(x.Subject))
                    .Select(x => new InformationNeedVectorItem
                    {
                        Subject = NormalizeInformationNeedSubject(x.Subject),
                        DataType = NormalizeInformationNeedSubject(x.Subject),
                        Confidence = Clamp01(x.Confidence),
                        Score = ClampScore(x.Score)
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
                    .Where(x => x != null && IsInformationNeedSubject(x.Subject))
                    .OrderByDescending(x => Math.Max(x.Score / 100.0, x.Confidence))
                    .FirstOrDefault();
                return new InformationNeedSelectionResult
                {
                    ShouldRequestMoreInformation = best != null && (best.Confidence >= 0.55 || best.Score >= 55),
                    SelectedDataType = best == null ? "history" : NormalizeInformationNeedSubject(best.Subject),
                    Subject = best == null ? "history" : NormalizeInformationNeedSubject(best.Subject),
                    Confidence = best?.Confidence ?? 0,
                    Score = best?.Score ?? 0,
                    Reason = "Information-need selector LLM failed; foreground informationNeeds fallback used: " + ex.Message,
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
            model.ThingsSheWantsToSay = NormalizeSpeechIntentCandidates(model.ThingsSheWantsToSay, profile.StarId);
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

        public async Task<StarDecisionScoreResponse> ScoreDecisionAsync(StarProfile profile, StarDecisionScoreRequest request, StarState state, CancellationToken cancellationToken)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (request == null) throw new ArgumentNullException(nameof(request));
            state = state ?? new StarState { StarId = profile.StarId };

            System.Diagnostics.Debug.WriteLine("[StructuredStarAgent] Sending decision impulse score request for " + profile.StarId + " scene " + (request.SceneKey ?? string.Empty) + "...");
            string json = await _client.CompleteJsonAsync(
                _promptBuilder.BuildDecisionScoreMessages(profile, request, state),
                StarResponseJsonSchema.CreateDecisionScoreResponseFormat(),
                0,
                "decision-impulse-score",
                cancellationToken).ConfigureAwait(false);

            StarDecisionScoreResponse? model = JsonSerializer.Deserialize<StarDecisionScoreResponse>(ExtractJson(json), _jsonOptions);
            if (model == null) throw new InvalidOperationException("Decision score response returned empty JSON.");

            string requestedScoreOrder = string.IsNullOrWhiteSpace(request.RequestedScoreOrder)
                ? StarCharacterAttributeSchema.DecisionScoreOrderText
                : request.RequestedScoreOrder.Trim();
            int scoreCount = requestedScoreOrder.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Length;
            if (scoreCount <= 0) scoreCount = StarCharacterAttributeSchema.DecisionScoreOrder.Length;

            model.StarId = string.IsNullOrWhiteSpace(model.StarId) ? profile.StarId : model.StarId;
            model.SceneKey = string.IsNullOrWhiteSpace(model.SceneKey) ? (request.SceneKey ?? string.Empty) : CleanOneLine(model.SceneKey, 160);
            model.ScoreOrder = requestedScoreOrder;
            model.Scores = StarCharacterAttributeSchema.NormalizeCompactScores(model.Scores, scoreCount);
            model.Confidence = ClampScore(model.Confidence);
            model.ReasonCode = CleanOneLine(model.ReasonCode, 140);
            model.ShortExplanation = CleanOneLine(model.ShortExplanation, 360);
            model.Source = "llm";
            return model;
        }

        public async Task<StarInteractionResponse> GenerateAsync(StarProfile profile, StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine($"[StructuredStarAgent] Sending FAST spoken response request to LLM for {request.StarId}...");
            System.Diagnostics.Debug.WriteLine($"[StructuredStarAgent] Player text: {request.PlayerText?.Substring(0, Math.Min(60, request.PlayerText?.Length ?? 0))}...");

            bool isStory = request.RequestType == RequestType.Story;
            bool isStarInitiative = request.RequestType == RequestType.StarInitiative;
            bool isManualStreamingInitiative = isStarInitiative
                && IsTruthyFlag(request, "starInitiativeForce")
                && !IsTruthyFlag(request, "starInitiativePrepareForListen");
            bool isStructuredForeground = isStory || (isStarInitiative && !isManualStreamingInitiative);
            bool isStoryContinuation = isStory && request.StoryOptions != null && request.StoryOptions.ContinueActiveStory;
            int maxTokens = isStory && !isStoryContinuation
                ? _storyPlanMaxTokens
                : (isStarInitiative ? _initiativeMaxTokens : _speechMaxTokens);
            string purpose = isStory
                ? (isStoryContinuation ? "story-continuation-spoken-response" : "story-plan-and-first-spoken-response")
                : (isManualStreamingInitiative
                    ? "star-initiative-manual-spoken-stream"
                    : (isStarInitiative ? "star-initiative-check-response" : "fast-spoken-response-stream"));

            if (!isStructuredForeground)
            {
                StreamingStarResponseMediator mediator = new StreamingStarResponseMediator(request.SpokenChunkReady, request.StreamActionReady);
                string streamedText;
                try
                {
                    streamedText = await _client.CompleteTextStreamingAsync(
                        _promptBuilder.BuildSpeechMessages(profile, request, state),
                        maxTokens,
                        purpose,
                        mediator.Append,
                        cancellationToken).ConfigureAwait(false);
                    mediator.Complete();
                }
                catch (Exception ex)
                {
                    string failedTracePath = StreamedResponseTraceLogger.Append(
                        profile,
                        request,
                        purpose,
                        "failed",
                        mediator.RawReceivedText,
                        mediator.SpokenParts,
                        mediator.OutgoingUnresolvedVector,
                        mediator.InformationNeeds,
                        mediator.TraceEntries,
                        ex.ToString());
                    if (!string.IsNullOrWhiteSpace(failedTracePath))
                    {
                        System.Diagnostics.Debug.WriteLine("[StructuredStarAgent] Failed foreground stream trace written to " + failedTracePath);
                    }
                    throw;
                }

                List<string> spokenParts = mediator.SpokenParts.ToList();
                List<string> mediationTrace = mediator.TraceEntries.ToList();
                string rawStreamForTrace = string.IsNullOrWhiteSpace(mediator.RawReceivedText) ? streamedText : mediator.RawReceivedText;
                string foregroundOutgoingUnresolved = mediator.OutgoingUnresolvedVector;
                List<InformationNeedVectorItem> foregroundInformationNeeds = mediator.InformationNeeds.ToList();
                if (spokenParts.Count == 0 && !string.IsNullOrWhiteSpace(streamedText))
                {
                    // Some compatible clients only return the final body and never forward deltas.
                    // Run that complete body through the same mediator so control vectors are still
                    // kept out of visible/TTS speech and are only acted on once complete.
                    StreamingStarResponseMediator fallbackMediator = new StreamingStarResponseMediator(request.SpokenChunkReady, request.StreamActionReady);
                    fallbackMediator.Append(streamedText);
                    fallbackMediator.Complete();
                    spokenParts = fallbackMediator.SpokenParts.ToList();
                    foregroundOutgoingUnresolved = MergeOutgoingUnresolvedVectors(foregroundOutgoingUnresolved, fallbackMediator.OutgoingUnresolvedVector);
                    foregroundInformationNeeds = MergeInformationNeeds(foregroundInformationNeeds, fallbackMediator.InformationNeeds);
                    mediationTrace.Add("fallback mediator used because the primary streaming callback produced no speech chunks");
                    mediationTrace.AddRange(fallbackMediator.TraceEntries);
                    if (string.IsNullOrWhiteSpace(rawStreamForTrace)) rawStreamForTrace = fallbackMediator.RawReceivedText;
                }

                string streamTracePath = StreamedResponseTraceLogger.Append(
                    profile,
                    request,
                    purpose,
                    "completed",
                    rawStreamForTrace,
                    spokenParts,
                    foregroundOutgoingUnresolved,
                    foregroundInformationNeeds,
                    mediationTrace);

                StarInteractionResponse streamedResponse = new StarInteractionResponse
                {
                    StarId = profile.StarId,
                    EmotionalPurpose = isManualStreamingInitiative
                        ? "Manual at-rest Thought/Listen stream; no player emotion scoring or relationship update."
                        : "Foreground spoken reply only; background emotion/vector/memory review pending.",
                    InitiativeShouldSpeak = true,
                    InitiativePriority = isManualStreamingInitiative ? 1.0 : 0.0,
                    InitiativeTopic = isManualStreamingInitiative ? "fresh at-rest thought" : string.Empty,
                    InitiativeReason = isManualStreamingInitiative ? "The Last Cat deliberately chose to hear a Thought now." : string.Empty,
                    EmotionalEffectVector = string.Empty,
                    NegativeEmotionVector = string.Empty,
                    VoiceDirective = BuildDefaultVoiceDirective(profile, request),
                    SuggestedStateDeltas = RelationshipDelta.Zero(),
                    ConversationSummary = BuildFallbackConversationSummary(state, request, string.Join(" ", spokenParts)),
                    ChangesSummary = BuildFallbackChangesSummary(request, string.Join(" ", spokenParts)),
                    NeedsMoreInformation = foregroundInformationNeeds.Count > 0,
                    InformationNeeds = foregroundInformationNeeds,
                    ThingsLearnedOrSuggestedByPlayer = new List<string>(),
                    OutgoingUnresolvedVector = foregroundOutgoingUnresolved,
                    Debug = new DebugInfo
                    {
                        DetectedIntent = request.RequestType.ToString(),
                        WisdomScore = 0
                    }
                };
                streamedResponse.SetSpokenParts(spokenParts);
                streamedResponse.Debug.Notes.Add(isManualStreamingInitiative
                    ? "StructuredStarAgent used the manual Thought/Listen plain-text streaming speech contract."
                    : "StructuredStarAgent used REAL LLM plain-text streaming foreground speech pass.");
                streamedResponse.Debug.Notes.Add((isManualStreamingInitiative ? "Manual Thought" : "Normal foreground speech") + " used § as the speakable-thought delimiter; each completed chunk can stream to Android immediately.");
                streamedResponse.Debug.Notes.Add(isManualStreamingInitiative
                    ? "Manual Thought/Listen does not run player emotion/memory review or relationship deltas."
                    : "Heavy emotion/memory analysis and compact emotion vectors wait until the voice pipeline reports foreground voice generation ready.");
                streamedResponse.Debug.Notes.Add(maxTokens > 0
                    ? $"Per-pass max_tokens cap used: {maxTokens}."
                    : "Per-pass max_tokens cap not sent; backend/model stops on EOS/context.");
                streamedResponse.Debug.Notes.Add($"Streamed speech parts: {streamedResponse.SpokenParts.Count}; joined length: {streamedResponse.SpokenText.Length} characters.");
                streamedResponse.Debug.Notes.Add("Foreground stream mediated speech plus complete ~ controls; deliberate information needs can route immediately without a selector LLM pass, while durable learning remains background work.");
                if (foregroundInformationNeeds.Count > 0)
                {
                    streamedResponse.Debug.Notes.Add("Foreground stream requested local information: " + string.Join(", ", foregroundInformationNeeds.Select(x => x.DataType + ":" + x.Query + "@" + x.Score.ToString("0"))));
                }
                else
                {
                    streamedResponse.Debug.Notes.Add("Foreground stream emitted no accepted ~info controls. Check the streamed-response trace to distinguish model omission from malformed/rejected controls.");
                }
                if (!string.IsNullOrWhiteSpace(streamTracePath))
                {
                    streamedResponse.Debug.Notes.Add("Outgoing stream mediation trace: " + streamTracePath);
                }
                return streamedResponse;
            }

            object responseFormat = isStory
                ? StarResponseJsonSchema.CreateStorySpeechResponseFormat(isStoryContinuation)
                : StarResponseJsonSchema.CreateStarInitiativeResponseFormat();

            string json = await _client.CompleteJsonAsync(
                _promptBuilder.BuildSpeechMessages(profile, request, state),
                responseFormat,
                maxTokens,
                purpose,
                cancellationToken).ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine($"[StructuredStarAgent] Received structured foreground LLM response ({json.Length} chars)");

            StarSpeechModelResponse model = ParseSpeech(json, profile, request);
            model.ThingsLearnedOrSuggestedByPlayer = CleanDurableLearnedItems(model.ThingsLearnedOrSuggestedByPlayer, 8);
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
                ThingsLearnedOrSuggestedByPlayer = model.ThingsLearnedOrSuggestedByPlayer ?? new List<string>(),
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
            response.Debug.Notes.Add(isStarInitiative
                ? "StructuredStarAgent used REAL LLM at-rest Star initiative check pass."
                : "StructuredStarAgent used REAL LLM structured story foreground speech pass.");
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
            response.RequestToShowBoobs = model.RequestToShowBoobs || IsRequestToShowBoobs(request.PlayerText);
            response.EmotionalEffectVector = string.IsNullOrWhiteSpace(model.EmotionalEffectVector) ? (foregroundResponse.EmotionalEffectVector ?? string.Empty) : model.EmotionalEffectVector;
            response.NegativeEmotionVector = string.IsNullOrWhiteSpace(model.NegativeEmotionVector) ? (foregroundResponse.NegativeEmotionVector ?? string.Empty) : model.NegativeEmotionVector;
            // Fast speech may explicitly mark an outgoing unresolved at the exact sentence where it
            // was created. Preserve that immediate intent and merge any later background refinement.
            response.OutgoingUnresolvedVector = MergeOutgoingUnresolvedVectors(foregroundResponse.OutgoingUnresolvedVector, response.OutgoingUnresolvedVector);
            PreserveForegroundStoryFields(foregroundResponse, response);
            response.Debug.Notes.Add("StructuredStarAgent used REAL LLM background emotion/memory review pass.");
            return response;
        }

        private static List<InformationNeedVectorItem> MergeInformationNeeds(IEnumerable<InformationNeedVectorItem>? first, IEnumerable<InformationNeedVectorItem>? second)
        {
            List<InformationNeedVectorItem> merged = new List<InformationNeedVectorItem>();
            foreach (InformationNeedVectorItem item in (first ?? Array.Empty<InformationNeedVectorItem>()).Concat(second ?? Array.Empty<InformationNeedVectorItem>()))
            {
                if (item == null || string.IsNullOrWhiteSpace(item.DataType)) continue;
                int existing = merged.FindIndex(x => string.Equals(x.DataType, item.DataType, StringComparison.OrdinalIgnoreCase));
                if (existing < 0)
                {
                    merged.Add(item);
                }
                else if (Math.Max(item.Confidence, item.Score / 100.0) >= Math.Max(merged[existing].Confidence, merged[existing].Score / 100.0))
                {
                    merged[existing] = item;
                }
            }
            return merged.OrderByDescending(x => Math.Max(x.Confidence, x.Score / 100.0)).Take(3).ToList();
        }

        private static string MergeOutgoingUnresolvedVectors(string? first, string? second)
        {
            List<StarUnresolvedVectorCodec.OutgoingThread> merged = new List<StarUnresolvedVectorCodec.OutgoingThread>();
            foreach (StarUnresolvedVectorCodec.OutgoingThread item in StarUnresolvedVectorCodec.ParseOutgoingVector(first, 10)
                .Concat(StarUnresolvedVectorCodec.ParseOutgoingVector(second, 10)))
            {
                int existing = merged.FindIndex(x =>
                    string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.OpenThread, item.OpenThread, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) merged[existing] = item;
                else merged.Add(item);
            }
            return StarUnresolvedVectorCodec.EncodeOutgoingVector(merged, 10);
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
                IncomingUnresolvedVector = model.IncomingUnresolvedVector ?? string.Empty,
                OutgoingUnresolvedVector = model.OutgoingUnresolvedVector ?? string.Empty,
                UnresolvedResolutionVector = model.UnresolvedResolutionVector ?? string.Empty,
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
            response.ThingsLearnedOrSuggestedByPlayer.AddRange(CleanDurableLearnedItems(model.ThingsLearnedOrSuggestedByPlayer, 8));
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
            response.Debug.Notes.Add("Unresolved vectors: incoming=" + StarUnresolvedVectorCodec.ParseIncomingVector(response.IncomingUnresolvedVector, 10).Count + ", outgoing=" + StarUnresolvedVectorCodec.ParseOutgoingVector(response.OutgoingUnresolvedVector, 10).Count + ", resolved=" + StarUnresolvedVectorCodec.ParseResolutionVector(response.UnresolvedResolutionVector, 8).Count + ".");
            response.Debug.Notes.Add($"Response length: {response.SpokenText.Length} characters");
            return response;
        }

        private static bool IsRequestToShowBoobs(string? playerText)
        {
            if (string.IsNullOrWhiteSpace(playerText)) return false;
            string text = Regex.Replace(playerText.ToLowerInvariant(), "\\s+", " ").Trim();

            bool breastTarget = Regex.IsMatch(text, @"\b(boob|boobs|breast|breasts|tits)\b");
            bool exposureVerb = Regex.IsMatch(text, @"\b(show|see|flash|reveal|expose|display)\b");
            if (breastTarget && exposureVerb) return true;

            bool shirtTarget = Regex.IsMatch(text, @"\b(shirt|top|blouse|bra)\b");
            bool removeBeforeTarget = Regex.IsMatch(text, @"\b(take\s+off|remove|lose|drop|open|unbutton|lift|pull)\b.{0,36}\b(shirt|top|blouse|bra)\b");
            bool targetBeforeRemove = Regex.IsMatch(text, @"\b(shirt|top|blouse|bra)\b.{0,24}\b(off|up|open)\b");
            if (shirtTarget && (removeBeforeTarget || targetBeforeRemove)) return true;

            return Regex.IsMatch(text, @"\b(go|be)\s+topless\b")
                || Regex.IsMatch(text, @"\btopless\s+(for me|please|now)\b");
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
            // Initiative stays much smaller than normal chat, but it may now contain a complete
            // 1-3 chunk living moment with an acted-out need/want. Keep enough room for valid JSON
            // plus several natural sentences without allowing a long-form generation.
            string? configured = Environment.GetEnvironmentVariable("SIX_STARS_LLM_INITIATIVE_MAX_TOKENS");
            if (int.TryParse(configured, out int value))
            {
                if (value <= 0) return 0;
                return Math.Max(160, Math.Min(768, value));
            }

            return 512;
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
            foreach (string item in CleanDurableLearnedItems(foreground.ThingsLearnedOrSuggestedByPlayer, 8))
            {
                if (!analysis.ThingsLearnedOrSuggestedByPlayer.Any(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase)))
                {
                    analysis.ThingsLearnedOrSuggestedByPlayer.Add(item);
                }
            }
        }

        private static List<string> CleanDurableLearnedItems(IEnumerable<string>? values, int maxItems)
        {
            return (values ?? Enumerable.Empty<string>())
                .Select(x => CleanOneLine(x, 900))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, maxItems))
                .ToList();
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
            model.ThingsLearnedOrSuggestedByPlayer ??= new List<string>();
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
            model.IncomingUnresolvedVector = StarUnresolvedVectorCodec.NormalizeIncomingVector(model.IncomingUnresolvedVector, 10);
            model.OutgoingUnresolvedVector = StarUnresolvedVectorCodec.NormalizeOutgoingVector(model.OutgoingUnresolvedVector, 10);
            model.UnresolvedResolutionVector = StarUnresolvedVectorCodec.NormalizeResolutionVector(model.UnresolvedResolutionVector, 8);
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

        private static List<StarSpeechIntentCandidate> NormalizeSpeechIntentCandidates(IEnumerable<StarSpeechIntentCandidate>? values, string starId)
        {
            List<StarSpeechIntentCandidate> result = new List<StarSpeechIntentCandidate>();
            foreach (StarSpeechIntentCandidate item in values ?? Enumerable.Empty<StarSpeechIntentCandidate>())
            {
                if (item == null) continue;
                string goal = CleanOneLine(item.SpeechGoal, 900);
                if (string.IsNullOrWhiteSpace(goal)) continue;
                string topic = CleanOneLine(item.Topic, 240);
                string category = CleanOneLine(item.Category, 80);
                string dedupeKey = CleanOneLine(item.DedupeKey, 200);
                if (string.IsNullOrWhiteSpace(dedupeKey))
                {
                    dedupeKey = (starId + ":" + category + ":" + topic).Trim(':').ToLowerInvariant().Replace(' ', '-');
                }
                result.Add(new StarSpeechIntentCandidate
                {
                    IntentId = CleanOneLine(item.IntentId, 160),
                    Category = string.IsNullOrWhiteSpace(category) ? "daily_state" : category,
                    Topic = topic,
                    SpeechGoal = goal,
                    BaseScore = ClampScore(item.BaseScore),
                    Priority = StarSpeechIntentPriorities.Normalize(item.Priority),
                    Confidence = Clamp01(item.Confidence),
                    SourceEventIds = CleanList(item.SourceEventIds, 16),
                    DedupeKey = dedupeKey,
                    ExpiresAfterHours = Math.Max(1, Math.Min(720, item.ExpiresAfterHours <= 0 ? 24 : item.ExpiresAfterHours)),
                    RequiresAcknowledgement = item.RequiresAcknowledgement
                });
                if (result.Count >= 12) break;
            }
            return result;
        }

        private static bool IsInformationNeedSubject(string? value)
        {
            string normalized = NormalizeInformationNeedSubject(value);
            return normalized == "history" || normalized == "quotes" || normalized == "markers"
                || normalized == "lore" || normalized == "bestiary" || normalized == "characters"
                || normalized == "world" || normalized == "screen" || normalized == "story";
        }

        private static string NormalizeInformationNeedSubject(string? value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
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

        private static string ExtractJsonArray(string rawContent)
        {
            if (string.IsNullOrWhiteSpace(rawContent)) return "[]";
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
            if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal)) return text;

            int start = -1;
            for (int candidateStart = text.IndexOf('['); candidateStart >= 0; candidateStart = text.IndexOf('[', candidateStart + 1))
            {
                int next = candidateStart + 1;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                if (next >= text.Length || text[next] == '{' || text[next] == ']')
                {
                    start = candidateStart;
                    break;
                }
            }
            if (start < 0) return "[]";
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
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1).Trim();
                }
            }
            return text.Substring(start).Trim();
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
            text = StripModelControlTokens(text).Trim();
            if (text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal)) return new List<string>();
            return CleanSpeechParts(new[] { CleanRawTextForSpeech(text) });
        }

        private static string StripModelControlTokens(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string text = value;
            text = Regex.Replace(text, @"<\|channel>\s*(thought|analysis|assistant|final)\s*<channel\|>", string.Empty, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<\|(thought|analysis|assistant|final|start|end)\|>", string.Empty, RegexOptions.IgnoreCase);
            return text;
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
            string text = StripModelControlTokens(value).Trim();
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
