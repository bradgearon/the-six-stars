using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Forge.Contracts
{
    /// <summary>
    /// Transport-neutral request envelope for the future out-of-process Forge service.
    /// The current app uses Forge in-process, but these contracts deliberately avoid UI/runtime types.
    /// </summary>
    public sealed class ForgeServiceRequest
    {
        public int SchemaVersion { get; set; } = 1;
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
        public string Operation { get; set; } = string.Empty;
        public ForgeActorReference Actor { get; set; } = new ForgeActorReference();
        public Dictionary<string, string> Input { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Small update notice a child Forge can send to a parent Forge without transferring its entire private state.
    /// </summary>
    public sealed class ForgeStateUpdateNotice
    {
        public string Facet { get; set; } = string.Empty;
        public long Revision { get; set; }
        public string Summary { get; set; } = string.Empty;
    }

    /// <summary>
    /// Transport-neutral service result packet. Audio/blob references can be carried in Content later.
    /// </summary>
    public sealed class ForgeServicePacket
    {
        public int SchemaVersion { get; set; } = 1;
        public string PacketId { get; set; } = Guid.NewGuid().ToString("N");
        public string CorrelationId { get; set; } = string.Empty;
        public ForgeActorReference Actor { get; set; } = new ForgeActorReference();
        public string Kind { get; set; } = string.Empty;
        public Dictionary<string, string> Content { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<ForgeStateUpdateNotice> Updates { get; set; } = new List<ForgeStateUpdateNotice>();
    }

    public static class ForgeOperations
    {
        public const string ActorRespond = "actor.respond";
        public const string VoiceRender = "voice.render";
        public const string InteractionReview = "interaction.review";
        public const string ContentGenerate = "content.generate";
    }
}
