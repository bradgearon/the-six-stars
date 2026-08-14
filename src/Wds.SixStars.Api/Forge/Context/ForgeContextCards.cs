using System.Collections.Generic;
using Wds.SixStars.Api.Forge.Contracts;

namespace Wds.SixStars.Api.Forge.Context
{
    public sealed class ForgeContextField
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class ForgeContextCard
    {
        public string CardType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public ForgeActorReference Actor { get; set; } = new ForgeActorReference();
        public List<ForgeContextField> Fields { get; set; } = new List<ForgeContextField>();
        public Dictionary<string, List<string>> Lists { get; set; } = new Dictionary<string, List<string>>();
    }

    public sealed class ForgeContextBundle
    {
        public ForgeActorReference Actor { get; set; } = new ForgeActorReference();
        public ForgeContextCard Character { get; set; } = new ForgeContextCard { CardType = "character" };
        public ForgeContextCard Relationship { get; set; } = new ForgeContextCard { CardType = "relationship" };
        public ForgeContextCard Emotion { get; set; } = new ForgeContextCard { CardType = "emotion" };
        public ForgeContextCard Memory { get; set; } = new ForgeContextCard { CardType = "memory" };
        public ForgeContextCard Setting { get; set; } = new ForgeContextCard { CardType = "setting" };
        public ForgeContextCard Conversation { get; set; } = new ForgeContextCard { CardType = "conversation" };
    }
}
