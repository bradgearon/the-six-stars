using System;
using System.Collections.Generic;
using System.Linq;

namespace Wds.SixStars.Api.Writings
{
    /// <summary>One value threshold used by a Star's writing persistence rules.</summary>
    public sealed class StarWritingPersistenceRequirement
    {
        public string Category { get; set; } = string.Empty;
        public double MinimumValue { get; set; }

        /// <summary>Backward-compatible input alias for older profile JSON.</summary>
        public double MinimumScore { get => MinimumValue; set => MinimumValue = value; }
    }

    /// <summary>
    /// Shared quality floor applied before any Star-specific persistence rule or the global Overall backstop.
    /// The candidate must meet MinimumOverallValue and at least MinimumSupportingMatches of the listed
    /// supporting requirements. When MinimumSupportingMatches is zero, every supporting requirement is required.
    /// </summary>
    public sealed class StarWritingPersistenceQualityGate
    {
        public double MinimumOverallValue { get; set; }

        /// <summary>Backward-compatible input alias for older profile JSON.</summary>
        public double MinimumOverallScore { get => MinimumOverallValue; set => MinimumOverallValue = value; }
        public int MinimumSupportingMatches { get; set; }
        public List<StarWritingPersistenceRequirement> SupportingRequirements { get; set; } = new List<StarWritingPersistenceRequirement>();
    }

    /// <summary>
    /// A writing is persisted when at least one rule matches. Within a rule, MatchMode is "all" by default,
    /// or "any" when any requirement may qualify the writing.
    /// </summary>
    public sealed class StarWritingPersistenceRule
    {
        public string Id { get; set; } = string.Empty;
        public string MatchMode { get; set; } = "all";
        public List<StarWritingPersistenceRequirement> Requirements { get; set; } = new List<StarWritingPersistenceRequirement>();
    }

    public sealed class StarWritingWordPairResult
    {
        public string StarId { get; set; } = string.Empty;
        public List<List<string>> WordPairs { get; set; } = new List<List<string>>();
        public string RawModelJson { get; set; } = string.Empty;
    }

    public sealed class StarWritingObjectActionItem
    {
        public string Object { get; set; } = string.Empty;
        public List<string> NormalActions { get; set; } = new List<string>();
    }

    public sealed class StarWritingObjectActionResult
    {
        public string StarId { get; set; } = string.Empty;
        public List<StarWritingObjectActionItem> Objects { get; set; } = new List<StarWritingObjectActionItem>();
        public string RawModelJson { get; set; } = string.Empty;
    }

    public sealed class StarWritingDisplacementCompositionItem
    {
        public string Noun { get; set; } = string.Empty;
        public string NormalVerb { get; set; } = string.Empty;
        public string DisplacedVerb { get; set; } = string.Empty;
        public string Writing { get; set; } = string.Empty;
    }

    public sealed class StarWritingDisplacementCompositionResult
    {
        public string StarId { get; set; } = string.Empty;
        public List<StarWritingDisplacementCompositionItem> Items { get; set; } = new List<StarWritingDisplacementCompositionItem>();
        public string RawModelJson { get; set; } = string.Empty;
    }

    public sealed class StarWritingGeneratedWordSet
    {
        public DateTimeOffset GeneratedAt { get; set; }
        public List<List<string>> WordPairs { get; set; } = new List<List<string>>();
    }

    public sealed class StarWritingCandidate
    {
        /// <summary>Stable id used by iterative improvement passes to target this candidate.</summary>
        public string Id { get; set; } = string.Empty;
        public List<string> WordsUsed { get; set; } = new List<string>();
        public string Writing { get; set; } = string.Empty;
    }

    public sealed class StarWritingImprovementReplacement
    {
        public string Id { get; set; } = string.Empty;
        public double OldValue { get; set; }
        public List<string> NewWordSet { get; set; } = new List<string>();
        public string NewWriting { get; set; } = string.Empty;
        public double NewValue { get; set; }
    }

    public sealed class StarWritingImprovementResult
    {
        public List<StarWritingImprovementReplacement> Replacements { get; set; } = new List<StarWritingImprovementReplacement>();
        public string RawModelJson { get; set; } = string.Empty;
    }

    public sealed class StarWritingPassSnapshot
    {
        public int PassNumber { get; set; }
        public int AppliedReplacementCount { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<StarWritingCandidate> Items { get; set; } = new List<StarWritingCandidate>();
    }

    public sealed class StarWritingGenerationResult
    {
        public string StarId { get; set; } = string.Empty;
        public string WritingMethod { get; set; } = StarWritingMethods.SemanticCollision;
        public string OutputType { get; set; } = StarWritingOutputTypes.Spark;
        public string Title { get; set; } = string.Empty;
        public List<string> ContributingStarIds { get; set; } = new List<string>();

        // The composition response supplies Jokes. UsedWords carries the two tracked creative terms for the selected method so Jokes[i] always maps to UsedWords[i].
        public List<string> Jokes { get; set; } = new List<string>();
        public List<List<string>> UsedWords { get; set; } = new List<List<string>>();
        /// <summary>Optional random seed vocabulary shared by every pass in this writing cycle.</summary>
        public List<string> SeedWords { get; set; } = new List<string>();

        // Normalized internal view used by the review, history, and persistence pipeline.
        public List<StarWritingCandidate> Items { get; set; } = new List<StarWritingCandidate>();
        public int SelectedPassCount { get; set; } = 1;
        public int CompletedPassCount { get; set; }
        public int AppliedImprovementCount { get; set; }
        public List<StarWritingPassSnapshot> Passes { get; set; } = new List<StarWritingPassSnapshot>();
        public string RawModelJson { get; set; } = string.Empty;
    }

    public sealed class StarWritingEmotionValue
    {
        public double Value { get; set; }
    }

    public sealed class StarWritingRating
    {
        public int Index { get; set; }
        public string EmotionalVector { get; set; } = string.Empty;
    }

    public sealed class StarWritingReviewResult
    {
        public string StarId { get; set; } = string.Empty;
        public List<StarWritingRating> Ratings { get; set; } = new List<StarWritingRating>();
        public string RawModelJson { get; set; } = string.Empty;
    }

    public sealed class StarWritingHistoryItem
    {
        public string Id { get; set; } = string.Empty;
        public string WritingMethod { get; set; } = StarWritingMethods.SemanticCollision;
        public string OutputType { get; set; } = StarWritingOutputTypes.Spark;
        public string Title { get; set; } = string.Empty;
        public List<string> ContributingStarIds { get; set; } = new List<string>();
        public string SourceBatchId { get; set; } = string.Empty;
        public DateTimeOffset GeneratedAt { get; set; }
        public int Index { get; set; }
        public List<string> WordsUsed { get; set; } = new List<string>();
        public string Writing { get; set; } = string.Empty;
        public string EmotionalVector { get; set; } = string.Empty;
        public bool Persisted { get; set; }
        public List<string> MatchedPersistenceRuleIds { get; set; } = new List<string>();
    }

    public sealed class StarWritingHistoryBatch
    {
        public string Id { get; set; } = string.Empty;
        public string WritingMethod { get; set; } = StarWritingMethods.SemanticCollision;
        public string OutputType { get; set; } = StarWritingOutputTypes.Spark;
        public DateTimeOffset GeneratedAt { get; set; }
        public int SelectedPassCount { get; set; } = 1;
        public int CompletedPassCount { get; set; } = 1;
        public int AppliedImprovementCount { get; set; }
        public List<StarWritingPassSnapshot> Passes { get; set; } = new List<StarWritingPassSnapshot>();
        public List<StarWritingHistoryItem> Items { get; set; } = new List<StarWritingHistoryItem>();
    }

    /// <summary>One append-only entry in the shared all-Star writing statement log.</summary>
    public sealed class StarWritingStatementLogEntry
    {
        public int Version { get; set; } = 4;
        public string Id { get; set; } = string.Empty;
        public string SourceBatchId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string Speaker { get; set; } = string.Empty;
        public string WritingMethod { get; set; } = StarWritingMethods.SemanticCollision;
        public string OutputType { get; set; } = StarWritingOutputTypes.Spark;
        public string Title { get; set; } = string.Empty;
        public List<string> ContributingStarIds { get; set; } = new List<string>();
        public DateTimeOffset GeneratedAt { get; set; }
        public int Index { get; set; }
        public List<string> WordsUsed { get; set; } = new List<string>();
        public string Statement { get; set; } = string.Empty;
        public string EmotionalVector { get; set; } = string.Empty;
        public string ValueUnit { get; set; } = StarWritingValueUnits.InformationUnits;
        public Dictionary<string, double> Values { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public bool Persisted { get; set; }
        public List<string> MatchedPersistenceRuleIds { get; set; } = new List<string>();
    }

    public sealed class PersistedStarWriting
    {
        public string Id { get; set; } = string.Empty;
        public string WritingMethod { get; set; } = StarWritingMethods.SemanticCollision;
        public string OutputType { get; set; } = StarWritingOutputTypes.Spark;
        public string Title { get; set; } = string.Empty;
        public List<string> ContributingStarIds { get; set; } = new List<string>();
        public string SourceBatchId { get; set; } = string.Empty;
        public List<string> WordsUsed { get; set; } = new List<string>();
        public string Writing { get; set; } = string.Empty;
        public string EmotionalVector { get; set; } = string.Empty;
        public List<string> MatchedPersistenceRuleIds { get; set; } = new List<string>();
        public DateTimeOffset GeneratedAt { get; set; }
        public DateTimeOffset PersistedAt { get; set; }
        public DateTimeOffset? LastToldAt { get; set; }
        public int ToldCount { get; set; }
        public DateTimeOffset? PostedAt { get; set; }
        public string XPostId { get; set; } = string.Empty;
        public string PostedText { get; set; } = string.Empty;
        public bool IsPending => ToldCount <= 0;
        public bool IsPosted => PostedAt.HasValue;
    }

    public sealed class StarWritingStoryTermContributionResult
    {
        public string StarId { get; set; } = string.Empty;
        public List<string> Subjects { get; set; } = new List<string>();
        public List<string> Verbs { get; set; } = new List<string>();
        public string RawModelJson { get; set; } = string.Empty;
    }

    public sealed class StarWritingStoryOutlineResult
    {
        public string StarId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Premise { get; set; } = string.Empty;
        /// <summary>Living named cast for the story. Entries can include role, desire, and memorable traits.</summary>
        public List<string> Characters { get; set; } = new List<string>();
        /// <summary>Living list of the story's actual subjects, themes, situations, and conflicts.</summary>
        public List<string> StorySubjects { get; set; } = new List<string>();
        public List<string> Beats { get; set; } = new List<string>();
        public string RawModelJson { get; set; } = string.Empty;
    }

    public static class StarWritingStoryCompletionDecisions
    {
        public const string Continue = "continue";
        public const string RequestFinish = "request-finish";
        public const string ApprovePendingFinish = "approve-pending-finish";
        public const string DenyPendingFinish = "deny-pending-finish";

        public static string Normalize(string? value, bool pendingFinish)
        {
            if (pendingFinish)
            {
                return string.Equals(value, ApprovePendingFinish, StringComparison.OrdinalIgnoreCase)
                    ? ApprovePendingFinish
                    : DenyPendingFinish;
            }

            return string.Equals(value, RequestFinish, StringComparison.OrdinalIgnoreCase)
                ? RequestFinish
                : Continue;
        }
    }

    public sealed class StarWritingStoryBuildResult
    {
        public string StarId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        /// <summary>The complete updated cast after this collaborator's pass.</summary>
        public List<string> Characters { get; set; } = new List<string>();
        /// <summary>The complete updated story-subject list after this collaborator's pass.</summary>
        public List<string> StorySubjects { get; set; } = new List<string>();
        public List<string> RevisedBeats { get; set; } = new List<string>();
        public string Story { get; set; } = string.Empty;
        public List<string> AddedLayers { get; set; } = new List<string>();
        public string CompletionDecision { get; set; } = StarWritingStoryCompletionDecisions.Continue;
        public string CompletionReason { get; set; } = string.Empty;
        public string RawModelJson { get; set; } = string.Empty;
    }

    public sealed class StarWritingStoryFinalResult
    {
        public string StarId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Story { get; set; } = string.Empty;
        public bool FinalStep { get; set; }
        public string RawModelJson { get; set; } = string.Empty;
    }

    public sealed class StarWritingStoryDevelopmentPhase
    {
        public int PhaseNumber { get; set; }
        public string Stage { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public List<string> Characters { get; set; } = new List<string>();
        public List<string> StorySubjects { get; set; } = new List<string>();
        public bool PendingFinish { get; set; }
        public string CompletionDecision { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string StorySnapshot { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public sealed class StarWritingStoryDevelopmentState
    {
        public string Id { get; set; } = string.Empty;
        public string ViewpointStarId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Premise { get; set; } = string.Empty;
        public List<string> Characters { get; set; } = new List<string>();
        public List<string> StorySubjects { get; set; } = new List<string>();
        public List<string> OutlineBeats { get; set; } = new List<string>();
        public string CurrentStory { get; set; } = string.Empty;
        /// <summary>Light-pass noun/subject vocabulary pool, separate from StorySubjects.</summary>
        public List<string> Subjects { get; set; } = new List<string>();
        public List<string> Verbs { get; set; } = new List<string>();
        public List<string> ContributingStarIds { get; set; } = new List<string>();
        public bool PendingFinish { get; set; }
        public string FinishRequestedByStarId { get; set; } = string.Empty;
        public bool Completed { get; set; }
        public bool FinalStep { get; set; }
        public int CurrentPhase { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<StarWritingStoryDevelopmentPhase> Phases { get; set; } = new List<StarWritingStoryDevelopmentPhase>();
    }

    public sealed class StarWritingNotesState
    {
        public int Version { get; set; } = 12;
        public string StarId { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
        public List<StarWritingHistoryItem> RecentWritingHistory { get; set; } = new List<StarWritingHistoryItem>();
        /// <summary>Latest iterative generation state, updated and saved after every completed pass.</summary>
        public string LatestWritingMethod { get; set; } = StarWritingMethods.SemanticCollision;
        public int LatestSelectedPassCount { get; set; } = 1;
        public int LatestCompletedPassCount { get; set; }
        public int LatestAppliedImprovementCount { get; set; }
        public DateTimeOffset? LatestPassUpdatedAt { get; set; }
        public List<StarWritingPassSnapshot> LatestWritingPasses { get; set; } = new List<StarWritingPassSnapshot>();
        /// <summary>The ten newest initial five-pair selections, used to discourage near-term word repetition.</summary>
        public List<StarWritingGeneratedWordSet> RecentGeneratedWordSets { get; set; } = new List<StarWritingGeneratedWordSet>();
        /// <summary>Cumulative usage count for words selected by the initial word-pair request.</summary>
        public Dictionary<string, int> GeneratedWordUsageCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// The 50 most recently selected nouns from verb/noun displacement setup, newest first.
        /// These are recorded when the noun/object list is chosen, before later writing improvements.
        /// </summary>
        public List<string> RecentGeneratedNouns { get; set; } = new List<string>();
        /// <summary>Cumulative usage count for nouns selected by verb/noun displacement setup.</summary>
        public Dictionary<string, int> GeneratedNounUsageCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Newest Constellation Forge story subjects/nouns, newest first.</summary>
        public List<string> RecentStoryNouns { get; set; } = new List<string>();
        /// <summary>Cumulative usage count for Constellation Forge story subjects/nouns.</summary>
        public Dictionary<string, int> StoryNounUsageCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Newest Constellation Forge story verbs, newest first.</summary>
        public List<string> RecentStoryVerbs { get; set; } = new List<string>();
        /// <summary>Cumulative usage count for Constellation Forge story verbs.</summary>
        public Dictionary<string, int> StoryVerbUsageCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Latest collaborative long-form development trace, saved after each phase for inspection and diagnostics.</summary>
        public StarWritingStoryDevelopmentState? LatestStoryDevelopment { get; set; }
        public List<string> AllUsedWords { get; set; } = new List<string>();
        public List<PersistedStarWriting> PersistedWritings { get; set; } = new List<PersistedStarWriting>();

        /// <summary>Untold compact Sparks eligible for the existing five-at-a-time performance action.</summary>
        public int PendingCount => PersistedWritings?.Count(x => x != null
            && x.ToldCount <= 0
            && string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), StarWritingOutputTypes.Spark, StringComparison.OrdinalIgnoreCase)) ?? 0;
        /// <summary>Unposted compact Sparks eligible for X publishing.</summary>
        public int UnpostedCount => PersistedWritings?.Count(x => x != null
            && !x.PostedAt.HasValue
            && string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), StarWritingOutputTypes.Spark, StringComparison.OrdinalIgnoreCase)) ?? 0;
        public int ConstellationStoryCount => PersistedWritings?.Count(x => x != null
            && string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), StarWritingOutputTypes.ConstellationStory, StringComparison.OrdinalIgnoreCase)) ?? 0;
        public int PendingConstellationStoryCount => PersistedWritings?.Count(x => x != null
            && x.ToldCount <= 0
            && string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), StarWritingOutputTypes.ConstellationStory, StringComparison.OrdinalIgnoreCase)) ?? 0;
    }

    public sealed class StarWritingCreateResult
    {
        public string WritingMethod { get; set; } = StarWritingMethods.SemanticCollision;
        public string OutputType { get; set; } = StarWritingOutputTypes.Spark;
        public StarWritingNotesState State { get; set; } = new StarWritingNotesState();
        public StarWritingHistoryBatch Batch { get; set; } = new StarWritingHistoryBatch();
        public int PersistedCount { get; set; }
        public int RepairedRatingCount { get; set; }
        public int SelectedPassCount { get; set; } = 1;
        public int CompletedPassCount { get; set; } = 1;
        public int AppliedImprovementCount { get; set; }
    }

    public sealed class StarWritingPerformancePlan
    {
        public string StarId { get; set; } = string.Empty;
        public List<string> SelectedWritingIds { get; set; } = new List<string>();
        public List<string> SpokenText { get; set; } = new List<string>();
        public string RawModelJson { get; set; } = string.Empty;
    }

    public sealed class StarWritingPerformanceResult
    {
        public string OutputType { get; set; } = StarWritingOutputTypes.Spark;
        public StarWritingNotesState State { get; set; } = new StarWritingNotesState();
        public List<PersistedStarWriting> SelectedWritings { get; set; } = new List<PersistedStarWriting>();
        public double SelectedOverallValue { get; set; }
        public double SelectedPublicValue { get; set; }
        public int CandidateCount { get; set; }
        public int ElitePoolCount { get; set; }
        public int AnchorCount { get; set; }
        public int ContrastCount { get; set; }
        public Wds.SixStars.Api.Models.StarInteractionResponse Response { get; set; } = new Wds.SixStars.Api.Models.StarInteractionResponse();
    }

    public static class StarWritingEmotionVector
    {
        public static readonly string[] Categories =
        {
            "Funny", "Deep", "Intrigued", "Interested", "Poetic", "Clever", "Beautiful",
            "Moving", "Memorable", "Original", "Witty", "Playful", "Surprising", "Mysterious",
            "Haunting", "Melancholy", "Hopeful", "Romantic", "Vivid", "Coherent", "Meaningful",
            "Characterful", "EmotionallyResonant", "Polished", "Quotable", "WorthRetelling", "Overall"
        };

        public static string CategoryOrder => string.Join(",", Categories);

        /// <summary>
        /// Normalizes the current value-only format while still accepting the former
        /// score:confidence representation so existing writing-note files migrate without
        /// another LLM request. Values are non-negative and deliberately have no upper bound.
        /// </summary>
        public static bool TryNormalizeComplete(
            string? raw,
            out string normalized,
            out Dictionary<string, StarWritingEmotionValue> values)
        {
            normalized = string.Empty;
            values = new Dictionary<string, StarWritingEmotionValue>(StringComparer.OrdinalIgnoreCase);
            string text = (raw ?? string.Empty).Trim().Trim('"').Trim('[', ']');
            if (string.IsNullOrWhiteSpace(text)) return false;

            List<double> parsed = new List<double>();
            string[] separated = text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (separated.Length == 1)
            {
                separated = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            }

            if (separated.Length == Categories.Length)
            {
                foreach (string item in separated)
                {
                    if (!TryParseValue(item, out double value)) return false;
                    parsed.Add(value);
                }
            }
            else
            {
                string[] tokens = text.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == Categories.Length)
                {
                    // Defensive compatibility if a model uses colons between score-only items.
                    foreach (string token in tokens)
                    {
                        if (!TryParseValue(token, out double value)) return false;
                        parsed.Add(value);
                    }
                }
                else if (tokens.Length == Categories.Length * 2)
                {
                    // Legacy compatibility for malformed old output that omitted semicolons
                    // and returned alternating score/confidence values in one colon chain.
                    for (int i = 0; i < Categories.Length; i++)
                    {
                        if (!TryParseValue(tokens[i * 2], out double value)) return false;
                        parsed.Add(value);
                    }
                }
                else
                {
                    return false;
                }
            }

            for (int i = 0; i < Categories.Length; i++)
            {
                values[Categories[i]] = new StarWritingEmotionValue { Value = parsed[i] };
            }

            normalized = Format(values);
            return true;
        }

        public static bool TryNormalizeUsable(
            string? raw,
            out string normalized,
            out Dictionary<string, StarWritingEmotionValue> values)
        {
            if (!TryNormalizeComplete(raw, out normalized, out values)) return false;

            // A local model was observed returning category positions 0..26 as values.
            // That has the correct length but is not a literary rating vector.
            int categoryIndexValues = 0;
            for (int i = 0; i < Categories.Length; i++)
            {
                if (values.TryGetValue(Categories[i], out StarWritingEmotionValue value)
                    && Math.Abs(value.Value - i) < 0.0001)
                {
                    categoryIndexValues++;
                }
            }
            if (categoryIndexValues >= 20) return false;

            // Real literary values should not consist almost entirely of 0..1 placeholders.
            if (values.Values.Count(x => x.Value <= 1.0) >= 24) return false;

            return true;
        }

        public static string Normalize(string? raw, out Dictionary<string, StarWritingEmotionValue> values)
        {
            if (TryNormalizeComplete(raw, out string normalized, out values))
            {
                return normalized;
            }

            // Lenient legacy path used while reading old or partially damaged files.
            values = new Dictionary<string, StarWritingEmotionValue>(StringComparer.OrdinalIgnoreCase);
            string text = (raw ?? string.Empty).Trim().Trim('"').Trim('[', ']');
            string[] parts = text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) parts = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Categories.Length; i++)
            {
                double value = 0;
                if (i < parts.Length) TryParseValue(parts[i], out value);
                values[Categories[i]] = new StarWritingEmotionValue { Value = ClampMinimum(value, 0) };
            }

            return Format(values);
        }

        private static bool TryParseValue(string raw, out double value)
        {
            value = 0;
            string text = (raw ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(text)) return false;

            // Former files store score:confidence. Keep only the score during migration.
            int colon = text.IndexOf(':');
            if (colon >= 0) text = text.Substring(0, colon).Trim();

            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)) return false;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return false;
            return true;
        }

        public static string FormatValues(IReadOnlyDictionary<string, double> values)
        {
            if (values == null) return string.Empty;
            Dictionary<string, StarWritingEmotionValue> normalized = new Dictionary<string, StarWritingEmotionValue>(StringComparer.OrdinalIgnoreCase);
            foreach (string category in Categories)
            {
                if (!values.TryGetValue(category, out double value)
                    || double.IsNaN(value)
                    || double.IsInfinity(value)
                    || value < 0)
                {
                    return string.Empty;
                }
                normalized[category] = new StarWritingEmotionValue { Value = value };
            }
            return Format(normalized);
        }

        public static double ReadValue(IReadOnlyDictionary<string, StarWritingEmotionValue> values, string category)
        {
            return values != null
                && values.TryGetValue(category ?? string.Empty, out StarWritingEmotionValue? value)
                && value != null
                && !double.IsNaN(value.Value)
                && !double.IsInfinity(value.Value)
                ? Math.Max(0.0, value.Value)
                : 0.0;
        }

        private static string Format(IReadOnlyDictionary<string, StarWritingEmotionValue> values)
        {
            return string.Join(";", Categories.Select(category =>
                values[category].Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
        }

        private static double ClampMinimum(double value, double min)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return min;
            return Math.Max(min, value);
        }
    }
}