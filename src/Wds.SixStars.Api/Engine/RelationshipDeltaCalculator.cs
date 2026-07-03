using System;
using System.Linq;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Engine
{
    public sealed class RelationshipDeltaCalculator : IRelationshipDeltaCalculator
    {
        public RelationshipDelta Calculate(StarProfile profile, StarInteractionRequest request, StarInteractionResponse response)
        {
            RelationshipDelta delta = new RelationshipDelta();

            foreach (StarEmotionDefinition definition in profile.Emotions)
            {
                if (!response.EmotionScores.TryGetValue(definition.Id, out EmotionScore? score))
                {
                    continue;
                }

                if (score.Score < 70 || score.Confidence < 0.70)
                {
                    continue;
                }

                int amount = ScoreToAmount(score.Score, score.Confidence);
                if (definition.Valence == EmotionValence.Positive)
                {
                    delta.Heart += amount;
                    delta.Trust += Math.Max(1, amount - 1);
                    if (request.GameContext.RomanceMode && definition.Tags.Contains("drawn_near"))
                    {
                        delta.Romance += Math.Max(0, amount - 2);
                    }
                    delta.BenaveilInfluence -= 1;
                }
                else if (definition.Valence == EmotionValence.Negative)
                {
                    delta.Heart -= amount;
                    delta.Trust -= Math.Max(1, amount - 1);
                    if (request.GameContext.RomanceMode)
                    {
                        delta.Romance -= Math.Max(0, amount - 2);
                    }
                    delta.BenaveilInfluence += 1;
                }
            }

            if (response.DetectedCombos.Contains("affection_plus_wisdom"))
            {
                delta.Heart += 2;
                delta.Trust += 2;
                if (request.GameContext.RomanceMode) delta.Romance += 1;
                delta.BenaveilInfluence -= 1;
            }

            if (response.DetectedCombos.Contains("cruel_escalation"))
            {
                delta.Heart -= 3;
                delta.Trust -= 2;
                delta.BenaveilInfluence += 2;
            }

            Clamp(delta);
            return delta;
        }

        private static int ScoreToAmount(double score, double confidence)
        {
            double weighted = (score / 100.0) * confidence;
            if (weighted >= 0.90) return 4;
            if (weighted >= 0.80) return 3;
            if (weighted >= 0.70) return 2;
            return 1;
        }

        private static void Clamp(RelationshipDelta delta)
        {
            delta.Heart = Math.Max(-8, Math.Min(8, delta.Heart));
            delta.Trust = Math.Max(-8, Math.Min(8, delta.Trust));
            delta.Romance = Math.Max(-6, Math.Min(6, delta.Romance));
            delta.BenaveilInfluence = Math.Max(-6, Math.Min(6, delta.BenaveilInfluence));
        }
    }
}
