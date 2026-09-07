using System;
using Wds.SixStars.Api.Forge.Contracts;
using Wds.SixStars.Api.Forge.Context;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Forge.Review
{
    /// <summary>
    /// In-process review request. Legacy Six Stars objects remain attached during migration, while Actor + Context
    /// are the transportable boundary that the future standalone Forge service will keep.
    /// </summary>
    public sealed class ForgeInteractionReviewRequest
    {
        public string ReviewId { get; set; } = Guid.NewGuid().ToString("N");
        public ForgeActorReference Actor { get; set; } = new ForgeActorReference();
        public ForgeContextBundle Context { get; set; } = new ForgeContextBundle();

        // Compatibility payload for the current StructuredStarAgent implementation.
        public StarProfile Profile { get; set; } = default!;
        public StarInteractionRequest Interaction { get; set; } = default!;
        public StarState StateBeforeTurn { get; set; } = default!;
        public StarInteractionResponse ForegroundResponse { get; set; } = default!;
    }

    public sealed class ForgeInteractionReviewResult
    {
        public string ReviewId { get; set; } = string.Empty;
        public ForgeActorReference Actor { get; set; } = new ForgeActorReference();
        public StarInteractionResponse Analysis { get; set; } = new StarInteractionResponse();
    }
}
