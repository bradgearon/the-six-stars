using System;
using System.Collections.Generic;
using System.Linq;

namespace Wds.SixStars.Api.Stars
{
    /// <summary>
    /// Authored deterministic relationship-personality defaults for the Six Stars.
    /// File JSON can override these, but every profile is normalized through this matrix so the scorer always
    /// has Heart/Trust/Romance thresholds, emotion influence weights, and combo rules.
    /// </summary>
    public static class StarRelationshipPersonalityMatrix
    {
        public static void EnsureDeterministicRelationshipConfig(StarProfile profile)
        {
            if (profile == null) return;
            if (profile.RelationshipStats == null) profile.RelationshipStats = CreateRelationshipStats(profile.StarId);
            NormalizeStats(profile.RelationshipStats);

            if (profile.EmotionInfluence == null || profile.EmotionInfluence.Count == 0)
            {
                profile.EmotionInfluence = CreateEmotionInfluence(profile.StarId);
            }
            else
            {
                EnsureEveryKnownEmotionHasInfluence(profile.StarId, profile.EmotionInfluence);
            }

            if (profile.ComboRules == null || profile.ComboRules.Count == 0)
            {
                profile.ComboRules = CreateComboRules(profile.StarId);
            }
        }

        public static StarRelationshipStatsConfig CreateRelationshipStats(string starId)
        {
            StarRelationshipStatsConfig stats = new StarRelationshipStatsConfig
            {
                Heart = new StarRelationshipStatRule { ScorePerPoint = 80, MaxPerTurn = 2 },
                Trust = new StarRelationshipStatRule { ScorePerPoint = 90, MaxPerTurn = 2 },
                Romance = new StarRelationshipStatRule { ScorePerPoint = 120, MaxPerTurn = 1 }
            };

            switch ((starId ?? string.Empty).ToLowerInvariant())
            {
                case "elusiaa":
                    stats.Heart.ScorePerPoint = 78;
                    stats.Trust.ScorePerPoint = 86;
                    stats.Romance.ScorePerPoint = 125;
                    break;
                case "khavia":
                    stats.Heart.ScorePerPoint = 76;
                    stats.Trust.ScorePerPoint = 82;
                    stats.Romance.ScorePerPoint = 118;
                    break;
                case "auerlis":
                    stats.Heart.ScorePerPoint = 74;
                    stats.Trust.ScorePerPoint = 88;
                    stats.Romance.ScorePerPoint = 112;
                    break;
                case "maera":
                    stats.Heart.ScorePerPoint = 90;
                    stats.Trust.ScorePerPoint = 78;
                    stats.Romance.ScorePerPoint = 132;
                    break;
                case "kaelia":
                    stats.Heart.ScorePerPoint = 84;
                    stats.Trust.ScorePerPoint = 94;
                    stats.Romance.ScorePerPoint = 128;
                    break;
                case "nyxari":
                    stats.Heart.ScorePerPoint = 86;
                    stats.Trust.ScorePerPoint = 92;
                    stats.Romance.ScorePerPoint = 130;
                    break;
            }

            return stats;
        }

        public static List<StarEmotionInfluenceRule> CreateEmotionInfluence(string starId)
        {
            Dictionary<string, StarEmotionInfluenceRule> rules = CreateBaselineEmotionInfluence();
            ApplyStarOverrides((starId ?? string.Empty).ToLowerInvariant(), rules);
            return rules.Values.OrderBy(x => x.Emotion, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<StarEmotionComboRule> CreateComboRules(string starId)
        {
            switch ((starId ?? string.Empty).ToLowerInvariant())
            {
                case "elusiaa":
                    return new List<StarEmotionComboRule>
                    {
                        Combo("merciful_sadness_comfort", "The player responds to pain with sadness, mercy, and comfort instead of judgment.", 4, 3, 1.35,
                            Weights(1.0, 0.70, 0.15),
                            Req("Sad", 0.70, 40, "compassionate"), Req("Comforted", 0.70, 35), Req("Hopeful", 0.66, 35)),
                        Combo("restrained_justice_safety", "The player sees possible evil but chooses patient correction, safety, and restrained justice.", 5, 3, 1.30,
                            Weights(0.85, 1.0, 0.10),
                            Req("Concerned", 0.62, 35), Req("Protected", 0.70, 45), Req("Respected", 0.70, 40)),
                        Combo("warm_mercy_trust", "The player consistently makes Elusiaa feel safe, comforted, and trusting.", 4, 3, 1.25,
                            Weights(0.75, 0.95, 0.20),
                            Req("Safe", 0.70, 40), Req("Comforted", 0.70, 40), Req("Trusting", 0.70, 40))
                    };
                case "khavia":
                    return new List<StarEmotionComboRule>
                    {
                        Combo("friendship_social_repair", "The player turns conflict into friendship and helps outsiders belong.", 4, 3, 1.30,
                            Weights(0.80, 1.0, 0.18),
                            Req("Happy", 0.66, 35), Req("Trusting", 0.70, 40), Req("Respected", 0.66, 35)),
                        Combo("bridge_builder_loyalty", "The player keeps showing up, building bridges and proving loyal.", 5, 3, 1.28,
                            Weights(0.75, 1.05, 0.14),
                            Req("Protected", 0.66, 35), Req("Comforted", 0.66, 35), Req("Hopeful", 0.66, 35)),
                        Combo("playful_friend_to_tender", "Friendship becomes playful, affectionate tenderness without skipping trust.", 4, 3, 1.22,
                            Weights(0.85, 0.65, 0.45),
                            Req("Playful", 0.66, 35), Req("Affectionate", 0.70, 40), Req("Tender", 0.70, 35))
                    };
                case "auerlis":
                    return new List<StarEmotionComboRule>
                    {
                        Combo("defiant_joy_in_darkness", "The player answers darkness with laughter, hope, and courage.", 4, 3, 1.35,
                            Weights(1.0, 0.70, 0.30),
                            Req("Amused", 0.66, 35), Req("Hopeful", 0.70, 40), Req("Determined", 0.66, 40)),
                        Combo("music_fire_warmth", "Music, play, and bright passion make Auerlis feel alive.", 4, 3, 1.25,
                            Weights(0.95, 0.55, 0.45),
                            Req("Happy", 0.66, 35), Req("Playful", 0.66, 35), Req("Impassioned", 0.70, 40)),
                        Combo("brave_laughter_affection", "The player flirts or jokes with courage rather than despair.", 4, 3, 1.22,
                            Weights(0.85, 0.50, 0.55),
                            Req("Playful", 0.66, 35), Req("Admiring", 0.66, 35), Req("Affectionate", 0.70, 40))
                    };
                case "maera":
                    return new List<StarEmotionComboRule>
                    {
                        Combo("safe_attachment_boundary", "The player offers closeness while respecting boundaries and refusing ownership.", 5, 3, 1.38,
                            Weights(0.70, 1.05, 0.35),
                            Req("Safe", 0.70, 40), Req("Respected", 0.70, 40), Req("Tender", 0.66, 35)),
                        Combo("choosing_life_over_possession", "The player chooses life, patience, and staying present instead of feeding possession.", 5, 3, 1.30,
                            Weights(0.90, 0.85, 0.20),
                            Req("Protected", 0.66, 35), Req("Hopeful", 0.66, 35), Req("Trusting", 0.70, 40)),
                        Combo("possessive_pressure_warning", "Possession, jealousy, or pressure is getting fed and should hurt trust.", 4, 2, 1.20,
                            Weights(-0.65, -1.0, -0.45),
                            Req("Jealous", 0.70, 45), Req("Pressured", 0.70, 45))
                    };
                case "kaelia":
                    return new List<StarEmotionComboRule>
                    {
                        Combo("hope_against_fatalism", "The player stays with darkness and makes change feel possible.", 5, 3, 1.36,
                            Weights(0.95, 0.85, 0.25),
                            Req("Sad", 0.62, 35, "compassionate"), Req("Hopeful", 0.70, 40), Req("Intrigued", 0.66, 35)),
                        Combo("staying_in_the_dark", "The player does not abandon Kaelia when she is guarded or vulnerable.", 5, 3, 1.32,
                            Weights(0.80, 1.05, 0.32),
                            Req("Vulnerable", 0.66, 35), Req("Safe", 0.70, 40), Req("Trusting", 0.70, 40)),
                        Combo("void_wonder_tenderness", "Curiosity, wonder, and tenderness contradict the lie that the Void is all there is.", 4, 3, 1.24,
                            Weights(0.86, 0.65, 0.42),
                            Req("Wondrous", 0.66, 35), Req("Tender", 0.70, 35), Req("Hopeful", 0.66, 35))
                    };
                case "nyxari":
                    return new List<StarEmotionComboRule>
                    {
                        Combo("identity_repair", "The player answers fragmentation with respect, meaning, and hope.", 5, 3, 1.34,
                            Weights(0.85, 1.0, 0.20),
                            Req("Vulnerable", 0.62, 35), Req("Respected", 0.70, 40), Req("Hopeful", 0.66, 35)),
                        Combo("fragment_integration", "The player patiently gathers broken pieces without cruelty or abandonment.", 5, 3, 1.32,
                            Weights(0.82, 1.05, 0.26),
                            Req("Intrigued", 0.62, 35), Req("Safe", 0.70, 40), Req("Comforted", 0.66, 35)),
                        Combo("truth_without_cruelty", "The player tells truth in a way that does not erase meaning.", 5, 3, 1.24,
                            Weights(0.70, 0.95, 0.12),
                            Req("Respected", 0.70, 40), Req("Trusting", 0.70, 40), Req("Determined", 0.62, 35))
                    };
                default:
                    return new List<StarEmotionComboRule>
                    {
                        Combo("trust_respect_combo", "Trust plus respect supports durable bond growth.", 4, 2, 1.15, Weights(0.70, 1.0, 0.10), Req("Trusting", 0.70, 40), Req("Respected", 0.70, 40))
                    };
            }
        }

        private static Dictionary<string, StarEmotionInfluenceRule> CreateBaselineEmotionInfluence()
        {
            Dictionary<string, StarEmotionInfluenceRule> rules = new Dictionary<string, StarEmotionInfluenceRule>(StringComparer.OrdinalIgnoreCase);

            Add(rules, Rule("Happy", "positive", 0.45, 0.35, 0.15, 0.05, "warmth"));
            Add(rules, Rule("Amused", "positive", 0.50, 0.38, 0.12, 0.06, "humor"));
            Add(rules, Rule("Playful", "positive", 0.55, 0.40, 0.15, 0.12, "play"));
            Add(rules, Rule("Comforted", "positive", 0.80, 0.55, 0.65, 0.12, "safety"));
            Add(rules, Rule("Safe", "positive", 0.85, 0.45, 0.85, 0.10, "safety"));
            Add(rules, Rule("Protected", "positive", 0.80, 0.45, 0.75, 0.08, "protection"));
            Add(rules, Rule("Trusting", "positive", 0.90, 0.45, 1.0, 0.12, "trust"));
            Add(rules, Rule("Respected", "positive", 0.85, 0.35, 0.90, 0.08, "respect"));
            Add(rules, Rule("Admiring", "positive", 0.65, 0.65, 0.35, 0.14, "admiration"));
            Add(rules, Rule("Affectionate", "positive", 0.75, 0.75, 0.35, 0.35, "affection", "romance"));
            Add(rules, Rule("Vulnerable", "contextual", 0.60, 0.35, 0.65, 0.12, "vulnerability"));
            Add(rules, Rule("Hopeful", "positive", 0.75, 0.65, 0.55, 0.15, "hope"));
            Add(rules, Rule("Wondrous", "positive", 0.65, 0.55, 0.30, 0.10, "wonder"));
            Add(rules, Rule("Intrigued", "positive", 0.55, 0.30, 0.28, 0.06, "curiosity"));
            Add(rules, Rule("Impassioned", "positive", 0.62, 0.58, 0.22, 0.28, "passion"));
            Add(rules, Rule("Determined", "positive", 0.55, 0.32, 0.45, 0.04, "resolve"));
            Add(rules, Rule("Concerned", "contextual", 0.45, 0.12, 0.22, 0.00, "discernment"));
            Add(rules, Rule("Confused", "contextual", 0.30, 0.05, 0.08, 0.00, "uncertainty"));
            Add(rules, Rule("Unsure", "contextual", 0.35, 0.06, 0.12, 0.00, "uncertainty"));
            Add(rules, Rule("Frustrated", "negative", 0.55, -0.20, -0.25, -0.05, "friction"));
            Add(rules, Rule("Angry", "negative", 0.65, -0.35, -0.40, -0.12, "anger"));
            Add(rules, Rule("Hurt", "negative", 0.82, -0.70, -0.65, -0.25, "wound"));
            Add(rules, Rule("Sad", "contextual", 0.40, 0.10, 0.04, 0.00, "sadness"));
            Add(rules, Rule("Melancholy", "contextual", 0.40, 0.08, 0.04, 0.00, "sadness"));
            Add(rules, Rule("Lonely", "contextual", 0.55, 0.18, 0.10, 0.02, "loneliness"));
            Add(rules, Rule("Guilty", "contextual", 0.42, 0.05, 0.10, 0.00, "repentance"));
            Add(rules, Rule("Suspicious", "negative", 0.66, -0.12, -0.55, -0.10, "guarded"));
            Add(rules, Rule("Flattered", "positive", 0.65, 0.45, 0.15, 0.55, "romance"));
            Add(rules, Rule("Attracted", "positive", 0.70, 0.34, 0.10, 0.90, "romance"));
            Add(rules, Rule("Tender", "positive", 0.78, 0.75, 0.45, 0.48, "romance", "care"));
            Add(rules, Rule("Yearning", "positive", 0.65, 0.30, 0.08, 0.85, "romance"));
            Add(rules, Rule("Jealous", "contextual", 0.55, -0.08, -0.18, 0.18, "romance", "risk"));

            Add(rules, Rule("Irritated", "negative", 0.60, -0.30, -0.20, -0.08, "boundary"));
            Add(rules, Rule("Disrespected", "negative", 0.90, -0.70, -0.95, -0.35, "boundary"));
            Add(rules, Rule("Pressured", "negative", 0.95, -0.60, -1.0, -0.45, "boundary"));
            Add(rules, Rule("CreepedOut", "negative", 1.0, -0.85, -1.0, -0.65, "boundary"));
            Add(rules, Rule("Bored", "negative", 0.40, -0.10, -0.04, -0.03, "low_effort"));
            Add(rules, Rule("Unsafe", "negative", 1.0, -1.0, -1.0, -0.75, "boundary", "danger"));

            return rules;
        }

        private static void ApplyStarOverrides(string starId, Dictionary<string, StarEmotionInfluenceRule> rules)
        {
            switch (starId)
            {
                case "elusiaa":
                    Override(rules, Rule("Comforted", "positive", 0.95, 0.80, 0.45, 0.20, "mercy", "comfort"));
                    Override(rules, Rule("Safe", "positive", 0.95, 0.60, 0.80, 0.14, "safety"));
                    Override(rules, Rule("Protected", "positive", 0.98, 0.72, 0.72, 0.10, "protect_weak"));
                    Override(rules, Rule("Trusting", "positive", 0.95, 0.62, 0.90, 0.16, "trust"));
                    Override(rules, Rule("Respected", "positive", 0.88, 0.42, 0.85, 0.08, "restrained_justice"));
                    Override(rules, Rule("Tender", "positive", 0.88, 0.86, 0.46, 0.34, "tender_light"));
                    Override(rules, Rule("Sad", "contextual", 0.65, 0.50, 0.25, 0.05, "compassionate_sadness"));
                    Override(rules, Rule("Concerned", "contextual", 0.70, 0.25, 0.45, 0.02, "discernment"));
                    Override(rules, Rule("Angry", "negative", 0.88, -0.50, -0.55, -0.16, "vengeance_risk"));
                    break;
                case "khavia":
                    Override(rules, Rule("Happy", "positive", 0.85, 0.70, 0.40, 0.10, "friendship"));
                    Override(rules, Rule("Playful", "positive", 0.82, 0.64, 0.35, 0.20, "friendship"));
                    Override(rules, Rule("Trusting", "positive", 0.96, 0.55, 1.0, 0.16, "loyalty"));
                    Override(rules, Rule("Comforted", "positive", 0.88, 0.70, 0.68, 0.18, "social_repair"));
                    Override(rules, Rule("Respected", "positive", 0.86, 0.50, 0.90, 0.10, "including_outsiders"));
                    Override(rules, Rule("Affectionate", "positive", 0.86, 0.88, 0.42, 0.42, "friend_to_lover"));
                    Override(rules, Rule("Tender", "positive", 0.84, 0.82, 0.44, 0.48, "friend_to_lover"));
                    break;
                case "auerlis":
                    Override(rules, Rule("Happy", "positive", 0.95, 0.80, 0.35, 0.18, "joy"));
                    Override(rules, Rule("Amused", "positive", 0.95, 0.78, 0.28, 0.16, "laughter"));
                    Override(rules, Rule("Playful", "positive", 0.92, 0.75, 0.30, 0.34, "playful_courage"));
                    Override(rules, Rule("Hopeful", "positive", 0.90, 0.72, 0.60, 0.16, "defiant_hope"));
                    Override(rules, Rule("Impassioned", "positive", 0.88, 0.70, 0.25, 0.55, "fire"));
                    Override(rules, Rule("Determined", "positive", 0.80, 0.45, 0.65, 0.08, "courage"));
                    Override(rules, Rule("Melancholy", "contextual", 0.65, -0.10, -0.08, -0.02, "despair_risk"));
                    break;
                case "maera":
                    Override(rules, Rule("Safe", "positive", 1.0, 0.62, 1.0, 0.24, "safe_attachment"));
                    Override(rules, Rule("Protected", "positive", 0.95, 0.75, 0.80, 0.18, "choosing_life"));
                    Override(rules, Rule("Respected", "positive", 1.0, 0.58, 1.05, 0.22, "boundaries"));
                    Override(rules, Rule("Trusting", "positive", 0.94, 0.55, 0.95, 0.24, "trust"));
                    Override(rules, Rule("Tender", "positive", 0.90, 0.86, 0.50, 0.60, "attachment"));
                    Override(rules, Rule("Vulnerable", "contextual", 0.88, 0.62, 0.80, 0.22, "staying_present"));
                    Override(rules, Rule("Attracted", "positive", 0.76, 0.38, 0.08, 0.92, "romance"));
                    Override(rules, Rule("Yearning", "contextual", 0.80, 0.22, -0.04, 0.76, "possession_risk"));
                    Override(rules, Rule("Jealous", "negative", 0.92, -0.30, -0.55, -0.12, "possession_risk"));
                    Override(rules, Rule("Pressured", "negative", 1.0, -0.78, -1.15, -0.65, "ownership_warning"));
                    break;
                case "kaelia":
                    Override(rules, Rule("Hopeful", "positive", 1.0, 0.88, 0.82, 0.24, "against_fatalism"));
                    Override(rules, Rule("Intrigued", "positive", 0.88, 0.50, 0.55, 0.12, "curiosity"));
                    Override(rules, Rule("Wondrous", "positive", 0.92, 0.72, 0.58, 0.18, "meaning"));
                    Override(rules, Rule("Safe", "positive", 0.95, 0.55, 1.0, 0.20, "staying"));
                    Override(rules, Rule("Trusting", "positive", 0.95, 0.50, 1.05, 0.26, "trust"));
                    Override(rules, Rule("Vulnerable", "contextual", 0.90, 0.65, 0.92, 0.28, "staying_in_dark"));
                    Override(rules, Rule("Sad", "contextual", 0.70, 0.35, 0.20, 0.04, "shared_darkness"));
                    Override(rules, Rule("Melancholy", "contextual", 0.68, 0.18, 0.14, 0.02, "fatalism_risk"));
                    Override(rules, Rule("Tender", "positive", 0.88, 0.78, 0.45, 0.55, "quiet_tenderness"));
                    Override(rules, Rule("Suspicious", "negative", 0.88, -0.25, -0.80, -0.12, "guarded"));
                    break;
                case "nyxari":
                    Override(rules, Rule("Safe", "positive", 0.98, 0.55, 1.0, 0.18, "identity_repair"));
                    Override(rules, Rule("Trusting", "positive", 0.98, 0.48, 1.05, 0.20, "integration"));
                    Override(rules, Rule("Vulnerable", "contextual", 0.92, 0.65, 0.92, 0.18, "fragmentation"));
                    Override(rules, Rule("Comforted", "positive", 0.90, 0.72, 0.70, 0.20, "not_abandoned"));
                    Override(rules, Rule("Respected", "positive", 0.95, 0.50, 1.0, 0.12, "truth_without_cruelty"));
                    Override(rules, Rule("Hopeful", "positive", 0.86, 0.72, 0.62, 0.12, "meaning"));
                    Override(rules, Rule("Intrigued", "positive", 0.78, 0.46, 0.46, 0.08, "meaning"));
                    Override(rules, Rule("Confused", "contextual", 0.66, 0.18, 0.22, 0.00, "identity_fragments"));
                    Override(rules, Rule("Hurt", "negative", 0.95, -0.88, -0.82, -0.35, "wounded_identity"));
                    break;
            }
        }

        private static void EnsureEveryKnownEmotionHasInfluence(string starId, List<StarEmotionInfluenceRule> existing)
        {
            Dictionary<string, StarEmotionInfluenceRule> merged = CreateBaselineEmotionInfluence();
            ApplyStarOverrides((starId ?? string.Empty).ToLowerInvariant(), merged);
            foreach (StarEmotionInfluenceRule rule in existing.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Emotion)))
            {
                merged[rule.Emotion] = rule;
            }
            existing.Clear();
            existing.AddRange(merged.Values.OrderBy(x => x.Emotion, StringComparer.OrdinalIgnoreCase));
        }

        private static void NormalizeStats(StarRelationshipStatsConfig stats)
        {
            if (stats.Heart == null) stats.Heart = new StarRelationshipStatRule { ScorePerPoint = 80, MaxPerTurn = 2 };
            if (stats.Trust == null) stats.Trust = new StarRelationshipStatRule { ScorePerPoint = 90, MaxPerTurn = 2 };
            if (stats.Romance == null) stats.Romance = new StarRelationshipStatRule { ScorePerPoint = 120, MaxPerTurn = 1 };
            NormalizeStat(stats.Heart, 80, 2);
            NormalizeStat(stats.Trust, 90, 2);
            NormalizeStat(stats.Romance, 120, 1);
        }

        private static void NormalizeStat(StarRelationshipStatRule rule, double scorePerPoint, int maxPerTurn)
        {
            if (rule.ScorePerPoint <= 0) rule.ScorePerPoint = scorePerPoint;
            if (rule.MaxPerTurn <= 0) rule.MaxPerTurn = maxPerTurn;
        }

        private static StarEmotionInfluenceRule Rule(string emotion, string polarity, double affectsStar, double heart, double trust, double romance, params string[] tags)
        {
            StarEmotionInfluenceRule rule = new StarEmotionInfluenceRule
            {
                Emotion = emotion,
                Polarity = polarity,
                AffectsStar = affectsStar,
                StatWeights = Weights(heart, trust, romance)
            };
            rule.Tags.AddRange(tags ?? Array.Empty<string>());
            return rule;
        }

        private static Dictionary<string, double> Weights(double heart, double trust, double romance)
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "heart", heart },
                { "trust", trust },
                { "romance", romance }
            };
        }

        private static StarEmotionComboRequirement Req(string emotion, double confidence, double score, string context = "")
        {
            return new StarEmotionComboRequirement
            {
                Emotion = emotion,
                MinConfidence = confidence,
                MinScore = score,
                Context = context
            };
        }

        private static StarEmotionComboRule Combo(string id, string description, int withinTurns, int minimumDistinct, double multiplier, Dictionary<string, double> weights, params StarEmotionComboRequirement[] requires)
        {
            StarEmotionComboRule rule = new StarEmotionComboRule
            {
                ComboId = id,
                Description = description,
                WithinTurns = withinTurns,
                MinimumDistinctEmotions = minimumDistinct,
                BonusMultiplier = multiplier,
                StatWeights = weights
            };
            rule.Requires.AddRange(requires ?? Array.Empty<StarEmotionComboRequirement>());
            return rule;
        }

        private static void Add(Dictionary<string, StarEmotionInfluenceRule> rules, StarEmotionInfluenceRule rule)
        {
            rules[rule.Emotion] = rule;
        }

        private static void Override(Dictionary<string, StarEmotionInfluenceRule> rules, StarEmotionInfluenceRule rule)
        {
            rules[rule.Emotion] = rule;
        }
    }
}
