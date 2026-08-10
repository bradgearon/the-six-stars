using System;
using System.Collections.Generic;
using System.Linq;

namespace Wds.SixStars.Api.Writings
{
    /// <summary>One score threshold used by a Star's writing persistence rules.</summary>
    public sealed class StarWritingPersistenceRequirement
    {
        public string Category { get; set; } = string.Empty;
        public double MinimumScore { get; set; }
    }

    /// <summary>
    /// Shared quality floor applied before any Star-specific persistence rule or the global Overall backstop.
    /// The candidate must meet MinimumOverallScore and at least MinimumSupportingMatches of the listed
    /// supporting requirements. When MinimumSupportingMatches is zero, every supporting requirement is required.
    /// </summary>
    public sealed class StarWritingPersistenceQualityGate
    {
        public double MinimumOverallScore { get; set; }
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
        public double OldScore { get; set; }
        public List<string> NewWordSet { get; set; } = new List<string>();
        public string NewWriting { get; set; } = string.Empty;
        public double NewScore { get; set; }
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
        public double Score { get; set; }
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
        public int Version { get; set; } = 2;
        public string Id { get; set; } = string.Empty;
        public string SourceBatchId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string Speaker { get; set; } = string.Empty;
        public string WritingMethod { get; set; } = StarWritingMethods.SemanticCollision;
        public DateTimeOffset GeneratedAt { get; set; }
        public int Index { get; set; }
        public List<string> WordsUsed { get; set; } = new List<string>();
        public string Statement { get; set; } = string.Empty;
        public string EmotionalVector { get; set; } = string.Empty;
        public Dictionary<string, double> Scores { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public bool Persisted { get; set; }
        public List<string> MatchedPersistenceRuleIds { get; set; } = new List<string>();
    }

    public sealed class PersistedStarWriting
    {
        public string Id { get; set; } = string.Empty;
        public string WritingMethod { get; set; } = StarWritingMethods.SemanticCollision;
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

    public sealed class StarWritingNotesState
    {
        public int Version { get; set; } = 9;
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
        public List<string> AllUsedWords { get; set; } = new List<string>();
        public List<PersistedStarWriting> PersistedWritings { get; set; } = new List<PersistedStarWriting>();

        public int PendingCount => PersistedWritings?.Count(x => x != null && x.ToldCount <= 0) ?? 0;
        public int UnpostedCount => PersistedWritings?.Count(x => x != null && !x.PostedAt.HasValue) ?? 0;
    }

    public sealed class StarWritingCreateResult
    {
        public string WritingMethod { get; set; } = StarWritingMethods.SemanticCollision;
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
        public StarWritingNotesState State { get; set; } = new StarWritingNotesState();
        public List<PersistedStarWriting> SelectedWritings { get; set; } = new List<PersistedStarWriting>();
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
        /// Normalizes the current score-only format while still accepting the former
        /// score:confidence representation so existing writing-note files migrate without
        /// another LLM request. The normalized value always contains only 27 scores.
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
                    if (!TryParseScore(item, out double score)) return false;
                    parsed.Add(score);
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
                        if (!TryParseScore(token, out double score)) return false;
                        parsed.Add(score);
                    }
                }
                else if (tokens.Length == Categories.Length * 2)
                {
                    // Legacy compatibility for malformed old output that omitted semicolons
                    // and returned alternating score/confidence values in one colon chain.
                    for (int i = 0; i < Categories.Length; i++)
                    {
                        if (!TryParseScore(tokens[i * 2], out double score)) return false;
                        parsed.Add(score);
                    }
                }
                else
                {
                    return false;
                }
            }

            for (int i = 0; i < Categories.Length; i++)
            {
                values[Categories[i]] = new StarWritingEmotionValue { Score = parsed[i] };
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

            // A local model was observed returning category positions 0..26 as scores.
            // That has the correct length but is not a literary rating vector.
            int categoryIndexScores = 0;
            for (int i = 0; i < Categories.Length; i++)
            {
                if (values.TryGetValue(Categories[i], out StarWritingEmotionValue value)
                    && Math.Abs(value.Score - i) < 0.0001)
                {
                    categoryIndexScores++;
                }
            }
            if (categoryIndexScores >= 20) return false;

            // Real 0-100 ratings should not consist almost entirely of 0..1 placeholders.
            if (values.Values.Count(x => x.Score <= 1.0) >= 24) return false;

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
                double score = 0;
                if (i < parts.Length) TryParseScore(parts[i], out score);
                values[Categories[i]] = new StarWritingEmotionValue { Score = Clamp(score, 0, 100) };
            }

            return Format(values);
        }

        private static bool TryParseScore(string raw, out double score)
        {
            score = 0;
            string text = (raw ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(text)) return false;

            // Former files store score:confidence. Keep only the score during migration.
            int colon = text.IndexOf(':');
            if (colon >= 0) text = text.Substring(0, colon).Trim();

            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out score)) return false;
            if (double.IsNaN(score) || double.IsInfinity(score) || score < 0 || score > 100) return false;
            return true;
        }

        private static string Format(IReadOnlyDictionary<string, StarWritingEmotionValue> values)
        {
            return string.Join(";", Categories.Select(category =>
                values[category].Score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return min;
            return Math.Max(min, Math.Min(max, value));
        }
    }
}