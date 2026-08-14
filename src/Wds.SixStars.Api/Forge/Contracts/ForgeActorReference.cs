using System;

namespace Wds.SixStars.Api.Forge.Contracts
{
    /// <summary>
    /// Stable identity that can cross Forge process/service boundaries without copying an actor's full profile.
    /// </summary>
    public sealed class ForgeActorReference
    {
        public string ForgeId { get; set; } = "six-stars";
        public string ActorId { get; set; } = string.Empty;
        public string ActorType { get; set; } = string.Empty;

        public static ForgeActorReference ForStar(string starId, string? actorType = null)
        {
            return new ForgeActorReference
            {
                ForgeId = "six-stars",
                ActorId = starId ?? string.Empty,
                ActorType = string.IsNullOrWhiteSpace(actorType) ? "star" : actorType!.Trim()
            };
        }

        public override string ToString()
        {
            return "forge://" + (string.IsNullOrWhiteSpace(ForgeId) ? "six-stars" : ForgeId.Trim())
                + "/actors/" + (ActorId ?? string.Empty);
        }
    }
}
