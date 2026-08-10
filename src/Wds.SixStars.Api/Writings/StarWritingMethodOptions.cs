using System;

namespace Wds.SixStars.Api.Writings
{
    /// <summary>Stable identifiers for the shelf artifact produced by a writing technique.</summary>
    public static class StarWritingOutputTypes
    {
        /// <summary>A compact standalone joke, aphorism, tiny poem, or poetic fragment.</summary>
        public const string Spark = "spark";
        /// <summary>A larger collaborative story developed across the Six Stars.</summary>
        public const string ConstellationStory = "constellation-story";

        public static string Normalize(string? outputType)
        {
            return string.Equals(outputType, ConstellationStory, StringComparison.OrdinalIgnoreCase)
                ? ConstellationStory
                : Spark;
        }
    }

    /// <summary>Stable identifiers for the creative method used to make one writing batch.</summary>
    public static class StarWritingMethods
    {
        public const string SemanticCollision = "semantic-collision";
        public const string VerbNounDisplacement = "verb-noun-displacement";
        public const string ConstellationForge = "constellation-forge";

        public static string Normalize(string? method)
        {
            if (string.Equals(method, VerbNounDisplacement, StringComparison.OrdinalIgnoreCase)) return VerbNounDisplacement;
            if (string.Equals(method, ConstellationForge, StringComparison.OrdinalIgnoreCase)) return ConstellationForge;
            return SemanticCollision;
        }

        public static string GetOutputType(string? method)
        {
            return string.Equals(Normalize(method), ConstellationForge, StringComparison.OrdinalIgnoreCase)
                ? StarWritingOutputTypes.ConstellationStory
                : StarWritingOutputTypes.Spark;
        }
    }

    /// <summary>Selects which creative writing method starts a batch.</summary>
    public sealed class StarWritingMethodOptions
    {
        public double SemanticCollisionWeight { get; set; } = 0.70;
        public double VerbNounDisplacementWeight { get; set; } = 0.30;
        /// <summary>Relative weight for the collaborative long-form Constellation Forge technique.</summary>
        public double ConstellationForgeWeight { get; set; } = 0.10;

        public string SelectMethod(Random random)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));

            double collision = NormalizeWeight(SemanticCollisionWeight);
            double displacement = NormalizeWeight(VerbNounDisplacementWeight);
            double constellation = NormalizeWeight(ConstellationForgeWeight);
            double total = collision + displacement + constellation;
            if (total <= 0) return StarWritingMethods.SemanticCollision;

            double roll = random.NextDouble() * total;
            if (roll < collision) return StarWritingMethods.SemanticCollision;
            roll -= collision;
            if (roll < displacement) return StarWritingMethods.VerbNounDisplacement;
            return StarWritingMethods.ConstellationForge;
        }

        private static double NormalizeWeight(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? 0 : value;
        }
    }
}
