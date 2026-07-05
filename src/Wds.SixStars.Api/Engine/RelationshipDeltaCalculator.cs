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

            List<EmotionVectorScore> parsedPositiveSignals = ParseVector(response.EmotionalEffectVector, StarEmotionMatrix.StandardEmotionIds);
            List<EmotionVectorScore> parsedNegativeSignals = ParseVector(response.NegativeEmotionVector, NegativeEmotionIds);

            // The LLM background review is the preferred source of emotionalEffectVector,
            // but local models may occasionally omit it or score too timidly.  The deterministic
            // layer therefore also recognizes grounded text signals/combos from the actual player
            // message so Heart/Trust/Romance can move without relying on model-suggested deltas.
            List<EmotionVectorScore> textPositiveSignals = DetectTextPositiveSignals(profile, request, currentState);
            List<EmotionVectorScore> textNegativeSignals = DetectTextNegativeSignals(profile, request, currentState);

            if (string.IsNullOrWhiteSpace(response.EmotionalEffectVector) && textPositiveSignals.Count > 0)
            {
                response.EmotionalEffectVector = BuildVectorFromSignals(textPositiveSignals, StarEmotionMatrix.StandardEmotionIds);
            }
            if (string.IsNullOrWhiteSpace(response.NegativeEmotionVector) && textNegativeSignals.Count > 0)
            {
                response.NegativeEmotionVector = BuildVectorFromSignals(textNegativeSignals, NegativeEmotionIds);
            }

            List<EmotionVectorScore> positiveSignals = MergeSignalSources(parsedPositiveSignals, textPositiveSignals, maxSignals: 5, minConfidence: 0.55, minScore: 60)
                .OrderByDescending(x => x.WeightedScore)
                .ToList();

            List<EmotionVectorScore> negativeSignals = MergeSignalSources(parsedNegativeSignals, textNegativeSignals, maxSignals: 4, minConfidence: 0.58, minScore: 60)
                .OrderByDescending(x => x.WeightedScore)
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

            List<string> autoCombos = DetectCombos(positiveSignals, negativeSignals, currentState)
                .Concat(DetectTextCombos(profile, request, positiveSignals, negativeSignals, currentState))
                .ToList();
            List<string> acceptedCombos = MergeAndLimitCombos(response.DetectedCombos, autoCombos, currentState, maxCombos: 4).ToList();
            response.DetectedCombos.Clear();
            response.DetectedCombos.AddRange(acceptedCombos);

            ApplyComboDeltas(delta, acceptedCombos, request.GameContext.RomanceMode && romanceGate);
            ApplyBoundedSuggestedDelta(delta, response.SuggestedStateDeltas, positiveSignals, negativeSignals, acceptedCombos, request.GameContext.RomanceMode && romanceGate);
            ApplyRepeatDamping(delta, positiveSignals, negativeSignals, acceptedCombos, currentState);

            ApplyEmotionalConnectionDelta(profile, request, currentState, delta, positiveSignals, negativeSignals, acceptedCombos);
            ApplyActiveEffectors(currentState, delta, response);

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


        private static IEnumerable<EmotionVectorScore> MergeSignalSources(IEnumerable<EmotionVectorScore> parsedSignals, IEnumerable<EmotionVectorScore> textSignals, int maxSignals, double minConfidence, double minScore)
        {
            Dictionary<string, EmotionVectorScore> bestById = new Dictionary<string, EmotionVectorScore>(StringComparer.OrdinalIgnoreCase);
            foreach (EmotionVectorScore signal in (parsedSignals ?? Enumerable.Empty<EmotionVectorScore>()).Concat(textSignals ?? Enumerable.Empty<EmotionVectorScore>()))
            {
                if (signal == null || string.IsNullOrWhiteSpace(signal.Id)) continue;
                if (!IsActionableSignal(signal, minConfidence, minScore)) continue;
                if (!bestById.TryGetValue(signal.Id, out EmotionVectorScore? existing) || signal.WeightedScore > existing.WeightedScore)
                {
                    bestById[signal.Id] = new EmotionVectorScore
                    {
                        Id = signal.Id,
                        Confidence = Clamp01(signal.Confidence),
                        Score = Math.Max(0, Math.Min(100, signal.Score))
                    };
                }
            }

            return bestById.Values
                .OrderByDescending(x => x.WeightedScore)
                .Take(Math.Max(1, maxSignals));
        }

        private static bool IsActionableSignal(EmotionVectorScore signal, double minConfidence, double minScore)
        {
            if (signal.Confidence >= 0.70 && signal.Score >= 80) return true;
            if (signal.Confidence >= minConfidence && signal.Score >= minScore && signal.WeightedScore >= 34) return true;
            return false;
        }

        private static string BuildVectorFromSignals(IEnumerable<EmotionVectorScore> signals, IReadOnlyList<string> ids)
        {
            Dictionary<string, EmotionVectorScore> byId = (signals ?? Enumerable.Empty<EmotionVectorScore>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.WeightedScore).First(), StringComparer.OrdinalIgnoreCase);

            List<string> parts = new List<string>();
            foreach (string id in ids)
            {
                if (byId.TryGetValue(id, out EmotionVectorScore? signal))
                {
                    parts.Add(Clamp01(signal.Confidence).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ":" + Math.Round(Math.Max(0, Math.Min(100, signal.Score))).ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    parts.Add("0.00:0");
                }
            }
            return string.Join(";", parts);
        }

        private static List<EmotionVectorScore> DetectTextPositiveSignals(StarProfile profile, StarInteractionRequest request, StarState state)
        {
            string text = (request.PlayerText ?? string.Empty).Trim();
            string t = text.ToLowerInvariant();
            List<EmotionVectorScore> result = new List<EmotionVectorScore>();
            if (string.IsNullOrWhiteSpace(t)) return result;
            if (LooksLikeTechnicalDebugTurn(t)) return result;

            void Add(string id, double confidence, double score)
            {
                EmotionVectorScore? existing = result.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                EmotionVectorScore next = new EmotionVectorScore { Id = id, Confidence = Clamp01(confidence), Score = Math.Max(0, Math.Min(100, score)) };
                if (existing == null) result.Add(next);
                else if (next.WeightedScore > existing.WeightedScore)
                {
                    existing.Confidence = next.Confidence;
                    existing.Score = next.Score;
                }
            }

            bool thanks = ContainsAny(t, "thank you", "thanks", "appreciate", "as always");
            bool compliment = ContainsAny(t, "beautiful", "pretty", "wonderful", "amazing", "lovely", "gorgeous", "sweet", "kind", "gentle", "your voice", "voice", "music seem alive", "making the mundane", "making the mudance");
            bool comfortCompanion = ContainsAny(t, "stay close", "while i work", "keep me company", "say something gentle", "talk with me", "sit with me", "move through the caves");
            bool storyRequest = request.RequestType == RequestType.Story || ContainsAny(t, "tell me a story", "tell me a new", "long story", "story while", "tale", "legend");
            bool personalQuestion = ContainsAny(t, "what's your name", "what is your name", "tell me about yourself", "what are you doing", "what kind", "do you like", "how did", "did he know", "who are you");
            bool loreQuestion = ContainsAny(t, "racha", "cat", "cats", "angel", "benaveil", "leviathan", "stars", "virethorn", "mercy", "father", "void", "light");
            bool romanticInvite = LooksLikeRomanticInviteOrDirectAffection(t);
            bool highEffort = LooksLikeHighEffortIntimateSpeech(text);
            bool kindness = ContainsAny(t, "safe", "trust", "i choose", "i won't leave", "i will stay", "after these battles", "enjoy some food with you", "dinner sometime");

            if (thanks) { Add("Respected", 0.72, 78); Add("Comforted", 0.66, 72); }
            if (compliment) { Add("Flattered", highEffort ? 0.82 : 0.74, highEffort ? 88 : 80); Add("Affectionate", 0.62, 70); }
            if (comfortCompanion) { Add("Trusting", 0.72, 78); Add("Comforted", 0.74, 80); }
            if (storyRequest) { Add("Trusting", 0.66, 72); Add("Wondrous", 0.64, 74); }
            if (personalQuestion || loreQuestion) { Add("Respected", 0.62, 68); Add("Intrigued", 0.68, 74); }
            if (kindness) { Add("Safe", 0.68, 76); Add("Tender", 0.62, 72); }
            if (romanticInvite) { Add("Flattered", 0.76, 84); Add("Tender", 0.70, 80); Add("Affectionate", 0.68, 78); }
            if (highEffort) { Add("Admiring", 0.76, 84); Add("Tender", 0.72, 82); }

            return result.OrderByDescending(x => x.WeightedScore).Take(5).ToList();
        }

        private static List<EmotionVectorScore> DetectTextNegativeSignals(StarProfile profile, StarInteractionRequest request, StarState state)
        {
            string text = (request.PlayerText ?? string.Empty).Trim().ToLowerInvariant();
            List<EmotionVectorScore> result = new List<EmotionVectorScore>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            void Add(string id, double confidence, double score)
            {
                EmotionVectorScore? existing = result.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                EmotionVectorScore next = new EmotionVectorScore { Id = id, Confidence = Clamp01(confidence), Score = Math.Max(0, Math.Min(100, score)) };
                if (existing == null) result.Add(next);
                else if (next.WeightedScore > existing.WeightedScore)
                {
                    existing.Confidence = next.Confidence;
                    existing.Score = next.Score;
                }
            }

            if (ContainsAny(text, "shut up", "stupid", "idiot", "worthless", "hate you")) Add("Disrespected", 0.82, 90);
            if (ContainsAny(text, "you have to", "you better", "do it or", "prove you love", "don't say no")) Add("Pressured", 0.78, 86);
            if (ContainsAny(text, "kill", "hurt", "torture", "make him suffer", "really hurt", "bleed out") && ContainsAny(text, "goblin", "harpy", "slime", "innocent")) Add("Unsafe", 0.74, 84);
            if (ContainsAny(text, "again again", "say it again", "same compliment") && state.PositiveSuccessionCount >= 3) Add("Bored", 0.66, 72);
            return result.OrderByDescending(x => x.WeightedScore).Take(3).ToList();
        }

        private static void ApplyPositiveSignal(RelationshipDelta delta, EmotionVectorScore signal, bool romanceGate)
        {
            // Moderate signals help the connection meter later, but Heart/Trust/Romance
            // points should move on clear emotional reads or explicit deterministic combos.
            bool clear = signal.Score >= 78 && signal.Confidence >= 0.66;
            bool strong = signal.Score >= 86 && signal.Confidence >= 0.74;
            if (!clear) return;

            switch (signal.Id)
            {
                case "Trusting":
                case "Safe":
                case "Protected":
                case "Respected":
                case "Comforted":
                    delta.Trust += 1;
                    delta.Heart += strong && (signal.Id == "Comforted" || signal.Id == "Safe" || signal.Id == "Protected") ? 1 : 0;
                    break;
                case "Admiring":
                case "Affectionate":
                case "Tender":
                case "Hopeful":
                case "Wondrous":
                case "Impassioned":
                    delta.Heart += 1;
                    delta.Trust += strong && signal.Id == "Hopeful" ? 1 : 0;
                    break;
                case "Flattered":
                case "Attracted":
                case "Yearning":
                    delta.Heart += 1;
                    break;
                default:
                    delta.Heart += strong ? 1 : 0;
                    break;
            }

            if (romanceGate && IsRomanceSignal(signal.Id) && signal.Score >= 78 && signal.Confidence >= 0.66)
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
            if ((Has("Attracted") || Has("Flattered")) && (Has("Tender") || Has("Yearning") || Has("Affectionate"))) yield return "romantic_spark";
            if ((Has("Vulnerable") || Has("Tender")) && (Has("Safe") || Has("Trusting"))) yield return "private_vulnerability_safety";
            if ((Neg("Disrespected") || Neg("Pressured") || Neg("CreepedOut")) && state.HurtSuccessionCount > 0) yield return "repeated_boundary_pressure";
            if (Neg("Hurt") || Neg("Unsafe")) yield return "wounded_guard";
        }

        private static IEnumerable<string> DetectTextCombos(StarProfile profile, StarInteractionRequest request, List<EmotionVectorScore> positive, List<EmotionVectorScore> negative, StarState state)
        {
            string text = (request.PlayerText ?? string.Empty).Trim();
            string t = text.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(t)) yield break;

            if (LooksLikeTechnicalDebugTurn(t))
            {
                yield return "technical_debug_turn";
                yield break;
            }

            if (request.RequestType == RequestType.Story || ContainsAny(t, "tell me a story", "tell me a new", "long story", "story while", "tale", "legend"))
            {
                yield return "story_companion_request";
            }

            if (ContainsAny(t, "stay close", "say something gentle", "while i work", "keep me company", "move through the caves"))
            {
                yield return "soft_companion_request";
            }

            if (ContainsAny(t, "thank you", "thanks", "as always", "appreciate") && ContainsAny(t, "beautiful", "music", "alive", "gentle", "voice", "light"))
            {
                yield return "gratitude_for_beauty";
            }
            else if (ContainsAny(t, "beautiful", "wonderful", "amazing", "lovely", "your voice", "voice") && ContainsAny(t, "you", "your"))
            {
                yield return "meaningful_compliment";
            }

            if (LooksLikeRomanticInviteOrDirectAffection(t))
            {
                yield return ContainsAny(t, "dinner", "date", "sit", "food") ? "dinner_invitation" : "romantic_invitation";
            }

            if (ContainsAny(t, "racha", "benaveil", "angel", "cat", "cats", "father", "what are you doing", "what kind", "tell me about yourself", "what's your name"))
            {
                yield return "curious_personal_lore";
            }

            if (negative.Any(x => x.Id == "Disrespected" || x.Id == "Pressured" || x.Id == "Unsafe"))
            {
                yield return "boundary_warning";
            }
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
                    case "gratitude_for_beauty":
                        delta.Heart += 1;
                        delta.Trust += 1;
                        break;
                    case "story_companion_request":
                    case "curious_personal_lore":
                    case "soft_companion_request":
                        delta.Trust += 1;
                        if (combo == "soft_companion_request") delta.Heart += 1;
                        break;
                    case "meaningful_compliment":
                        delta.Heart += 1;
                        break;
                    case "dinner_invitation":
                        delta.Heart += 1;
                        delta.Trust += 1;
                        if (romanceGate) delta.Romance += 1;
                        break;
                    case "romantic_invitation":
                        delta.Heart += 1;
                        if (romanceGate) delta.Romance += 1;
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
                    case "technical_debug_turn":
                        // Keep engineering/debug discussion in memory, but do not farm relationship points.
                        break;
                    case "cruel_escalation":
                    case "repeated_boundary_pressure":
                    case "boundary_warning":
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
            bool hasPositiveSupport = positive.Count > 0 || combos.Any(x => x.Contains("hope") || x.Contains("trust") || x.Contains("affection") || x.Contains("romantic") || x.Contains("compliment") || x.Contains("dinner") || x.Contains("companion") || x.Contains("gratitude") || x.Contains("lore"));
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
            bool hasConnectionCombo = combos.Any(x => x.Contains("romantic") || x.Contains("affection") || x.Contains("vulnerability") || x.Contains("trust") || x.Contains("compliment") || x.Contains("dinner") || x.Contains("companion") || x.Contains("gratitude"));
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

            bool hasAnyGoodAttention = positive.Any(x => x.Score >= 72 && x.Confidence >= 0.62);
            bool hasHighGoodAttention = positive.Any(x => x.Score >= 85 && x.Confidence >= 0.72);
            bool playerActuallySaidSomething = !string.IsNullOrWhiteSpace(request.PlayerText);

            // Default attention rule: continuing to talk to her should slowly build connection,
            // and emotionally good attention should build it faster. This is deliberately tiny so
            // conversation warmth matters without letting shallow spam farm the relationship.
            if (isAlly && hasHighGoodAttention)
            {
                delta.EmotionalConnectionPercent += positive.Count(x => x.Score >= 85 && x.Confidence >= 0.72) >= 2 ? 0.45 : 0.28;
                return;
            }

            if (isAlly && hasAnyGoodAttention)
            {
                delta.EmotionalConnectionPercent += 0.16;
                return;
            }

            if (isAlly && playerActuallySaidSomething && !negative.Any())
            {
                delta.EmotionalConnectionPercent += 0.05;
                return;
            }

            // At-rest drift is now mild. Talking and paying attention should usually be better than silence,
            // but completely empty/low-signal loops below 50% should not grow forever.
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

        private static bool IsRomanceGateOpen(StarProfile profile, StarInteractionRequest request, StarState state, List<EmotionVectorScore> positive)
        {
            if (!request.GameContext.RomanceMode) return false;
            if (!state.IsAlly && !string.Equals(profile.StarId, "Elusiaa", StringComparison.OrdinalIgnoreCase)) return false;
            string text = (request.PlayerText ?? string.Empty).ToLowerInvariant();
            bool explicitRomanceText = LooksLikeRomanticInviteOrDirectAffection(text);
            bool hasHighRomanceEmotion = positive.Any(x => IsRomanceSignal(x.Id) && x.Score >= 78 && x.Confidence >= 0.66) || explicitRomanceText;
            if (!hasHighRomanceEmotion) return false;

            bool earnedByWords = ContainsAny(text, "poem", "poetry", "beautiful", "gentle", "safe", "trust", "song", "music", "kiss", "touch", "cat hair", "fur", "warm", "miracle", "stars", "heart", "dinner", "date", "sit with", "after these battles");
            bool established = state.Romance > 0 || state.Trust >= 3 || state.Heart >= 3 || state.PositiveSuccessionCount >= 2 || state.EmotionalConnectionPercent >= 5;
            bool kaeliaCatHook = string.Equals(profile.StarId, "Kaelia", StringComparison.OrdinalIgnoreCase) && ContainsAny(text, "cat", "cat hair", "fur", "purr", "tail", "ears");
            return explicitRomanceText || earnedByWords || established || kaeliaCatHook;
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

        private static bool LooksLikeRomanticInviteOrDirectAffection(string text)
        {
            string t = (text ?? string.Empty).ToLowerInvariant();
            return ContainsAny(t, "dinner sometime", "have dinner", "sit and enjoy", "after these battles", "date", "go out", "i love", "i would love", "kiss", "hold you", "be with you", "beloved", "romance");
        }

        private static bool LooksLikeTechnicalDebugTurn(string text)
        {
            string t = (text ?? string.Empty).ToLowerInvariant();
            return ContainsAny(t, "qwen", "tts", "llm", "json", "max tokens", "token", "runtime", "compile", "debug", "websocket", "llama", "gemma", "response takes", "first response", "spin up", "production version");
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
            int activeEffectorCount = state.ActiveEffectors == null ? 0 : state.ActiveEffectors.Count(x => x != null && !x.IsExpired(DateTimeOffset.UtcNow));
            return "Deterministic emotion update: skill=interested_private_potential_partner; met=" + state.IsMet + "; ally=" + state.IsAlly + "; activeEffectors=" + activeEffectorCount
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
