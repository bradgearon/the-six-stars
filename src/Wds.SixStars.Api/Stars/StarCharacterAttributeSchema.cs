using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Wds.SixStars.Api.Stars
{
    public static class StarCharacterAttributeSchema
    {
        public const int Version = 1;

        public static readonly string[] AttributeOrder = new[]
        {
            "mercy",
            "patience",
            "justice",
            "honor",
            "zeal",
            "humility",
            "insight",
            "hope",
            "pride",
            "control",
            "wrath",
            "fear",
            "despair"
        };

        public static readonly string[] DecisionScoreOrder = new[]
        {
            "protect",
            "punish",
            "investigate",
            "reconcile",
            "withdraw",
            "obeyInfluence",
            "trustPlayer",
            "restrainSelf",
            "escalate",
            "seekEvidence"
        };

        public static string AttributeOrderText => string.Join(",", AttributeOrder);

        public static string DecisionScoreOrderText => string.Join(",", DecisionScoreOrder);

        public static string NormalizeCompactScores(string? compactScores, int expectedCount)
        {
            List<int> scores = ParseCompactScores(compactScores, expectedCount);
            while (scores.Count < expectedCount) scores.Add(0);
            if (scores.Count > expectedCount) scores = scores.Take(expectedCount).ToList();
            return string.Join(",", scores.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        }

        public static List<int> ParseCompactScores(string? compactScores, int expectedCount)
        {
            List<int> scores = new List<int>();
            if (!string.IsNullOrWhiteSpace(compactScores))
            {
                string[] parts = compactScores.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string part in parts)
                {
                    if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int score))
                    {
                        continue;
                    }

                    scores.Add(ClampScore(score));
                    if (expectedCount > 0 && scores.Count >= expectedCount) break;
                }
            }

            return scores;
        }

        public static int ClampScore(int score)
        {
            if (score < 0) return 0;
            if (score > 100) return 100;
            return score;
        }
    }
}
