using System;
using System.Collections.Generic;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Stars
{
    public static class StarEmotionMatrix
    {
        public static readonly string[] StandardEmotionIds = new[]
        {
            "Happy",
            "Amused",
            "Playful",
            "Comforted",
            "Safe",
            "Protected",
            "Trusting",
            "Respected",
            "Admiring",
            "Affectionate",
            "Vulnerable",
            "Hopeful",
            "Wondrous",
            "Intrigued",
            "Impassioned",
            "Determined",
            "Concerned",
            "Confused",
            "Unsure",
            "Frustrated",
            "Angry",
            "Hurt",
            "Sad",
            "Melancholy",
            "Lonely",
            "Guilty",
            "Suspicious",
            "Flattered",
            "Attracted",
            "Tender",
            "Yearning",
            "Jealous"
        };

        /// <summary>
        /// Backward-compatible method name. It now returns the canonical Six Stars emotion set:
        /// 27 general emotions plus 5 optional romance-layer emotions.
        /// </summary>
        public static List<StarEmotionDefinition> CreateDefault27(string starId)
        {
            return CreateStandardEmotionSet(starId);
        }

        public static List<StarEmotionDefinition> CreateStandardEmotionSet(string starId)
        {
            List<StarEmotionDefinition> result = new List<StarEmotionDefinition>();
            foreach (string emotion in StandardEmotionIds)
            {
                result.Add(new StarEmotionDefinition
                {
                    Id = emotion,
                    DisplayName = ToDisplayName(starId, emotion),
                    Valence = DetermineValence(emotion),
                    Tags = CreateTags(emotion)
                });
            }

            return result;
        }

        private static EmotionValence DetermineValence(string emotion)
        {
            switch (emotion)
            {
                case "Happy":
                case "Amused":
                case "Playful":
                case "Comforted":
                case "Safe":
                case "Protected":
                case "Trusting":
                case "Respected":
                case "Admiring":
                case "Affectionate":
                case "Hopeful":
                case "Wondrous":
                case "Intrigued":
                case "Impassioned":
                case "Determined":
                case "Flattered":
                case "Attracted":
                case "Tender":
                case "Yearning":
                    return EmotionValence.Positive;
                case "Concerned":
                case "Confused":
                case "Unsure":
                case "Vulnerable":
                    return EmotionValence.Neutral;
                default:
                    return EmotionValence.Negative;
            }
        }

        private static List<string> CreateTags(string emotion)
        {
            List<string> tags = new List<string>();
            switch (emotion)
            {
                case "Flattered":
                case "Attracted":
                case "Tender":
                case "Yearning":
                case "Jealous":
                    tags.Add("romance");
                    break;
            }

            switch (emotion)
            {
                case "Affectionate":
                case "Trusting":
                case "Comforted":
                case "Safe":
                case "Protected":
                case "Respected":
                case "Admiring":
                case "Flattered":
                case "Attracted":
                case "Tender":
                case "Yearning":
                    tags.Add("drawn_near");
                    break;
                case "Suspicious":
                case "Confused":
                case "Unsure":
                    tags.Add("guarded");
                    break;
                case "Hurt":
                case "Sad":
                case "Melancholy":
                case "Lonely":
                case "Angry":
                case "Frustrated":
                case "Guilty":
                case "Jealous":
                    tags.Add("withdrawing");
                    break;
            }

            return tags;
        }

        private static string ToDisplayName(string starId, string emotionId)
        {
            if (starId.Equals("Elusiaa", StringComparison.OrdinalIgnoreCase))
            {
                if (emotionId == "Affectionate") return "Warm Mercy";
                if (emotionId == "Concerned") return "Holy Caution";
                if (emotionId == "Hurt") return "Wounded Mercy";
                if (emotionId == "Wondrous") return "Radiant Wonder";
                if (emotionId == "Tender") return "Tender Light";
            }

            if (starId.Equals("Kaelia", StringComparison.OrdinalIgnoreCase))
            {
                if (emotionId == "Hopeful") return "The Void Feels Lighter";
                if (emotionId == "Suspicious") return "Void-Touched Suspicion";
                if (emotionId == "Hurt") return "Tar-Water Collapse";
                if (emotionId == "Tender") return "Quiet Contradiction";
            }

            return emotionId;
        }
    }
}
