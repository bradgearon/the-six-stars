using System;

namespace Wds.SixStars.Api.Models
{
    public sealed class EmotionVectorScore
    {
        public string Id { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public double Score { get; set; }

        public double WeightedScore => Confidence * Score;

        public bool IsHighSignal(double minConfidence = 0.40, double minScore = 25)
        {
            return Confidence >= minConfidence && Score >= minScore;
        }

        public override string ToString()
        {
            return Id + "(" + Confidence.ToString("0.00") + ":" + Score.ToString("0") + ")";
        }
    }
}
