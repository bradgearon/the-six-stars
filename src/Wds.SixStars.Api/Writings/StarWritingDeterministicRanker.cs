using System;
using System.Collections.Generic;
using System.Linq;

namespace Wds.SixStars.Api.Writings
{
    /// <summary>
    /// Deterministic ranking helpers for persisted shelf writing. The saved open-ended review value vector is the
    /// source of truth: no additional LLM call is needed to decide which part of a large shelf is strongest.
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
        /// Computes an open-ended public-sharing value. Overall contributes 40%, broadly reusable
        /// craft/retellability 40%, and the strongest three expressive dimensions 20%. Multiple
        /// non-global persistence routes add a small proportional bonus, so the calculation remains
        /// meaningful when values rise above 100 instead of flattening against a hard ceiling.
        /// </summary>
        public static double PublicSharingValue(PersistedStarWriting? writing)
        {
            if (writing == null
                || !StarWritingEmotionVector.TryNormalizeUsable(
                    writing.EmotionalVector,
                    out _,
                    out Dictionary<string, StarWritingEmotionValue> values))
            {
                return -1.0;
            }

            double overall = Value(values, "Overall");
            double qualityAverage = PublicQualityCategories.Average(category => Value(values, category));
            double expressiveAverage = ExpressiveCategories
                .Select(category => Value(values, category))
                .OrderByDescending(x => x)
                .Take(3)
                .DefaultIfEmpty(0.0)
                .Average();

            double baseValue = (overall * 0.40)
                + (qualityAverage * 0.40)
                + (expressiveAverage * 0.20);

            int characterRouteCount = (writing.MatchedPersistenceRuleIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(id => !string.Equals(id, "global-overall-excellence", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(id, "default-overall", StringComparison.OrdinalIgnoreCase));
            double routeMultiplier = 1.0 + (Math.Min(2, characterRouteCount) * 0.015);

            return baseValue * routeMultiplier;
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
                .OrderByDescending(x => x.PublicValue)
                .ThenByDescending(x => x.Value("Overall"))
                .ThenByDescending(x => x.Value("Quotable"))
                .ThenByDescending(x => x.Value("WorthRetelling"))
                .ThenByDescending(x => x.Value("Memorable"))
                .ThenByDescending(x => x.Value("Original"))
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

        private static double Value(
            IReadOnlyDictionary<string, StarWritingEmotionValue> values,
            string category)
        {
            return StarWritingEmotionVector.ReadValue(values, category);
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
                PublicValue = PublicSharingValue(writing);
            }

            public PersistedStarWriting Writing { get; }
            public double PublicValue { get; }

            public double Value(string category)
            {
                return StarWritingDeterministicRanker.Value(_values, category);
            }
        }
    }
}
