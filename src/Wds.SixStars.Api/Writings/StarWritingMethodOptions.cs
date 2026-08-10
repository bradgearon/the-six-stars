using System;

namespace Wds.SixStars.Api.Writings
{
    /// <summary>Stable identifiers for the creative method used to make one writing batch.</summary>
    public static class StarWritingMethods
    {
        public const string SemanticCollision = "semantic-collision";
        public const string VerbNounDisplacement = "verb-noun-displacement";

        public static string Normalize(string? method)
        {
            return string.Equals(method, VerbNounDisplacement, StringComparison.OrdinalIgnoreCase)
                ? VerbNounDisplacement
                : SemanticCollision;
        }
    }

    /// <summary>Selects which creative writing method starts a batch.</summary>
    public sealed class StarWritingMethodOptions
    {
        public double SemanticCollisionWeight { get; set; } = 0.70;
        public double VerbNounDisplacementWeight { get; set; } = 0.30;

        public string SelectMethod(Random random)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));

            double collision = NormalizeWeight(SemanticCollisionWeight);
            double displacement = NormalizeWeight(VerbNounDisplacementWeight);
            double total = collision + displacement;
            if (total <= 0) return StarWritingMethods.SemanticCollision;

            double roll = random.NextDouble() * total;
            return roll < collision
                ? StarWritingMethods.SemanticCollision
                : StarWritingMethods.VerbNounDisplacement;
        }

        private static double NormalizeWeight(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? 0 : value;
        }
    }
}
