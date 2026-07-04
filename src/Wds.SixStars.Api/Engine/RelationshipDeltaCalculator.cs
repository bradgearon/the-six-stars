using System;
using System.Collections.Generic;
using System.Linq;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Engine
{
    /// <summary>
    /// Converts the fast foreground emotionalEffectVector, the fast negativeEmotionVector, and the
    /// background review's suggested deltas/combos into small deterministic state changes.
    /// The model may suggest, but this class decides what actually moves the saved Star state.
    /// </summary>
    public sealed class RelationshipDeltaCalculator : IRelationshipDeltaCalculator
    {
        public static readonly string[] NegativeEmotionIds = new[]
        {
            "Irritated", "Disrespected", "Pressured", "CreepedOut", "Bored", "Hurt", "Unsafe"
        };

        public RelationshipDelta Calculate(StarProfile profile, StarInteractionRequest request, StarInteractionResponse response, StarState currentState)
        {
            if (currentState == null) currentState = new StarState { StarId = request.StarId };

            List<EmotionVectorScore> positiveSignals = ParseVector(response.EmotionalEffectVector, StarEmotionMatrix.StandardEmotionIds)
                .Where(x => x.IsHighSignal())
                .OrderByDescending(x => x.WeightedScore)
                .Take(3)
                .ToList();

            List<EmotionVectorScore> negativeSignals = ParseVector(response.NegativeEmotionVector, NegativeEmotionIds)
                .Where(x => x.IsHighSignal())
                .OrderByDescending(x => x.WeightedScore)
                .Take(3)
                .ToList();

            RelationshipDelta delta = new RelationshipDelta();
            bool romanceGate = IsRomanceGateOpen(profile, request, currentState, positiveSignals);

            foreach (EmotionVectorScore signal in positiveSignals)
            {
                ApplyPositiveSignal(delta, signal, request.GameContext.RomanceMode && romanceGate);
            }

            foreach (EmotionVectorScore signal in negativeSignals)
            {
                ApplyNegativeSignal(delta, signal, request.GameContext.RomanceMode);
            }

            List<string> autoCombos = DetectCombos(positiveSignals, negativeSignals, currentState).ToList();
            List<string> acceptedCombos = MergeAndLimitCombos(response.DetectedCombos, autoCombos, currentState, maxCombos: 3).ToList();
            response.DetectedCombos.Clear();
            response.DetectedCombos.AddRange(acceptedCombos);

            ApplyComboDeltas(delta, acceptedCombos, request.GameContext.RomanceMode && romanceGate);
            ApplyBoundedSuggestedDelta(delta, response.SuggestedStateDeltas, positiveSignals, negativeSignals, acceptedCombos, request.GameContext.RomanceMode && romanceGate);
            ApplyRepeatDamping(delta, positiveSignals, negativeSignals, acceptedCombos, currentState);

            ApplyEmotionalConnectionDelta(profile, request, currentState, delta, positiveSignals, negativeSignals, acceptedCombos);

            // Non-allies can still warm up, but they should not leap into deep relationship scores from one good line.
            if (!currentState.IsAlly && !string.Equals(profile.StarId, "Elusiaa", StringComparison.OrdinalIgnoreCase))
            {
                delta.Heart = Math.Min(delta.Heart, 1);
                delta.Trust = Math.Min(delta.Trust, 1);
                delta.Romance = Math.Min(delta.Romance, 0);
                delta.EmotionalConnectionPercent = Math.Min(delta.EmotionalConnectionPercent, 0);
            }

            Clamp(delta);

            response.AcceptedPositiveEmotionSignals.Clear();
            response.AcceptedPositiveEmotionSignals.AddRange(positiveSignals.Select(x => x.ToString()));
            response.AcceptedNegativeEmotionSignals.Clear();
            response.AcceptedNegativeEmotionSignals.AddRange(negativeSignals.Select(x => x.ToString()));
            response.AcceptedRelationshipCombos.Clear();
            response.AcceptedRelationshipCombos.AddRange(acceptedCombos);
            response.DeterministicEmotionUpdateSummary = BuildSummary(currentState, positiveSignals, negativeSignals, acceptedCombos, response.SuggestedStateDeltas, delta, romanceGate);
            response.Debug.Notes.Add(response.DeterministicEmotionUpdateSummary);

            return delta;
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

        private static void ApplyPositiveSignal(RelationshipDelta delta, EmotionVectorScore signal, bool romanceGate)
        {
            switch (signal.Id)
            {
                case "Trusting":
                case "Safe":
                case "Protected":
                case "Respected":
                case "Comforted":
                    delta.Trust += 1;
                    delta.Heart += signal.Score >= 90 ? 1 : 0;
                    break;
                case "Admiring":
                case "Affectionate":
                case "Tender":
                case "Hopeful":
                case "Wondrous":
                case "Impassioned":
                    delta.Heart += 1;
                    delta.Trust += signal.Id == "Hopeful" ? 1 : 0;
                    break;
                case "Flattered":
                case "Attracted":
                case "Yearning":
                    delta.Heart += 1;
                    break;
                default:
                    delta.Heart += signal.Score >= 88 && signal.Confidence >= 0.80 ? 1 : 0;
                    break;
            }

            if (romanceGate && IsRomanceSignal(signal.Id) && signal.Score >= 85 && signal.Confidence >= 0.75)
            {
                delta.Romance += signal.Id == "Attracted" || signal.Id == "Yearning" ? 2 : 1;
            }
        }

        private static void ApplyNegativeSignal(RelationshipDelta delta, EmotionVectorScore signal, bool romanceMode)
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
                    delta.Heart -= 2;
                    delta.Trust -= 1;
                    if (romanceMode) delta.Romance -= 1;
                    break;
                case "Irritated":
                    delta.Heart -= 1;
                    break;
                case "Bored":
                    // Boredom should not punish hard, but repeated low-effort turns should not build connection.
                    delta.Heart -= signal.Score >= 90 ? 1 : 0;
                    break;
            }

            delta.BenaveilInfluence += signal.Id == "CreepedOut" || signal.Id == "Unsafe" || signal.Id == "Hurt" ? 1 : 0;
        }

        private static IEnumerable<string> DetectCombos(List<EmotionVectorScore> positive, List<EmotionVectorScore> negative, StarState state)
        {
            bool Has(string id) => positive.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            bool Neg(string id) => negative.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

            if ((Has("Trusting") || Has("Safe")) && (Has("Respected") || Has("Protected"))) yield return "trust_respect_combo";
            if ((Has("Affectionate") || Has("Tender")) && (Has("Hopeful") || Has("Comforted"))) yield return "affection_plus_hope";
            if (Has("Attracted") && (Has("Flattered") || Has("Tender") || Has("Yearning"))) yield return "romantic_spark";
            if ((Has("Vulnerable") || Has("Tender")) && (Has("Safe") || Has("Trusting"))) yield return "private_vulnerability_safety";
            if ((Neg("Disrespected") || Neg("Pressured") || Neg("CreepedOut")) && state.HurtSuccessionCount > 0) yield return "repeated_boundary_pressure";
            if (Neg("Hurt") || Neg("Unsafe")) yield return "wounded_guard";
        }

        private static IEnumerable<string> MergeAndLimitCombos(IEnumerable<string> modelCombos, IEnumerable<string> autoCombos, StarState state, int maxCombos)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string combo in autoCombos.Concat(modelCombos ?? Enumerable.Empty<string>()))
            {
                if (string.IsNullOrWhiteSpace(combo)) continue;
                string id = Slugify(combo);
                if (!seen.Add(id)) continue;
                // Do not farm the same combo every line; after 3+ triggers it must cool down unless it is negative/boundary related.
                RelationshipComboState? prior = state.RelationshipCombos.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                bool negativeCombo = id.Contains("hurt") || id.Contains("wounded") || id.Contains("cruel") || id.Contains("pressure") || id.Contains("guard");
                if (!negativeCombo && prior != null && prior.Count >= 3 && prior.LastTriggeredAt > DateTimeOffset.UtcNow.AddMinutes(-10))
                {
                    continue;
                }
                yield return id;
                if (--maxCombos <= 0) yield break;
            }
        }

        private static void ApplyComboDeltas(RelationshipDelta delta, IEnumerable<string> combos, bool romanceGate)
        {
            foreach (string combo in combos)
            {
                switch (combo)
                {
                    case "affection_plus_wisdom":
                    case "trust_respect_combo":
                        delta.Heart += 1;
                        delta.Trust += 1;
                        break;
                    case "affection_plus_hope":
                    case "private_vulnerability_safety":
                        delta.Heart += 1;
                        if (romanceGate) delta.Romance += 1;
                        delta.BenaveilInfluence -= 1;
                        break;
                    case "romantic_spark":
                        if (romanceGate) delta.Romance += 1;
                        delta.Heart += 1;
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
        }

        private static void ApplyBoundedSuggestedDelta(RelationshipDelta delta, RelationshipDelta suggestion, List<EmotionVectorScore> positive, List<EmotionVectorScore> negative, List<string> combos, bool romanceGate)
        {
            bool hasPositiveSupport = positive.Count > 0 || combos.Any(x => x.Contains("hope") || x.Contains("trust") || x.Contains("affection") || x.Contains("romantic"));
            bool hasNegativeSupport = negative.Count > 0 || combos.Any(x => x.Contains("hurt") || x.Contains("cruel") || x.Contains("pressure") || x.Contains("guard"));

            if (hasPositiveSupport)
            {
                delta.Heart += Math.Max(0, Math.Min(1, suggestion.Heart));
                delta.Trust += Math.Max(0, Math.Min(1, suggestion.Trust));
                if (romanceGate) delta.Romance += Math.Max(0, Math.Min(1, suggestion.Romance));
                delta.BenaveilInfluence += Math.Max(-1, Math.Min(0, suggestion.BenaveilInfluence));
            }

            if (hasNegativeSupport)
            {
                delta.Heart += Math.Min(0, Math.Max(-1, suggestion.Heart));
                delta.Trust += Math.Min(0, Math.Max(-1, suggestion.Trust));
                delta.Romance += Math.Min(0, Math.Max(-1, suggestion.Romance));
                delta.BenaveilInfluence += Math.Max(0, Math.Min(1, suggestion.BenaveilInfluence));
            }

            // The model may suggest tiny connection drift. Deterministic logic still requires support and clamps it heavily.
            if (hasPositiveSupport && suggestion.EmotionalConnectionPercent > 0)
            {
                delta.EmotionalConnectionPercent += Math.Min(0.35, suggestion.EmotionalConnectionPercent);
            }
            if ((hasNegativeSupport || positive.Count == 0) && suggestion.EmotionalConnectionPercent < 0)
            {
                delta.EmotionalConnectionPercent += Math.Max(-0.75, suggestion.EmotionalConnectionPercent);
            }
        }

        private static void ApplyRepeatDamping(RelationshipDelta delta, List<EmotionVectorScore> positive, List<EmotionVectorScore> negative, List<string> combos, StarState state)
        {
            List<EmotionUpdateHistoryEntry> recent = state.EmotionUpdateHistory.Take(3).ToList();
            if (recent.Count == 0) return;

            int repeatedPositiveSignals = positive.Count(signal => recent.Any(entry => entry.AcceptedPositiveSignals.Any(x => x.StartsWith(signal.Id + "(", StringComparison.OrdinalIgnoreCase))));
            int repeatedPositiveCombos = combos.Count(combo => recent.Any(entry => entry.AcceptedCombos.Any(x => string.Equals(x, combo, StringComparison.OrdinalIgnoreCase))));
            if (repeatedPositiveSignals + repeatedPositiveCombos >= 2)
            {
                delta.Heart = Math.Max(0, delta.Heart - 1);
                delta.Trust = Math.Max(0, delta.Trust - 1);
                delta.Romance = Math.Max(0, delta.Romance - 1);
                delta.EmotionalConnectionPercent = Math.Min(delta.EmotionalConnectionPercent, 0.20);
            }

            // Negative repeats should still matter; do not damp them. They build caution/hurt succession instead.
        }

        private static void ApplyEmotionalConnectionDelta(StarProfile profile, StarInteractionRequest request, StarState state, RelationshipDelta delta, List<EmotionVectorScore> positive, List<EmotionVectorScore> negative, List<string> combos)
        {
            double connection = state.EmotionalConnectionPercent;
            bool isAlly = state.IsAlly || string.Equals(profile.StarId, "Elusiaa", StringComparison.OrdinalIgnoreCase);
            bool hasMajorNegative = negative.Any(x => x.Score >= 80 && x.Confidence >= 0.70 && (x.Id == "Disrespected" || x.Id == "Pressured" || x.Id == "CreepedOut" || x.Id == "Hurt" || x.Id == "Unsafe"));
            bool boringOrIrritated = negative.Any(x => x.Score >= 85 && x.Confidence >= 0.70 && (x.Id == "Bored" || x.Id == "Irritated"));
            bool hasIntimatePositive = positive.Count(x => IsIntimateConnectionSignal(x.Id) && x.Score >= 82 && x.Confidence >= 0.72) >= 1;
            bool hasMultipleIntimate = positive.Count(x => IsIntimateConnectionSignal(x.Id) && x.Score >= 82 && x.Confidence >= 0.72) >= 2;
            bool hasPersonalityMatch = positive.Any(x => IsPersonalityMatchedConnectionSignal(profile.StarId, x.Id) && x.Score >= 80 && x.Confidence >= 0.70);
            bool hasConnectionCombo = combos.Any(x => x.Contains("romantic") || x.Contains("affection") || x.Contains("vulnerability") || x.Contains("trust"));
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
                // Before ally unlock, the relationship may warm in Heart/Trust, but the deeper girlfriend meter does not build.
                if (connection > 0) delta.EmotionalConnectionPercent -= 0.25;
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

            // At-rest drift: unless the bond is already more than half connected, non-intimate turns cool slowly.
            // This prevents shallow farming while letting a serious >50% connection remain stable until harmed.
            if (connection > 0 && connection <= 50)
            {
                delta.EmotionalConnectionPercent -= 0.35;
            }
        }

        private static bool IsRomanceGateOpen(StarProfile profile, StarInteractionRequest request, StarState state, List<EmotionVectorScore> positive)
        {
            if (!request.GameContext.RomanceMode) return false;
            if (!state.IsAlly && !string.Equals(profile.StarId, "Elusiaa", StringComparison.OrdinalIgnoreCase)) return false;
            bool hasHighRomanceEmotion = positive.Any(x => IsRomanceSignal(x.Id) && x.Score >= 85 && x.Confidence >= 0.75);
            if (!hasHighRomanceEmotion) return false;

            string text = (request.PlayerText ?? string.Empty).ToLowerInvariant();
            bool earnedByWords = ContainsAny(text, "poem", "poetry", "beautiful", "gentle", "safe", "trust", "song", "music", "kiss", "touch", "cat hair", "fur", "warm", "miracle", "stars", "heart");
            bool established = state.Romance > 0 || state.Trust >= 5 || state.Heart >= 5 || state.PositiveSuccessionCount >= 2 || state.EmotionalConnectionPercent >= 10;
            bool kaeliaCatHook = string.Equals(profile.StarId, "Kaelia", StringComparison.OrdinalIgnoreCase) && ContainsAny(text, "cat", "cat hair", "fur", "purr", "tail", "ears");
            return earnedByWords || established || kaeliaCatHook;
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
                    return id == "Comforted" || id == "Safe" || id == "Protected" || id == "Trusting" || id == "Tender" || id == "Hopeful" || id == "Wondrous" || id == "Affectionate";
                case "kaelia":
                    return id == "Intrigued" || id == "Wondrous" || id == "Hopeful" || id == "Respected" || id == "Trusting" || id == "Tender" || id == "Flattered" || id == "Attracted" || id == "Yearning";
                case "khavia":
                    return id == "Happy" || id == "Playful" || id == "Comforted" || id == "Safe" || id == "Trusting" || id == "Affectionate" || id == "Tender";
                case "auerlis":
                    return id == "Happy" || id == "Amused" || id == "Playful" || id == "Hopeful" || id == "Admiring" || id == "Affectionate" || id == "Impassioned";
                case "maera":
                    return id == "Protected" || id == "Trusting" || id == "Respected" || id == "Tender" || id == "Attracted" || id == "Yearning" || id == "Vulnerable";
                case "nyxari":
                    return id == "Safe" || id == "Trusting" || id == "Vulnerable" || id == "Comforted" || id == "Tender" || id == "Hopeful" || id == "Intrigued";
                default:
                    return IsIntimateConnectionSignal(id);
            }
        }

        private static bool LooksLikeHighEffortIntimateSpeech(string text)
        {
            string t = (text ?? string.Empty).ToLowerInvariant();
            int length = t.Count(char.IsLetter);
            if (ContainsAny(t, "poem", "poetry", "your eyes", "your heart", "miracle", "beautiful", "stars", "sea", "storm", "safe", "trust", "I choose", "I won't leave", "I will stay", "cat hair", "fur", "purr")) return true;
            return length >= 90 && (ContainsAny(t, "you", "your") && ContainsAny(t, "heart", "light", "dark", "safe", "trust", "beautiful", "gentle", "hope"));
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            return needles.Any(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string BuildSummary(StarState state, List<EmotionVectorScore> positive, List<EmotionVectorScore> negative, List<string> combos, RelationshipDelta suggested, RelationshipDelta actual, bool romanceGate)
        {
            string pos = positive.Count == 0 ? "none" : string.Join(", ", positive.Select(x => x.ToString()));
            string neg = negative.Count == 0 ? "none" : string.Join(", ", negative.Select(x => x.ToString()));
            string combo = combos.Count == 0 ? "none" : string.Join(", ", combos);
            double projectedConnection = Math.Max(0, Math.Min(100, state.EmotionalConnectionPercent + actual.EmotionalConnectionPercent));
            bool projectedReady = (state.IsGirlfriend || (state.IsAlly && projectedConnection >= 85 && state.Heart + actual.Heart >= 20 && state.Trust + actual.Trust >= 15 && state.Romance + actual.Romance >= 12));
            return "Deterministic emotion update: skill=interested_private_potential_partner; ally=" + state.IsAlly
                + "; connection=" + state.EmotionalConnectionPercent.ToString("0.0") + "%->" + projectedConnection.ToString("0.0") + "%"
                + "; girlfriendReadyAfter=" + projectedReady
                + "; positiveSignals=" + pos
                + "; negativeSignals=" + neg
                + "; acceptedCombos=" + combo
                + "; romanceGate=" + romanceGate
                + "; suggestedDelta=(H" + suggested.Heart + ",T" + suggested.Trust + ",R" + suggested.Romance + ",B" + suggested.BenaveilInfluence + ",C" + suggested.EmotionalConnectionPercent.ToString("0.00") + ")"
                + "; actualDelta=(H" + actual.Heart + ",T" + actual.Trust + ",R" + actual.Romance + ",B" + actual.BenaveilInfluence + ",C" + actual.EmotionalConnectionPercent.ToString("0.00") + ").";
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

        private static void Clamp(RelationshipDelta delta)
        {
            delta.Heart = Math.Max(-4, Math.Min(4, delta.Heart));
            delta.Trust = Math.Max(-4, Math.Min(4, delta.Trust));
            delta.Romance = Math.Max(-3, Math.Min(3, delta.Romance));
            delta.BenaveilInfluence = Math.Max(-3, Math.Min(3, delta.BenaveilInfluence));
            delta.EmotionalConnectionPercent = Math.Max(-3.0, Math.Min(3.0, delta.EmotionalConnectionPercent));
        }
    }
}
