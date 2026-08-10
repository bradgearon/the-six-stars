using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Wds.SixStars.Api.Writings
{
    /// <summary>Names the review contract used to interpret saved writing values.</summary>
    public static class StarWritingReviewDisciplines
    {
        /// <summary>Legacy/current Spark literary-value vector. Existing saved shelf data defaults here.</summary>
        public const string LiteraryValuesV1 = "literary-values-v1";

        /// <summary>Named reader-emotion feedback used for completed Constellation Stories.</summary>
        public const string ConstellationReaderEmotionsV1 = "constellation-reader-emotions-v1";

        public static string Normalize(string? value)
        {
            string text = (value ?? string.Empty).Trim();
            return string.Equals(text, ConstellationReaderEmotionsV1, StringComparison.OrdinalIgnoreCase)
                ? ConstellationReaderEmotionsV1
                : LiteraryValuesV1;
        }
    }

    public sealed class StarWritingStoryEmotionValue
    {
        public string Emotion { get; set; } = string.Empty;
        public double Value { get; set; }
    }

    public sealed class StarWritingStoryEmotionBucketDefinition
    {
        public StarWritingStoryEmotionBucketDefinition(string emotion, string discipline)
        {
            Emotion = emotion;
            Discipline = discipline;
        }

        public string Emotion { get; }
        public string Discipline { get; }
    }

    /// <summary>
    /// Canonical reader-effect discipline for Constellation Forge reviews.
    /// These buckets describe what the finished story actually makes a reader feel, not abstract craft labels.
    /// Overall literary value remains separate because it is not itself an emotion.
    /// </summary>
    public static class StarWritingStoryEmotionFeedback
    {
        public static readonly StarWritingStoryEmotionBucketDefinition[] Buckets =
        {
            new StarWritingStoryEmotionBucketDefinition("Mirthful", "Made me genuinely feel mirth or want to laugh, from a real smile/chuckle through full laughter. This is the humor-response bucket; do not use Amused and do not reward merely recognizing that a joke was attempted. The IU magnitude carries how powerfully the laughter response landed."),
            new StarWritingStoryEmotionBucketDefinition("Playful", "Made me feel mischievous, light, game, teasing, or eager to play along."),
            new StarWritingStoryEmotionBucketDefinition("Delighted", "Produced bright pleasure, joy, or a pleased emotional lift."),
            new StarWritingStoryEmotionBucketDefinition("Intrigued", "Made me want to know more because an idea, mystery, character, or contradiction caught me."),
            new StarWritingStoryEmotionBucketDefinition("Engrossed", "Held sustained attention strongly enough that I wanted to remain inside the story."),
            new StarWritingStoryEmotionBucketDefinition("Surprised", "Produced a real unexpected turn, discovery, image, behavior, or reversal rather than routine novelty."),
            new StarWritingStoryEmotionBucketDefinition("Wondrous", "Produced wonder, strange beauty, imaginative expansion, or the feeling that the world became larger."),
            new StarWritingStoryEmotionBucketDefinition("Awed", "Produced reverence, vastness, majesty, dread-filled grandeur, or overwhelming scale."),
            new StarWritingStoryEmotionBucketDefinition("Admiring", "Made me admire a character, choice, intelligence, courage, craft, or way of seeing."),
            new StarWritingStoryEmotionBucketDefinition("Moved", "Stirred me emotionally in a way that mattered rather than merely informing me that something emotional occurred."),
            new StarWritingStoryEmotionBucketDefinition("Tender", "Produced gentleness, protectiveness, softness, or careful affection."),
            new StarWritingStoryEmotionBucketDefinition("Comforted", "Made me feel soothed, steadied, reassured, sheltered, or less alone."),
            new StarWritingStoryEmotionBucketDefinition("Hopeful", "Made possibility, repair, survival, redemption, reunion, or a future feel emotionally credible."),
            new StarWritingStoryEmotionBucketDefinition("Inspired", "Made me want to create, act, endure, improve, help, attempt, or carry an idea forward."),
            new StarWritingStoryEmotionBucketDefinition("Impassioned", "Raised emotional heat, conviction, zeal, devotion, urgency, or fierce investment."),
            new StarWritingStoryEmotionBucketDefinition("Exhilarated", "Produced kinetic excitement, battle-energy, chase-energy, triumph, spectacle, danger, or thrilling momentum."),
            new StarWritingStoryEmotionBucketDefinition("Connected", "Made me feel bonds, friendship, solidarity, kinship, mutual recognition, or shared life more strongly."),
            new StarWritingStoryEmotionBucketDefinition("Affectionate", "Produced fondness, warmth, liking, or caring attachment toward a person or relationship."),
            new StarWritingStoryEmotionBucketDefinition("Attracted", "Produced romantic, sensual, magnetic, or desirous attraction without requiring explicit sexuality."),
            new StarWritingStoryEmotionBucketDefinition("Yearning", "Produced longing for a person, place, possibility, lost thing, future, reconciliation, or impossible desire."),
            new StarWritingStoryEmotionBucketDefinition("Vulnerable", "Made emotional defenses feel exposed, lowered, fragile, intimate, or personally implicated."),
            new StarWritingStoryEmotionBucketDefinition("Melancholy", "Produced beautiful or reflective sorrow, wistfulness, elegiac feeling, or bittersweet loss."),
            new StarWritingStoryEmotionBucketDefinition("Sad", "Produced direct grief, sorrow, heartbreak, or emotional pain."),
            new StarWritingStoryEmotionBucketDefinition("Haunted", "Left an eerie, lingering, uncanny, spiritually dark, or difficult-to-shake emotional residue."),
            new StarWritingStoryEmotionBucketDefinition("Uneasy", "Produced tension, discomfort, dread, moral instability, uncertainty, or a sense that something is wrong."),
            new StarWritingStoryEmotionBucketDefinition("Afraid", "Produced actual fear, threat-response, horror, danger, or anticipation of harm."),
            new StarWritingStoryEmotionBucketDefinition("Concerned", "Made me worry for a character, relationship, place, choice, or approaching consequence."),
            new StarWritingStoryEmotionBucketDefinition("Angry", "Produced anger, indignation, outrage, fury, or a desire to oppose what happened."),
            new StarWritingStoryEmotionBucketDefinition("Determined", "Made persistence, resolve, courage, resistance, or commitment feel active in me."),
            new StarWritingStoryEmotionBucketDefinition("Contemplative", "Made me stop and think, reconsider a belief, sit with a contradiction, or continue reasoning after the story ended.")
        };

        public static readonly string[] Emotions = Buckets.Select(x => x.Emotion).ToArray();

        public static bool TryNormalizeComplete(
            IEnumerable<StarWritingStoryEmotionValue>? raw,
            out List<StarWritingStoryEmotionValue> normalized,
            out Dictionary<string, StarWritingEmotionValue> values)
        {
            normalized = new List<StarWritingStoryEmotionValue>();
            values = new Dictionary<string, StarWritingEmotionValue>(StringComparer.OrdinalIgnoreCase);
            if (raw == null) return false;

            Dictionary<string, double> incoming = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (StarWritingStoryEmotionValue item in raw.Where(x => x != null))
            {
                string emotion = (item.Emotion ?? string.Empty).Trim();
                if (emotion.Length == 0
                    || !Emotions.Contains(emotion, StringComparer.OrdinalIgnoreCase)
                    || double.IsNaN(item.Value)
                    || double.IsInfinity(item.Value)
                    || item.Value < 0
                    || incoming.ContainsKey(emotion))
                {
                    return false;
                }
                incoming[emotion] = item.Value;
            }

            if (incoming.Count != Emotions.Length) return false;
            foreach (string emotion in Emotions)
            {
                if (!incoming.TryGetValue(emotion, out double value)) return false;
                StarWritingStoryEmotionValue item = new StarWritingStoryEmotionValue
                {
                    Emotion = emotion,
                    Value = value
                };
                normalized.Add(item);
                values[emotion] = new StarWritingEmotionValue { Value = value };
            }
            return true;
        }

        public static Dictionary<string, StarWritingEmotionValue> ToValueDictionary(
            IEnumerable<StarWritingStoryEmotionValue>? feedback,
            double overallValue)
        {
            Dictionary<string, StarWritingEmotionValue> values = new Dictionary<string, StarWritingEmotionValue>(StringComparer.OrdinalIgnoreCase);
            if (TryNormalizeComplete(feedback, out _, out Dictionary<string, StarWritingEmotionValue> normalized))
            {
                foreach (KeyValuePair<string, StarWritingEmotionValue> pair in normalized)
                {
                    values[pair.Key] = new StarWritingEmotionValue { Value = pair.Value.Value };
                }
            }
            if (!double.IsNaN(overallValue) && !double.IsInfinity(overallValue) && overallValue >= 0)
            {
                values["Overall"] = new StarWritingEmotionValue { Value = overallValue };
            }
            return values;
        }

        public static double ReadValue(IEnumerable<StarWritingStoryEmotionValue>? feedback, string emotion)
        {
            return (feedback ?? Enumerable.Empty<StarWritingStoryEmotionValue>())
                .Where(x => x != null && string.Equals(x.Emotion, emotion, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)
                .DefaultIfEmpty(0.0)
                .First();
        }

        public static string FormatCompact(IEnumerable<StarWritingStoryEmotionValue>? feedback, int maximum = 30)
        {
            return string.Join("; ", (feedback ?? Enumerable.Empty<StarWritingStoryEmotionValue>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Emotion))
                .Take(Math.Max(0, maximum))
                .Select(x => x.Emotion.Trim() + "=" + x.Value.ToString("0.##", CultureInfo.InvariantCulture)));
        }
    }
}
