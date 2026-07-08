using System;
using System.Collections.Generic;
using System.Linq;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Engine
{
    /// <summary>
    /// Converts current-turn emotion vectors into deterministic relationship changes.
    /// The LLM supplies emotional evidence; this class owns Heart/Trust/Romance movement using
    /// Star-specific JSON personality maps, stored vector history, combo rules, and point thresholds.
    /// </summary>
    public sealed class RelationshipDeltaCalculator : IRelationshipDeltaCalculator
    {
        // The local review model uses confidence as certainty, not as a minimum gate.
        // A direct, sweet line may come back as confidence 0.05-0.35 with a high score.
        // Treat any non-zero confidence as evidence, then use confidence * score as the deterministic weight.
        private const double RelationshipSignalConfidence = 0.01;
        private const double RelationshipSignalScore = 1;
        private const double RelationshipSignalWeightedScore = 0.01;
        private const double RelationshipDisplayWeightedScore = 1.0;
        private const double ComboSignalConfidence = 0.01;
        private const double ComboSignalScore = 1;
        private const double ComboRequirementWeightedScore = 1.0;

        public static readonly string[] NegativeEmotionIds = new[]
        {
            "Irritated", "Disrespected", "Pressured", "CreepedOut", "Bored", "Hurt", "Unsafe"
        };

        public RelationshipDelta Calculate(StarProfile profile, StarInteractionRequest request, StarInteractionResponse response, StarState currentState)
        {
            if (currentState == null) currentState = new StarState { StarId = request.StarId };
            NormalizeDeterministicState(currentState);
            StarRelationshipPersonalityMatrix.EnsureDeterministicRelationshipConfig(profile);

            List<EmotionVectorScore> positiveAll = ParseVector(response.EmotionalEffectVector, StarEmotionMatrix.StandardEmotionIds);
            List<EmotionVectorScore> negativeAll = ParseVector(response.NegativeEmotionVector, NegativeEmotionIds);

            // Relationship scoring should not silently collapse the vector to the top few emotions.
            // The model's score is the strength of the feeling and confidence is only a certainty multiplier,
            // so every non-zero positive vector entry is useful Star-specific evidence.
            List<EmotionVectorScore> positiveSignals = positiveAll
                .Where(HasAnyEmotionEvidence)
                .OrderByDescending(x => x.WeightedScore)
                .ThenByDescending(x => x.Score)
                .ToList();

            // Boundary/negative vectors still use a stronger floor before they can punish state, so tiny
            // accidental negative noise does not hurt the player.
            List<EmotionVectorScore> negativeSignals = negativeAll
                .Where(IsMeaningfulBoundaryEvidence)
                .OrderByDescending(x => x.WeightedScore)
                .ThenByDescending(x => x.Score)
                .ToList();

            RelationshipDelta delta = new RelationshipDelta();
            bool isInterstarMicroUpdate = IsInterstarMicroRequest(request);
            bool romanceGate = IsRomanceGateOpen(profile, request, currentState, positiveSignals);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            StarEmotionVectorHistoryEntry vectorHistory = BuildVectorHistoryEntry(profile, request, positiveAll, negativeAll, now);
            List<string> profileCombos = isInterstarMicroUpdate ? new List<string>() : DetectProfileCombos(profile, currentState, vectorHistory).ToList();
            List<string> legacySafetyCombos = isInterstarMicroUpdate ? new List<string>() : DetectLegacySafetyCombos(positiveSignals, negativeSignals, currentState).ToList();
            List<string> filteredModelCombos = isInterstarMicroUpdate ? new List<string>() : FilterModelCombosToConfiguredRules(response.DetectedCombos, profile, legacySafetyCombos).ToList();
            List<string> acceptedCombos = MergeAndLimitCombos(profileCombos.Concat(legacySafetyCombos).Concat(filteredModelCombos), currentState, maxCombos: 4).ToList();

            vectorHistory.ComboHits.Clear();
            vectorHistory.ComboHits.AddRange(acceptedCombos);

            bool enoughHistoryForPositivePoints = HasEnoughRelevantHistoryForPositivePoints(currentState, vectorHistory, acceptedCombos);
            ApplyStarSpecificEmotionScores(profile, currentState, vectorHistory, delta, romanceGate, enoughHistoryForPositivePoints, now);
            ApplyProfileComboScores(profile, currentState, vectorHistory, acceptedCombos, delta, romanceGate, now);
            ApplyMajorNegativeSafetyDeltas(delta, negativeSignals, request.GameContext.RomanceMode);
            ApplyBoundedSuggestedDelta(delta, response.SuggestedStateDeltas, positiveSignals, negativeSignals, acceptedCombos, romanceGate);
            ApplyRepeatDamping(delta, positiveSignals, negativeSignals, acceptedCombos, currentState, request.PlayerText);

            if (!isInterstarMicroUpdate || positiveSignals.Count > 0 || negativeSignals.Count > 0 || acceptedCombos.Count > 0)
            {
                ApplyEmotionalConnectionDelta(profile, request, currentState, delta, positiveSignals, negativeSignals, acceptedCombos);
            }
            ApplyActiveEffectors(currentState, delta, response);

            if (isInterstarMicroUpdate && positiveSignals.Count == 0 && negativeSignals.Count == 0 && acceptedCombos.Count == 0)
            {
                delta = RelationshipDelta.Zero();
            }

            // Non-allies can warm toward ally status but should not leap into deep romance/connection.
            if (!currentState.IsAlly && !string.Equals(profile.StarId, "Elusiaa", StringComparison.OrdinalIgnoreCase))
            {
                delta.Heart = Math.Min(delta.Heart, 1);
                delta.Trust = Math.Min(delta.Trust, 1);
                delta.Romance = Math.Min(delta.Romance, 0);
                delta.EmotionalConnectionPercent = currentState.IsMet
                    ? Math.Min(delta.EmotionalConnectionPercent, 0.10)
                    : Math.Min(delta.EmotionalConnectionPercent, 0);
            }

            Clamp(delta, profile.RelationshipStats);
            StoreVectorHistory(currentState, vectorHistory);

            response.DetectedCombos.Clear();
            response.DetectedCombos.AddRange(acceptedCombos);
            response.AcceptedPositiveEmotionSignals.Clear();
            response.AcceptedPositiveEmotionSignals.AddRange(vectorHistory.Entries
                .Where(x => x.Accepted && !string.Equals(x.Polarity, "negative", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.WeightedScore * Math.Max(0.05, x.AffectsStar))
                .ThenByDescending(x => x.Score)
                .Select(FormatHistoryItem));
            response.AcceptedNegativeEmotionSignals.Clear();
            response.AcceptedNegativeEmotionSignals.AddRange(vectorHistory.Entries
                .Where(x => x.Accepted && string.Equals(x.Polarity, "negative", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.WeightedScore * Math.Max(0.05, x.AffectsStar))
                .ThenByDescending(x => x.Score)
                .Select(FormatHistoryItem));
            response.AcceptedRelationshipCombos.Clear();
            response.AcceptedRelationshipCombos.AddRange(acceptedCombos);
            response.TopAffectedEmotions.Clear();
            response.TopAffectedEmotions.AddRange(BuildTopAffectedEmotions(response.EmotionalEffectVector, response.NegativeEmotionVector));
            response.DeterministicEmotionUpdateSummary = BuildSummary(currentState, vectorHistory, positiveSignals, negativeSignals, acceptedCombos, response.SuggestedStateDeltas, delta, romanceGate, enoughHistoryForPositivePoints);
            response.Debug.Notes.Add(response.DeterministicEmotionUpdateSummary);

            return delta;
        }

        public static List<TopAffectedEmotion> BuildTopAffectedEmotions(string? emotionalEffectVector, string? negativeEmotionVector, int maxCount = 0)
        {
            List<TopAffectedEmotion> all = new List<TopAffectedEmotion>();

            foreach (EmotionVectorScore signal in ParseVector(emotionalEffectVector, StarEmotionMatrix.StandardEmotionIds))
            {
                if (signal == null || string.IsNullOrWhiteSpace(signal.Id)) continue;
                if (signal.Confidence <= 0 && signal.Score <= 0) continue;
                all.Add(CreateTopAffectedEmotion(signal, "positive"));
            }

            foreach (EmotionVectorScore signal in ParseVector(negativeEmotionVector, NegativeEmotionIds))
            {
                if (signal == null || string.IsNullOrWhiteSpace(signal.Id)) continue;
                if (signal.Confidence <= 0 && signal.Score <= 0) continue;
                all.Add(CreateTopAffectedEmotion(signal, "negative"));
            }

            IEnumerable<TopAffectedEmotion> ordered = all
                .OrderByDescending(x => x.WeightedScore)
                .ThenByDescending(x => x.Score)
                .ThenByDescending(x => x.Confidence)
                .ThenBy(x => x.EmotionId, StringComparer.OrdinalIgnoreCase);

            if (maxCount > 0) ordered = ordered.Take(maxCount);
            return ordered.ToList();
        }

        public static List<EmotionVectorScore> ParseVector(string? vector, IReadOnlyList<string> ids)
        {
            List<EmotionVectorScore> result = new List<EmotionVectorScore>();
            if (ids == null || ids.Count == 0) return result;

            string[] parts = (vector ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < ids.Count; i++)
            {
                double confidence = 0;
                double score = 0;
                if (i < parts.Length)
                {
                    string[] pair = parts[i].Trim().Split(':');
                    if (pair.Length >= 2)
                    {
                        double.TryParse(pair[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out confidence);
                        double.TryParse(pair[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out score);
                    }
                }

                result.Add(new EmotionVectorScore
                {
                    Id = ids[i],
                    Confidence = Clamp01(confidence),
                    Score = Math.Max(0, Math.Min(100, score))
                });
            }

            return result;
        }

        private static TopAffectedEmotion CreateTopAffectedEmotion(EmotionVectorScore signal, string valence)
        {
            double weighted = signal.WeightedScore;
            bool accepted = IsDisplayWorthyEvidence(signal);
            string sign = string.Equals(valence, "negative", StringComparison.OrdinalIgnoreCase) ? "-" : "+";
            return new TopAffectedEmotion
            {
                EmotionId = signal.Id,
                Valence = valence,
                Confidence = signal.Confidence,
                Score = signal.Score,
                WeightedScore = weighted,
                AcceptedForStateUpdate = accepted,
                Summary = signal.Id + " " + sign + signal.Score.ToString("0") + " confidence " + signal.Confidence.ToString("0.00") + (accepted ? " accepted" : " observed")
            };
        }

        private static StarEmotionVectorHistoryEntry BuildVectorHistoryEntry(StarProfile profile, StarInteractionRequest request, List<EmotionVectorScore> positiveAll, List<EmotionVectorScore> negativeAll, DateTimeOffset now)
        {
            Dictionary<string, StarEmotionInfluenceRule> rules = BuildInfluenceLookup(profile);
            StarEmotionVectorHistoryEntry entry = new StarEmotionVectorHistoryEntry
            {
                CreatedAt = now,
                TurnId = now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + (request.StarId ?? string.Empty),
                RequestType = request.RequestType.ToString(),
                PlayerText = TruncateLocal(request.PlayerText, 500)
            };

            foreach (EmotionVectorScore signal in positiveAll.Where(x => x != null && (x.Confidence > 0 || x.Score > 0)))
            {
                AddHistoryItem(entry, signal, ResolveInfluence(rules, signal.Id, "positive"), "positive");
            }

            foreach (EmotionVectorScore signal in negativeAll.Where(x => x != null && (x.Confidence > 0 || x.Score > 0)))
            {
                AddHistoryItem(entry, signal, ResolveInfluence(rules, signal.Id, "negative"), "negative");
            }

            entry.Summary = BuildVectorEntrySummary(entry);
            return entry;
        }

        private static void AddHistoryItem(StarEmotionVectorHistoryEntry entry, EmotionVectorScore signal, StarEmotionInfluenceRule rule, string fallbackPolarity)
        {
            double weighted = signal.WeightedScore;
            double affectsStar = Clamp01(rule.AffectsStar <= 0 ? 0 : rule.AffectsStar);
            double starScore = weighted * affectsStar;
            double heart = starScore * GetWeight(rule, "heart");
            double trust = starScore * GetWeight(rule, "trust");
            double romance = starScore * GetWeight(rule, "romance");
            bool isNegativePolarity = string.Equals(string.IsNullOrWhiteSpace(rule.Polarity) ? fallbackPolarity : rule.Polarity, "negative", StringComparison.OrdinalIgnoreCase);
            bool hasStatEffect = (Math.Abs(heart) + Math.Abs(trust) + Math.Abs(romance)) >= 0.001;
            bool accepted = isNegativePolarity
                ? (IsMeaningfulBoundaryEvidence(signal) && affectsStar > 0 && hasStatEffect)
                : (HasAnyEmotionEvidence(signal) && affectsStar > 0 && hasStatEffect);

            entry.Entries.Add(new StarEmotionVectorHistoryItem
            {
                Emotion = signal.Id,
                Polarity = string.IsNullOrWhiteSpace(rule.Polarity) ? fallbackPolarity : rule.Polarity,
                Confidence = signal.Confidence,
                Score = signal.Score,
                WeightedScore = weighted,
                AffectsStar = affectsStar,
                Accepted = accepted,
                HeartScore = heart,
                TrustScore = trust,
                RomanceScore = romance,
                Reason = rule.Emotion + " affects this Star " + affectsStar.ToString("0.00") + " with H/T/R score " + heart.ToString("0.0") + "/" + trust.ToString("0.0") + "/" + romance.ToString("0.0")
            });
        }

        private static Dictionary<string, StarEmotionInfluenceRule> BuildInfluenceLookup(StarProfile profile)
        {
            StarRelationshipPersonalityMatrix.EnsureDeterministicRelationshipConfig(profile);
            return (profile.EmotionInfluence ?? new List<StarEmotionInfluenceRule>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Emotion))
                .GroupBy(x => x.Emotion, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        }

        private static StarEmotionInfluenceRule ResolveInfluence(Dictionary<string, StarEmotionInfluenceRule> rules, string emotion, string fallbackPolarity)
        {
            if (rules.TryGetValue(emotion ?? string.Empty, out StarEmotionInfluenceRule? rule)) return rule;
            return new StarEmotionInfluenceRule
            {
                Emotion = emotion ?? string.Empty,
                Polarity = fallbackPolarity,
                AffectsStar = 0.10,
                StatWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    { "heart", 0.0 },
                    { "trust", 0.0 },
                    { "romance", 0.0 }
                }
            };
        }

        private static bool HasEnoughRelevantHistoryForPositivePoints(StarState state, StarEmotionVectorHistoryEntry current, List<string> combos)
        {
            int currentDistinct = CountDistinctAcceptedEmotions(current, includeNegative: false);
            if (currentDistinct >= 1) return true;
            if (combos.Any(x => !IsNegativeCombo(x))) return true;

            int priorRelevantTurns = (state.EmotionVectorHistory ?? new List<StarEmotionVectorHistoryEntry>())
                .Take(16)
                .Count(x => x != null && CountDistinctAcceptedEmotions(x, includeNegative: false) > 0);
            return priorRelevantTurns + (currentDistinct > 0 ? 1 : 0) >= 1;
        }

        private static int CountDistinctAcceptedEmotions(StarEmotionVectorHistoryEntry entry, bool includeNegative)
        {
            if (entry == null || entry.Entries == null) return 0;
            return entry.Entries
                .Where(x => x != null && x.Accepted && (includeNegative || !string.Equals(x.Polarity, "negative", StringComparison.OrdinalIgnoreCase)))
                .Select(x => x.Emotion)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        private static void ApplyStarSpecificEmotionScores(StarProfile profile, StarState state, StarEmotionVectorHistoryEntry current, RelationshipDelta delta, bool romanceGate, bool allowPositiveCashOut, DateTimeOffset now)
        {
            foreach (StarEmotionVectorHistoryItem item in current.Entries.Where(x => x != null && x.Accepted))
            {
                ApplyOneStatScore(profile, state, "heart", item.HeartScore, delta, true, true, now, item.Reason);
                ApplyOneStatScore(profile, state, "trust", item.TrustScore, delta, true, true, now, item.Reason);
                ApplyOneStatScore(profile, state, "romance", item.RomanceScore, delta, romanceGate || item.RomanceScore < 0, romanceGate || item.RomanceScore < 0, now, item.Reason);
            }
        }

        private static void ApplyProfileComboScores(StarProfile profile, StarState state, StarEmotionVectorHistoryEntry current, List<string> acceptedCombos, RelationshipDelta delta, bool romanceGate, DateTimeOffset now)
        {
            if (acceptedCombos == null || acceptedCombos.Count == 0) return;
            Dictionary<string, StarEmotionComboRule> rules = (profile.ComboRules ?? new List<StarEmotionComboRule>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ComboId))
                .GroupBy(x => Slugify(x.ComboId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

            foreach (string combo in acceptedCombos)
            {
                string id = Slugify(combo);
                if (!rules.TryGetValue(id, out StarEmotionComboRule? rule))
                {
                    ApplyLegacyComboScore(profile, state, id, delta, romanceGate, now);
                    continue;
                }

                double baseScore = CalculateComboBaseScore(rule, state, current);
                double bonusScore = baseScore * (rule.BonusMultiplier <= 0 ? 1.0 : rule.BonusMultiplier);
                string reason = "combo " + id + " score " + bonusScore.ToString("0.0") + ": " + rule.Description;
                ApplyOneStatScore(profile, state, "heart", bonusScore * GetWeight(rule, "heart"), delta, true, true, now, reason);
                ApplyOneStatScore(profile, state, "trust", bonusScore * GetWeight(rule, "trust"), delta, true, true, now, reason);
                ApplyOneStatScore(profile, state, "romance", bonusScore * GetWeight(rule, "romance"), delta, true, romanceGate || IsNegativeCombo(id), now, reason);
            }
        }

        private static double CalculateComboBaseScore(StarEmotionComboRule rule, StarState state, StarEmotionVectorHistoryEntry current)
        {
            List<StarEmotionVectorHistoryEntry> window = BuildRecentWindow(state, current, rule.WithinTurns <= 0 ? 4 : rule.WithinTurns);
            List<double> scores = new List<double>();
            foreach (StarEmotionComboRequirement req in rule.Requires ?? new List<StarEmotionComboRequirement>())
            {
                StarEmotionVectorHistoryItem? match = FindRequirementMatch(window, req);
                if (match != null)
                {
                    scores.Add(match.WeightedScore * Math.Max(0.05, match.AffectsStar));
                }
            }

            return scores.Count == 0 ? 45 : Math.Max(35, scores.Average());
        }

        private static void ApplyLegacyComboScore(StarProfile profile, StarState state, string combo, RelationshipDelta delta, bool romanceGate, DateTimeOffset now)
        {
            string reason = "legacy deterministic combo " + combo;
            switch (combo)
            {
                case "trust_respect_combo":
                    ApplyOneStatScore(profile, state, "heart", 55, delta, true, true, now, reason);
                    ApplyOneStatScore(profile, state, "trust", 70, delta, true, true, now, reason);
                    break;
                case "affection_plus_hope":
                case "private_vulnerability_safety":
                    ApplyOneStatScore(profile, state, "heart", 70, delta, true, true, now, reason);
                    ApplyOneStatScore(profile, state, "romance", 40, delta, true, romanceGate, now, reason);
                    delta.BenaveilInfluence -= 1;
                    break;
                case "romantic_spark":
                    ApplyOneStatScore(profile, state, "romance", 75, delta, true, romanceGate, now, reason);
                    ApplyOneStatScore(profile, state, "heart", 45, delta, true, true, now, reason);
                    break;
                case "cruel_escalation":
                case "repeated_boundary_pressure":
                    delta.Heart -= 2;
                    delta.Trust -= 2;
                    delta.Romance -= 1;
                    delta.BenaveilInfluence += 2;
                    break;
                case "wounded_guard":
                    delta.Heart -= 1;
                    delta.Trust -= 1;
                    delta.BenaveilInfluence += 1;
                    break;
            }
        }

        private static void ApplyOneStatScore(StarProfile profile, StarState state, string stat, double score, RelationshipDelta delta, bool allowCashOut, bool gateOpen, DateTimeOffset now, string reason)
        {
            if (!gateOpen || Math.Abs(score) < 0.01) return;
            StarRelationshipStatRule rule = GetStatRule(profile.RelationshipStats, stat);
            StarRelationshipScoreAccumulator accumulator = GetAccumulator(state, stat);
            accumulator.Score += score;
            accumulator.LastScoreAdded = score;
            accumulator.LastUpdatedAt = now;
            accumulator.LastReason = reason;

            if (!allowCashOut && accumulator.Score > 0) return;

            int cap = Math.Max(1, rule.MaxPerTurn);
            double threshold = Math.Max(1, rule.ScorePerPoint);
            // Positive relationship evidence should visibly move the marks over time.
            // Authored JSON ScorePerPoint values remain the personality baseline, but vector
            // scoring uses a softer threshold so confidence ~0.40 signals can accumulate into
            // actual Heart/Trust/Romance progress instead of waiting forever.
            if (accumulator.Score > 0) threshold = Math.Max(8, threshold * 0.35);
            if (accumulator.Score >= threshold)
            {
                int points = Math.Min(cap, (int)Math.Floor(accumulator.Score / threshold));
                accumulator.Score -= points * threshold;
                accumulator.TotalPositivePoints += points;
                AddDelta(delta, stat, points);
            }
            else if (accumulator.Score <= -threshold)
            {
                int points = Math.Min(cap, (int)Math.Floor(Math.Abs(accumulator.Score) / threshold));
                accumulator.Score += points * threshold;
                accumulator.TotalNegativePoints += points;
                AddDelta(delta, stat, -points);
            }
        }

        private static StarRelationshipScoreAccumulator GetAccumulator(StarState state, string stat)
        {
            if (state.RelationshipScoreAccumulators == null) state.RelationshipScoreAccumulators = new List<StarRelationshipScoreAccumulator>();
            StarRelationshipScoreAccumulator? accumulator = state.RelationshipScoreAccumulators.FirstOrDefault(x => string.Equals(x.Stat, stat, StringComparison.OrdinalIgnoreCase));
            if (accumulator == null)
            {
                accumulator = new StarRelationshipScoreAccumulator { Stat = stat };
                state.RelationshipScoreAccumulators.Add(accumulator);
            }
            return accumulator;
        }

        private static StarRelationshipStatRule GetStatRule(StarRelationshipStatsConfig stats, string stat)
        {
            if (stats == null) stats = new StarRelationshipStatsConfig();
            switch ((stat ?? string.Empty).ToLowerInvariant())
            {
                case "trust": return stats.Trust ?? new StarRelationshipStatRule { ScorePerPoint = 90, MaxPerTurn = 2 };
                case "romance": return stats.Romance ?? new StarRelationshipStatRule { ScorePerPoint = 120, MaxPerTurn = 1 };
                default: return stats.Heart ?? new StarRelationshipStatRule { ScorePerPoint = 80, MaxPerTurn = 2 };
            }
        }

        private static void AddDelta(RelationshipDelta delta, string stat, int points)
        {
            switch ((stat ?? string.Empty).ToLowerInvariant())
            {
                case "trust": delta.Trust += points; break;
                case "romance": delta.Romance += points; break;
                default: delta.Heart += points; break;
            }
        }

        private static IEnumerable<string> DetectProfileCombos(StarProfile profile, StarState state, StarEmotionVectorHistoryEntry current)
        {
            foreach (StarEmotionComboRule rule in profile.ComboRules ?? new List<StarEmotionComboRule>())
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.ComboId)) continue;
                string id = Slugify(rule.ComboId);
                if (IsComboCoolingDown(id, rule, state)) continue;
                List<StarEmotionVectorHistoryEntry> window = BuildRecentWindow(state, current, rule.WithinTurns <= 0 ? 4 : rule.WithinTurns);
                int requiredMatches = 0;
                foreach (StarEmotionComboRequirement req in rule.Requires ?? new List<StarEmotionComboRequirement>())
                {
                    if (FindRequirementMatch(window, req) != null) requiredMatches += 1;
                }

                int distinct = window
                    .SelectMany(x => x.Entries ?? new List<StarEmotionVectorHistoryItem>())
                    .Where(x => x != null && x.Accepted)
                    .Select(x => x.Emotion)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                bool matchedRequirements = (rule.Requires == null || rule.Requires.Count == 0) ? distinct >= Math.Max(2, rule.MinimumDistinctEmotions) : requiredMatches >= rule.Requires.Count;
                if (matchedRequirements && distinct >= Math.Max(1, rule.MinimumDistinctEmotions)) yield return id;
            }
        }

        private static List<StarEmotionVectorHistoryEntry> BuildRecentWindow(StarState state, StarEmotionVectorHistoryEntry current, int turns)
        {
            List<StarEmotionVectorHistoryEntry> window = new List<StarEmotionVectorHistoryEntry> { current };
            window.AddRange((state.EmotionVectorHistory ?? new List<StarEmotionVectorHistoryEntry>()).Where(x => x != null).Take(Math.Max(1, turns - 1)));
            return window;
        }

        private static StarEmotionVectorHistoryItem? FindRequirementMatch(List<StarEmotionVectorHistoryEntry> window, StarEmotionComboRequirement req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Emotion)) return null;
            return window
                .SelectMany(x => x.Entries ?? new List<StarEmotionVectorHistoryItem>())
                .Where(x => x != null && x.Accepted)
                .Where(x => string.Equals(x.Emotion, req.Emotion, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.IsNullOrWhiteSpace(req.Polarity) || string.Equals(x.Polarity, req.Polarity, StringComparison.OrdinalIgnoreCase) || string.Equals(req.Polarity, "contextual", StringComparison.OrdinalIgnoreCase))
                .Where(x => RequirementAllowsSignal(x, req))
                .OrderByDescending(x => x.WeightedScore * Math.Max(0.05, x.AffectsStar))
                .FirstOrDefault();
        }

        private static bool IsComboCoolingDown(string id, StarEmotionComboRule rule, StarState state)
        {
            if (state.RelationshipCombos == null || rule.CooldownTurns <= 0) return false;
            RelationshipComboState? prior = state.RelationshipCombos.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (prior == null || prior.LastTriggeredAt == default(DateTimeOffset)) return false;
            int recentEntries = (state.EmotionUpdateHistory ?? new List<EmotionUpdateHistoryEntry>())
                .Take(rule.CooldownTurns)
                .Count(x => x.CreatedAt <= prior.LastTriggeredAt);
            return prior.Count > 0 && recentEntries > 0;
        }

        private static IEnumerable<string> DetectLegacySafetyCombos(List<EmotionVectorScore> positive, List<EmotionVectorScore> negative, StarState state)
        {
            bool Has(string id) => positive.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase) && IsComboWorthyEvidence(x));
            bool Neg(string id) => negative.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase) && IsMeaningfulBoundaryEvidence(x));

            if ((Has("Trusting") || Has("Safe")) && (Has("Respected") || Has("Protected"))) yield return "trust_respect_combo";
            if ((Has("Affectionate") || Has("Tender")) && (Has("Hopeful") || Has("Comforted"))) yield return "affection_plus_hope";
            if (Has("Attracted") && (Has("Flattered") || Has("Tender") || Has("Yearning"))) yield return "romantic_spark";
            if ((Has("Vulnerable") || Has("Tender")) && (Has("Safe") || Has("Trusting"))) yield return "private_vulnerability_safety";
            if ((Neg("Disrespected") || Neg("Pressured") || Neg("CreepedOut")) && state.HurtSuccessionCount > 0) yield return "repeated_boundary_pressure";
            if (Neg("Hurt") || Neg("Unsafe")) yield return "wounded_guard";
        }

        private static IEnumerable<string> FilterModelCombosToConfiguredRules(IEnumerable<string> modelCombos, StarProfile profile, List<string> legacySafetyCombos)
        {
            HashSet<string> allowed = new HashSet<string>((profile.ComboRules ?? new List<StarEmotionComboRule>()).Select(x => Slugify(x.ComboId)), StringComparer.OrdinalIgnoreCase);
            foreach (string combo in legacySafetyCombos ?? new List<string>()) allowed.Add(Slugify(combo));
            foreach (string combo in modelCombos ?? Enumerable.Empty<string>())
            {
                string id = Slugify(combo);
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (allowed.Contains(id) || IsNegativeCombo(id)) yield return id;
            }
        }

        private static IEnumerable<string> MergeAndLimitCombos(IEnumerable<string> combos, StarState state, int maxCombos)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string combo in combos ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(combo)) continue;
                string id = Slugify(combo);
                if (!seen.Add(id)) continue;
                RelationshipComboState? prior = state.RelationshipCombos == null ? null : state.RelationshipCombos.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                bool negativeCombo = IsNegativeCombo(id);
                if (!negativeCombo && prior != null && prior.Count >= 3 && prior.LastTriggeredAt > DateTimeOffset.UtcNow.AddMinutes(-10))
                {
                    continue;
                }
                yield return id;
                if (--maxCombos <= 0) yield break;
            }
        }

        private static bool HasAnyEmotionEvidence(EmotionVectorScore signal)
        {
            return signal != null && signal.Confidence > 0 && signal.Score > 0;
        }

        private static bool IsScorableEvidence(EmotionVectorScore signal)
        {
            return HasAnyEmotionEvidence(signal) && signal.WeightedScore >= RelationshipSignalWeightedScore;
        }

        private static bool IsDisplayWorthyEvidence(EmotionVectorScore signal)
        {
            return IsScorableEvidence(signal) || (HasAnyEmotionEvidence(signal) && signal.WeightedScore >= RelationshipDisplayWeightedScore);
        }

        private static bool IsMeaningfulRelationshipEvidence(EmotionVectorScore signal)
        {
            return HasAnyEmotionEvidence(signal);
        }

        private static bool IsComboWorthyEvidence(EmotionVectorScore signal)
        {
            return HasAnyEmotionEvidence(signal);
        }

        private static bool IsMeaningfulBoundaryEvidence(EmotionVectorScore signal)
        {
            return HasAnyEmotionEvidence(signal) && (signal.WeightedScore >= 8.0 || (signal.Confidence >= 0.35 && signal.Score >= 35));
        }

        private static bool RequirementAllowsSignal(StarEmotionVectorHistoryItem item, StarEmotionComboRequirement req)
        {
            if (item == null || req == null) return false;
            if (item.Confidence <= 0 || item.Score <= 0) return false;
            double minConfidence = req.MinConfidence <= 0 ? ComboSignalConfidence : Math.Min(req.MinConfidence, 0.10);
            double minScore = req.MinScore <= 0 ? ComboSignalScore : Math.Max(8.0, req.MinScore * 0.35);
            double minWeighted = Math.Max(ComboRequirementWeightedScore, (req.MinScore <= 0 ? 25.0 : req.MinScore) * 0.025);
            return item.Confidence >= minConfidence && item.Score >= minScore && item.WeightedScore >= minWeighted;
        }

        private static bool IsIntimateEvidence(EmotionVectorScore signal)
        {
            return HasAnyEmotionEvidence(signal) && signal.Score >= 10;
        }

        private static bool IsPersonalityMatchEvidence(EmotionVectorScore signal)
        {
            return HasAnyEmotionEvidence(signal) && signal.Score >= 10;
        }

        private static bool IsRomanticEvidence(EmotionVectorScore signal)
        {
            return HasAnyEmotionEvidence(signal) && signal.Score >= 10;
        }

        private static void ApplyMajorNegativeSafetyDeltas(RelationshipDelta delta, List<EmotionVectorScore> negative, bool romanceMode)
        {
            foreach (EmotionVectorScore signal in negative)
            {
                switch (signal.Id)
                {
                    case "Disrespected":
                    case "Pressured":
                    case "CreepedOut":
                    case "Unsafe":
                        delta.Trust -= 1;
                        delta.Heart -= 1;
                        if (romanceMode) delta.Romance -= 1;
                        break;
                    case "Hurt":
                        delta.Heart -= 1;
                        delta.Trust -= 1;
                        if (romanceMode && signal.Score >= 90) delta.Romance -= 1;
                        break;
                    case "Irritated":
                        if (signal.Score >= 90) delta.Heart -= 1;
                        break;
                    case "Bored":
                        if (signal.Score >= 92) delta.Heart -= 1;
                        break;
                }

                if (signal.Id == "CreepedOut" || signal.Id == "Unsafe" || signal.Id == "Hurt") delta.BenaveilInfluence += 1;
            }
        }

        private static void ApplyBoundedSuggestedDelta(RelationshipDelta delta, RelationshipDelta suggestion, List<EmotionVectorScore> positive, List<EmotionVectorScore> negative, List<string> combos, bool romanceGate)
        {
            // Heart/Trust/Romance are intentionally not accepted from the model here. The LLM provides vectors and memory;
            // deterministic Star JSON, accumulators, and combo rules decide the relationship points.
            bool hasPositiveSupport = positive.Count > 0 || combos.Any(x => !IsNegativeCombo(x));
            bool hasNegativeSupport = negative.Count > 0 || combos.Any(IsNegativeCombo);

            if (hasPositiveSupport)
            {
                delta.BenaveilInfluence += Math.Max(-1, Math.Min(0, suggestion.BenaveilInfluence));
            }

            if (hasNegativeSupport)
            {
                delta.BenaveilInfluence += Math.Max(0, Math.Min(1, suggestion.BenaveilInfluence));
            }

            if (hasPositiveSupport && suggestion.EmotionalConnectionPercent > 0)
            {
                delta.EmotionalConnectionPercent += Math.Min(0.20, suggestion.EmotionalConnectionPercent);
            }
            if ((hasNegativeSupport || positive.Count == 0) && suggestion.EmotionalConnectionPercent < 0)
            {
                delta.EmotionalConnectionPercent += Math.Max(-0.60, suggestion.EmotionalConnectionPercent);
            }
        }

        private static void ApplyRepeatDamping(RelationshipDelta delta, List<EmotionVectorScore> positive, List<EmotionVectorScore> negative, List<string> combos, StarState state, string? playerText)
        {
            List<EmotionUpdateHistoryEntry> recent = (state.EmotionUpdateHistory ?? new List<EmotionUpdateHistoryEntry>()).Take(3).ToList();
            if (recent.Count == 0) return;

            // Do not punish a sequence of sincere, varied romantic / ally-building lines just because
            // the same good emotions are appearing. Only damp obvious low-effort repetition.
            bool highEffort = LooksLikeHighEffortIntimateSpeech(playerText ?? string.Empty);
            int repeatedPositiveSignals = positive.Count(signal => recent.Any(entry => entry.AcceptedPositiveSignals.Any(x => x.StartsWith(signal.Id + "(", StringComparison.OrdinalIgnoreCase))));
            int repeatedPositiveCombos = combos.Count(combo => !IsNegativeCombo(combo) && recent.Any(entry => entry.AcceptedCombos.Any(x => string.Equals(x, combo, StringComparison.OrdinalIgnoreCase))));
            if (!highEffort && repeatedPositiveSignals + repeatedPositiveCombos >= 4)
            {
                delta.EmotionalConnectionPercent = Math.Min(delta.EmotionalConnectionPercent, 0.35);
            }
        }

        private static void ApplyEmotionalConnectionDelta(StarProfile profile, StarInteractionRequest request, StarState state, RelationshipDelta delta, List<EmotionVectorScore> positive, List<EmotionVectorScore> negative, List<string> combos)
        {
            double connection = state.EmotionalConnectionPercent;
            bool isAlly = state.IsAlly || string.Equals(profile.StarId, "Elusiaa", StringComparison.OrdinalIgnoreCase);
            bool hasMajorNegative = negative.Any(x => x.Score >= 70 && x.Confidence >= 0.60 && (x.Id == "Disrespected" || x.Id == "Pressured" || x.Id == "CreepedOut" || x.Id == "Hurt" || x.Id == "Unsafe"));
            bool boringOrIrritated = negative.Any(x => x.Score >= 75 && x.Confidence >= 0.60 && (x.Id == "Bored" || x.Id == "Irritated"));
            bool hasIntimatePositive = positive.Count(x => IsIntimateConnectionSignal(x.Id) && IsIntimateEvidence(x)) >= 1;
            bool hasMultipleIntimate = positive.Count(x => IsIntimateConnectionSignal(x.Id) && IsIntimateEvidence(x)) >= 2;
            bool hasPersonalityMatch = positive.Any(x => IsPersonalityMatchedConnectionSignal(profile.StarId, x.Id) && IsPersonalityMatchEvidence(x));
            bool hasConnectionCombo = combos.Any(x => x.Contains("romantic") || x.Contains("affection") || x.Contains("vulnerability") || x.Contains("trust") || x.Contains("tender") || x.Contains("safe"));
            bool strongPlayerEffort = LooksLikeHighEffortIntimateSpeech(request.PlayerText ?? string.Empty);

            if (hasMajorNegative)
            {
                delta.EmotionalConnectionPercent -= 1.25 + Math.Min(1.25, negative.Count * 0.45);
                return;
            }

            if (boringOrIrritated)
            {
                delta.EmotionalConnectionPercent -= 0.50;
                return;
            }

            if (!isAlly)
            {
                bool metWarmSignal = state.IsMet && positive.Any(IsMeaningfulRelationshipEvidence);
                if (metWarmSignal)
                {
                    delta.EmotionalConnectionPercent += 0.05;
                    return;
                }
                if (connection > 0) delta.EmotionalConnectionPercent -= 0.10;
                return;
            }

            if (hasIntimatePositive && hasPersonalityMatch && (hasMultipleIntimate || hasConnectionCombo || strongPlayerEffort || state.Romance >= 8 || state.Trust >= 10))
            {
                double gain = 0.75;
                if (hasMultipleIntimate) gain += 0.40;
                if (hasConnectionCombo) gain += 0.35;
                if (strongPlayerEffort) gain += 0.35;
                if (positive.Any(x => x.Id == "Attracted" || x.Id == "Yearning")) gain += 0.25;
                if (state.PositiveSuccessionCount >= 2 || state.RomanticSuccessionCount >= 1) gain += 0.25;
                delta.EmotionalConnectionPercent += Math.Min(2.25, gain);
                return;
            }

            bool hasAnyGoodAttention = positive.Any(IsMeaningfulRelationshipEvidence);
            bool hasHighGoodAttention = positive.Any(x => x.WeightedScore >= 12.0 || (x.Score >= 55 && x.Confidence >= 0.20));
            bool playerActuallySaidSomething = !string.IsNullOrWhiteSpace(request.PlayerText);

            if (isAlly && hasHighGoodAttention)
            {
                delta.EmotionalConnectionPercent += positive.Count(x => x.WeightedScore >= 12.0 || (x.Score >= 55 && x.Confidence >= 0.20)) >= 2 ? 0.55 : 0.35;
                return;
            }

            if (isAlly && hasAnyGoodAttention)
            {
                delta.EmotionalConnectionPercent += 0.22;
                return;
            }

            if (isAlly && playerActuallySaidSomething && !negative.Any())
            {
                delta.EmotionalConnectionPercent += 0.05;
                return;
            }

            if (connection > 0 && connection <= 50)
            {
                delta.EmotionalConnectionPercent -= 0.10;
            }
        }

        private static void ApplyActiveEffectors(StarState state, RelationshipDelta delta, StarInteractionResponse response)
        {
            if (state.ActiveEffectors == null || state.ActiveEffectors.Count == 0) return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (StarRelationshipEffector effector in state.ActiveEffectors.Where(x => x != null && !x.IsExpired(now)).ToList())
            {
                double beforeConnection = delta.EmotionalConnectionPercent;

                if (delta.EmotionalConnectionPercent > 0)
                {
                    if (effector.SuppressPassiveConnectionGain && delta.EmotionalConnectionPercent <= 0.16)
                    {
                        delta.EmotionalConnectionPercent = 0;
                    }
                    else
                    {
                        delta.EmotionalConnectionPercent *= effector.ConnectionGainMultiplier <= 0 ? 1.0 : effector.ConnectionGainMultiplier;
                    }
                }
                else if (delta.EmotionalConnectionPercent < 0)
                {
                    delta.EmotionalConnectionPercent *= effector.ConnectionLossMultiplier <= 0 ? 1.0 : effector.ConnectionLossMultiplier;
                }

                delta.Heart += effector.HeartDeltaPerTurn;
                delta.Trust += effector.TrustDeltaPerTurn;
                delta.Romance += effector.RomanceDeltaPerTurn;
                delta.BenaveilInfluence += effector.BenaveilInfluenceDeltaPerTurn;
                effector.AppliedTurns += 1;

                response.Debug.Notes.Add("Active deterministic effector " + effector.Id + " applied: connection " + beforeConnection.ToString("0.00") + " -> " + delta.EmotionalConnectionPercent.ToString("0.00") + ", H/T/R/B +=" + effector.HeartDeltaPerTurn + "/" + effector.TrustDeltaPerTurn + "/" + effector.RomanceDeltaPerTurn + "/" + effector.BenaveilInfluenceDeltaPerTurn + ".");
            }
        }

        private static bool IsInterstarMicroRequest(StarInteractionRequest request)
        {
            return request != null
                && request.GameContext != null
                && request.GameContext.Flags != null
                && request.GameContext.Flags.TryGetValue("interstarMicroUpdate", out string micro)
                && string.Equals(micro, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRomanceGateOpen(StarProfile profile, StarInteractionRequest request, StarState state, List<EmotionVectorScore> positive)
        {
            if (!request.GameContext.RomanceMode) return false;
            if (!state.IsAlly && !string.Equals(profile.StarId, "Elusiaa", StringComparison.OrdinalIgnoreCase)) return false;
            string text = (request.PlayerText ?? string.Empty).ToLowerInvariant();
            bool hasHighRomanceEmotion = positive.Any(x => IsRomanceSignal(x.Id) && IsRomanticEvidence(x));
            bool earnedByWords = ContainsAny(text, "girlfriend", "be my girlfriend", "go out", "date", "poem", "poetry", "beautiful", "beauty", "attracted", "attraction", "gentle", "safe", "trust", "song", "music", "kiss", "touch", "cat hair", "fur", "warm", "miracle", "stars", "heart", "light", "eyes", "precious", "protecting", "sanctuary", "guard", "stand beside", "beside you", "cherish", "faithful", "falling for", "drawn to you");
            bool established = state.IsGirlfriend || state.Romance > 0 || state.Trust >= 4 || state.Heart >= 4 || state.PositiveSuccessionCount >= 2 || state.EmotionalConnectionPercent >= 8;
            bool kaeliaCatHook = string.Equals(profile.StarId, "Kaelia", StringComparison.OrdinalIgnoreCase) && ContainsAny(text, "cat", "cat hair", "fur", "purr", "tail", "ears");
            return hasHighRomanceEmotion || earnedByWords || established || kaeliaCatHook;
        }

        private static bool IsRomanceSignal(string id)
        {
            return id == "Flattered" || id == "Attracted" || id == "Tender" || id == "Yearning" || id == "Jealous" || id == "Affectionate";
        }

        private static bool IsIntimateConnectionSignal(string id)
        {
            return id == "Affectionate" || id == "Vulnerable" || id == "Trusting" || id == "Safe" || id == "Protected" || id == "Tender" || id == "Flattered" || id == "Attracted" || id == "Yearning" || id == "Hopeful";
        }

        private static bool IsPersonalityMatchedConnectionSignal(string starId, string id)
        {
            switch ((starId ?? string.Empty).ToLowerInvariant())
            {
                case "elusiaa":
                    return id == "Comforted" || id == "Safe" || id == "Protected" || id == "Trusting" || id == "Tender" || id == "Hopeful" || id == "Wondrous" || id == "Affectionate" || id == "Sad" || id == "Concerned";
                case "kaelia":
                    return id == "Intrigued" || id == "Wondrous" || id == "Hopeful" || id == "Respected" || id == "Trusting" || id == "Tender" || id == "Flattered" || id == "Attracted" || id == "Yearning" || id == "Vulnerable";
                case "khavia":
                    return id == "Happy" || id == "Playful" || id == "Comforted" || id == "Safe" || id == "Trusting" || id == "Affectionate" || id == "Tender" || id == "Respected";
                case "auerlis":
                    return id == "Happy" || id == "Amused" || id == "Playful" || id == "Hopeful" || id == "Admiring" || id == "Affectionate" || id == "Impassioned" || id == "Determined";
                case "maera":
                    return id == "Protected" || id == "Trusting" || id == "Respected" || id == "Tender" || id == "Attracted" || id == "Yearning" || id == "Vulnerable" || id == "Safe";
                case "nyxari":
                    return id == "Safe" || id == "Trusting" || id == "Vulnerable" || id == "Comforted" || id == "Tender" || id == "Hopeful" || id == "Intrigued" || id == "Respected";
                default:
                    return IsIntimateConnectionSignal(id);
            }
        }

        private static bool LooksLikeHighEffortIntimateSpeech(string text)
        {
            string t = (text ?? string.Empty).ToLowerInvariant();
            int length = t.Count(char.IsLetter);
            if (ContainsAny(t, "poem", "poetry", "your eyes", "your heart", "miracle", "beautiful", "beauty", "stars", "sea", "storm", "safe", "trust", "i choose", "i won't leave", "i will stay", "stand beside", "beside you", "precious", "sanctuary", "guard", "protect", "cherish", "faithful", "cat hair", "fur", "purr")) return true;
            return length >= 70 && (ContainsAny(t, "you", "your") && ContainsAny(t, "heart", "light", "dark", "safe", "trust", "beautiful", "beauty", "gentle", "hope", "mercy", "truth", "heal"));
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            return needles.Any(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void StoreVectorHistory(StarState state, StarEmotionVectorHistoryEntry entry)
        {
            if (entry == null) return;
            if (state.EmotionVectorHistory == null) state.EmotionVectorHistory = new List<StarEmotionVectorHistoryEntry>();
            if (!entry.Entries.Any(x => x.Accepted)) return;
            state.EmotionVectorHistory.Insert(0, entry);
            Trim(state.EmotionVectorHistory, 256);
        }

        private static string BuildSummary(StarState state, StarEmotionVectorHistoryEntry vectorHistory, List<EmotionVectorScore> positive, List<EmotionVectorScore> negative, List<string> combos, RelationshipDelta suggested, RelationshipDelta actual, bool romanceGate, bool enoughHistory)
        {
            string pos = positive.Count == 0 ? "none" : string.Join(", ", positive.Take(4).Select(x => x.ToString()));
            string neg = negative.Count == 0 ? "none" : string.Join(", ", negative.Take(4).Select(x => x.ToString()));
            string combo = combos.Count == 0 ? "none" : string.Join(", ", combos);
            string acceptedVector = vectorHistory.Entries.Count(x => x.Accepted).ToString(System.Globalization.CultureInfo.InvariantCulture);
            double projectedConnection = Math.Max(0, Math.Min(100, state.EmotionalConnectionPercent + actual.EmotionalConnectionPercent));
            bool projectedReady = (state.IsGirlfriend || (state.IsAlly && projectedConnection >= 25 && state.Heart + actual.Heart >= 6 && state.Trust + actual.Trust >= 4 && state.Romance + actual.Romance >= 3));
            int activeEffectorCount = state.ActiveEffectors == null ? 0 : state.ActiveEffectors.Count(x => x != null && !x.IsExpired(DateTimeOffset.UtcNow));
            string accumulatorSummary = BuildAccumulatorSummary(state);
            return "Deterministic emotion update: skill=star_specific_vector_scoring; met=" + state.IsMet + "; ally=" + state.IsAlly + "; activeEffectors=" + activeEffectorCount
                + "; connection=" + state.EmotionalConnectionPercent.ToString("0.0") + "%->" + projectedConnection.ToString("0.0") + "%"
                + "; girlfriendReadyAfter=" + projectedReady
                + "; highVectorEntries=" + acceptedVector
                + "; enoughRelevantHistoryForPositivePoints=" + enoughHistory
                + "; positiveSignals=" + pos
                + "; negativeSignals=" + neg
                + "; acceptedCombos=" + combo
                + "; accumulators=" + accumulatorSummary
                + "; romanceGate=" + romanceGate
                + "; modelSuggestedIgnoredForHTR=(H" + suggested.Heart + ",T" + suggested.Trust + ",R" + suggested.Romance + ")"
                + "; actualDelta=(H" + actual.Heart + ",T" + actual.Trust + ",R" + actual.Romance + ",B" + actual.BenaveilInfluence + ",C" + actual.EmotionalConnectionPercent.ToString("0.00") + ").";
        }

        private static string BuildVectorEntrySummary(StarEmotionVectorHistoryEntry entry)
        {
            List<string> top = entry.Entries
                .Where(x => x.Accepted)
                .OrderByDescending(x => Math.Abs(x.HeartScore) + Math.Abs(x.TrustScore) + Math.Abs(x.RomanceScore))
                .Take(5)
                .Select(FormatHistoryItem)
                .ToList();
            return top.Count == 0 ? "No non-zero Star-specific emotion entries accepted." : "Accepted Star-specific emotions: " + string.Join(", ", top);
        }

        private static string FormatHistoryItem(StarEmotionVectorHistoryItem item)
        {
            return item.Emotion + "(" + item.Confidence.ToString("0.00") + ":" + item.Score.ToString("0") + ",star=" + item.AffectsStar.ToString("0.00") + ",H/T/R=" + item.HeartScore.ToString("0.0") + "/" + item.TrustScore.ToString("0.0") + "/" + item.RomanceScore.ToString("0.0") + ")";
        }

        private static string BuildAccumulatorSummary(StarState state)
        {
            if (state.RelationshipScoreAccumulators == null || state.RelationshipScoreAccumulators.Count == 0) return "none";
            return string.Join(", ", state.RelationshipScoreAccumulators
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Stat))
                .OrderBy(x => x.Stat)
                .Select(x => x.Stat + "=" + x.Score.ToString("0.0")));
        }

        private static double GetWeight(StarEmotionInfluenceRule rule, string key)
        {
            if (rule == null || rule.StatWeights == null) return 0;
            return rule.StatWeights.TryGetValue(key, out double value) ? value : 0;
        }

        private static double GetWeight(StarEmotionComboRule rule, string key)
        {
            if (rule == null || rule.StatWeights == null) return 0;
            return rule.StatWeights.TryGetValue(key, out double value) ? value : 0;
        }

        private static bool IsNegativeCombo(string combo)
        {
            string id = Slugify(combo);
            return id.Contains("hurt") || id.Contains("wounded") || id.Contains("cruel") || id.Contains("pressure") || id.Contains("guard") || id.Contains("warning") || id.Contains("unsafe") || id.Contains("possessive");
        }

        private static void NormalizeDeterministicState(StarState state)
        {
            if (state.ActiveEffectors == null) state.ActiveEffectors = new List<StarRelationshipEffector>();
            if (state.EmotionUpdateHistory == null) state.EmotionUpdateHistory = new List<EmotionUpdateHistoryEntry>();
            if (state.EmotionVectorHistory == null) state.EmotionVectorHistory = new List<StarEmotionVectorHistoryEntry>();
            if (state.RelationshipScoreAccumulators == null) state.RelationshipScoreAccumulators = new List<StarRelationshipScoreAccumulator>();
        }

        private static string Slugify(string value)
        {
            string raw = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (raw.Length == 0) return string.Empty;
            char[] chars = raw.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            string slug = new string(chars).Trim('_');
            while (slug.Contains("__")) slug = slug.Replace("__", "_");
            return slug;
        }

        private static double Clamp01(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        private static void Clamp(RelationshipDelta delta, StarRelationshipStatsConfig stats)
        {
            int heartCap = Math.Max(2, stats?.Heart?.MaxPerTurn ?? 2);
            int trustCap = Math.Max(2, stats?.Trust?.MaxPerTurn ?? 2);
            int romanceCap = Math.Max(1, stats?.Romance?.MaxPerTurn ?? 1);
            delta.Heart = Math.Max(-4, Math.Min(heartCap, delta.Heart));
            delta.Trust = Math.Max(-4, Math.Min(trustCap, delta.Trust));
            delta.Romance = Math.Max(-3, Math.Min(romanceCap, delta.Romance));
            delta.BenaveilInfluence = Math.Max(-3, Math.Min(3, delta.BenaveilInfluence));
            delta.EmotionalConnectionPercent = Math.Max(-3.0, Math.Min(3.0, delta.EmotionalConnectionPercent));
        }

        private static void Trim<T>(List<T> list, int maxCount)
        {
            if (list == null || list.Count <= maxCount) return;
            list.RemoveRange(maxCount, list.Count - maxCount);
        }

        private static string TruncateLocal(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string cleaned = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (cleaned.Length <= maxLength) return cleaned;
            return cleaned.Substring(0, maxLength).Trim() + "...";
        }
    }
}
