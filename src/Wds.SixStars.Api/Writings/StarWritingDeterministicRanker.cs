using System;
using System.Collections.Generic;
using System.Linq;

namespace Wds.SixStars.Api.Writings
{
    /// <summary>
    /// Deterministic ranking helpers for persisted shelf writing. The saved review vector is the
    /// source of truth: no additional LLM call is needed to decide which part of a large shelf is
    /// strong enough to enter a later selection pass.
    /// </summary>
    public static class StarWritingDeterministicRanker
    {
        private static readonly string[] PublicQualityCategories =
        {
            "Memorable",
            "Original",
            "Coherent",
            "Meaningful",
            "Characterful",
            "EmotionallyResonant",
            "Polished",
            "Quotable",
            "WorthRetelling"
        };

        private static readonly string[] ExpressiveCategories =
        {
            "Funny",
            "Deep",
            "Intrigued",
            "Interested",
            "Poetic",
            "Clever",
            "Beautiful",
            "Moving",
            "Witty",
            "Playful",
            "Surprising",
            "Mysterious",
            "Haunting",
            "Melancholy",
            "Hopeful",
            "Romantic",
            "Vivid"
        };

        /// <summary>
        /// Scores a persisted writing for public sharing from 0-103. Overall quality provides
        /// 40%, broadly reusable craft/retellability provides 40%, and the best three expressive
        /// strengths provide 20%. A small bonus rewards writings that matched multiple persistence
        /// routes, which helps each Star's strongest characteristic material rise inside her own shelf.
        /// </summary>
        public static double PublicSharingScore(PersistedStarWriting? writing)
        {
            if (writing == null
                || !StarWritingEmotionVector.TryNormalizeUsable(
                    writing.EmotionalVector,
                    out _,
                    out Dictionary<string, StarWritingEmotionValue> values))
            {
                return -1.0;
            }

            double overall = Score(values, "Overall");
            double qualityAverage = PublicQualityCategories.Average(category => Score(values, category));
            double expressiveAverage = ExpressiveCategories
                .Select(category => Score(values, category))
                .OrderByDescending(x => x)
                .Take(3)
                .DefaultIfEmpty(0.0)
                .Average();

            int characterRouteCount = (writing.MatchedPersistenceRuleIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(id => !string.Equals(id, "global-overall-excellence", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(id, "default-overall", StringComparison.OrdinalIgnoreCase));
            double routeBonus = Math.Min(2, characterRouteCount) * 1.5;

            return (overall * 0.40)
                + (qualityAverage * 0.40)
                + (expressiveAverage * 0.20)
                + routeBonus;
        }

        /// <summary>
        /// Returns the strongest writings first. Tie breakers are also vector-derived before falling
        /// back to stable persisted metadata, so the same shelf state always produces the same window.
        /// </summary>
        public static List<PersistedStarWriting> TakeHighestRanked(
            IEnumerable<PersistedStarWriting>? writings,
            int maximum)
        {
            int take = Math.Max(0, maximum);
            if (take == 0) return new List<PersistedStarWriting>();

            return (writings ?? Enumerable.Empty<PersistedStarWriting>())
                .Where(x => x != null)
                .Select(x => new RankedWriting(x!, ReadVector(x!)))
                .OrderByDescending(x => x.PublicScore)
                .ThenByDescending(x => x.Score("Overall"))
                .ThenByDescending(x => x.Score("Quotable"))
                .ThenByDescending(x => x.Score("WorthRetelling"))
                .ThenByDescending(x => x.Score("Memorable"))
                .ThenByDescending(x => x.Score("Original"))
                .ThenByDescending(x => x.Writing.PersistedAt)
                .ThenBy(x => x.Writing.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .Select(x => x.Writing)
                .ToList();
        }

        private static Dictionary<string, StarWritingEmotionValue> ReadVector(PersistedStarWriting writing)
        {
            if (StarWritingEmotionVector.TryNormalizeUsable(
                writing.EmotionalVector,
                out _,
                out Dictionary<string, StarWritingEmotionValue> values))
            {
                return values;
            }

            return new Dictionary<string, StarWritingEmotionValue>(StringComparer.OrdinalIgnoreCase);
        }

        private static double Score(
            IReadOnlyDictionary<string, StarWritingEmotionValue> values,
            string category)
        {
            return values.TryGetValue(category, out StarWritingEmotionValue? value)
                ? Math.Max(0.0, Math.Min(100.0, value.Score))
                : 0.0;
        }

        private sealed class RankedWriting
        {
            private readonly IReadOnlyDictionary<string, StarWritingEmotionValue> _values;

            public RankedWriting(
                PersistedStarWriting writing,
                IReadOnlyDictionary<string, StarWritingEmotionValue> values)
            {
                Writing = writing;
                _values = values;
                PublicScore = PublicSharingScore(writing);
            }

            public PersistedStarWriting Writing { get; }
            public double PublicScore { get; }

            public double Score(string category)
            {
                return StarWritingDeterministicRanker.Score(_values, category);
            }
        }
    }
}
