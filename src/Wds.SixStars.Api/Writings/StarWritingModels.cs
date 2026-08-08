using System;
using System.Collections.Generic;
using System.Linq;

namespace Wds.SixStars.Api.Writings
{
    /// <summary>One score/confidence threshold used by a Star's writing persistence rules.</summary>
    public sealed class StarWritingPersistenceRequirement
    {
        public string Category { get; set; } = string.Empty;
        public double MinimumScore { get; set; }
        public double MinimumConfidence { get; set; }
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

    public sealed class StarWritingCandidate
    {
        public List<string> WordsUsed { get; set; } = new List<string>();
        public string Writing { get; set; } = string.Empty;
    }

    public sealed class StarWritingGenerationResult
    {
        public string StarId { get; set; } = string.Empty;

        // The LLM response deliberately uses two matching arrays: Jokes[i] was created from UsedWords[i].
        public List<string> Jokes { get; set; } = new List<string>();
        public List<List<string>> UsedWords { get; set; } = new List<List<string>>();

        // Normalized internal view used by the review, history, and persistence pipeline.
        public List<StarWritingCandidate> Items { get; set; } = new List<StarWritingCandidate>();
        public string RawModelJson { get; set; } = string.Empty;
    }

    public sealed class StarWritingEmotionValue
    {
        public double Score { get; set; }
        public double Confidence { get; set; }
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
        public DateTimeOffset GeneratedAt { get; set; }
        public List<StarWritingHistoryItem> Items { get; set; } = new List<StarWritingHistoryItem>();
    }

    public sealed class PersistedStarWriting
    {
        public string Id { get; set; } = string.Empty;
        public string SourceBatchId { get; set; } = string.Empty;
        public List<string> WordsUsed { get; set; } = new List<string>();
        public string Writing { get; set; } = string.Empty;
        public string EmotionalVector { get; set; } = string.Empty;
        public List<string> MatchedPersistenceRuleIds { get; set; } = new List<string>();
        public DateTimeOffset GeneratedAt { get; set; }
        public DateTimeOffset PersistedAt { get; set; }
        public DateTimeOffset? LastToldAt { get; set; }
        public int ToldCount { get; set; }
        public bool IsPending => ToldCount <= 0;
    }

    public sealed class StarWritingNotesState
    {
        public int Version { get; set; } = 2;
        public string StarId { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
        public List<StarWritingHistoryItem> RecentWritingHistory { get; set; } = new List<StarWritingHistoryItem>();
        public List<string> AllUsedWords { get; set; } = new List<string>();
        public List<PersistedStarWriting> PersistedWritings { get; set; } = new List<PersistedStarWriting>();

        public int PendingCount => PersistedWritings?.Count(x => x != null && x.ToldCount <= 0) ?? 0;
    }

    public sealed class StarWritingCreateResult
    {
        public StarWritingNotesState State { get; set; } = new StarWritingNotesState();
        public StarWritingHistoryBatch Batch { get; set; } = new StarWritingHistoryBatch();
        public int PersistedCount { get; set; }
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

        public static string Normalize(string? raw, out Dictionary<string, StarWritingEmotionValue> values)
        {
            values = new Dictionary<string, StarWritingEmotionValue>(StringComparer.OrdinalIgnoreCase);
            string[] parts = (raw ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Categories.Length; i++)
            {
                double score = 0;
                double confidence = 0;
                if (i < parts.Length)
                {
                    string[] pair = parts[i].Trim().Split(':');
                    if (pair.Length > 0) double.TryParse(pair[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out score);
                    if (pair.Length > 1) double.TryParse(pair[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out confidence);
                }
                score = Clamp(score, 0, 100);
                confidence = Clamp(confidence, 0, 1);
                values[Categories[i]] = new StarWritingEmotionValue { Score = score, Confidence = confidence };
            }

            return string.Join(";", Categories.Select(ReadScore(values)));
        }

        private static Func<string, string> ReadScore(Dictionary<string, StarWritingEmotionValue> values)
        {
            return category =>
            {
                StarWritingEmotionValue value = values[category];
                return value.Score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ":" +
                    value.Confidence.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            };
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return min;
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
