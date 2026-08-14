using System;
using Wds.SixStars.Api.Forge.Contracts;
using Wds.SixStars.Api.Writings;

namespace Wds.SixStars.Api.Forge.Generation
{
    public static class ForgeGeneratorIds
    {
        public const string Auto = "auto";
        public const string SemanticCollision = StarWritingMethods.SemanticCollision;
        public const string VerbNounDisplacement = StarWritingMethods.VerbNounDisplacement;
        public const string ConstellationForge = StarWritingMethods.ConstellationForge;

        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(value, Auto, StringComparison.OrdinalIgnoreCase)) return Auto;
            return StarWritingMethods.Normalize(value);
        }
    }

    public sealed class ForgeContentGenerationRequest
    {
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
        public ForgeActorReference Actor { get; set; } = new ForgeActorReference();
        public string GeneratorId { get; set; } = ForgeGeneratorIds.Auto;
        public Action<string>? Progress { get; set; }
    }

    /// <summary>
    /// Compatibility result during the first extraction. Artifact/result DTOs can become transport-neutral next;
    /// callers no longer need to invoke the monolithic writing service directly for generation.
    /// </summary>
    public sealed class ForgeContentGenerationResult
    {
        public string RequestId { get; set; } = string.Empty;
        public ForgeActorReference Actor { get; set; } = new ForgeActorReference();
        public string GeneratorId { get; set; } = string.Empty;
        public StarWritingCreateResult WritingResult { get; set; } = new StarWritingCreateResult();
    }
}
