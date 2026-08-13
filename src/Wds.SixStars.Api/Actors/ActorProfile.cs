using System.Collections.Generic;
using System.Linq;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Actors
{
    /// <summary>
    /// Generic souled actor profile. Stars are one actor type; future slimes, NPCs, spirits,
    /// monsters, settlements, or other story participants can opt into only the capabilities
    /// their configured soul depth supports.
    /// </summary>
    public sealed class ActorProfile
    {
        public string ActorId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ActorType { get; set; } = "actor";
        public bool Active { get; set; } = true;
        public int SortOrder { get; set; } = 1000;
        public string Description { get; set; } = string.Empty;
        public string Personality { get; set; } = string.Empty;
        public string Background { get; set; } = string.Empty;
        public List<string> Aliases { get; set; } = new List<string>();
        public List<string> Quotes { get; set; } = new List<string>();
        public List<string> Behaviors { get; set; } = new List<string>();
        public List<string> StorytellingRules { get; set; } = new List<string>();
        public List<string> CanonNotes { get; set; } = new List<string>();
        public ActorSoulConfig Soul { get; set; } = new ActorSoulConfig();

        public static ActorProfile FromStarProfile(StarProfile star)
        {
            return new ActorProfile
            {
                ActorId = string.IsNullOrWhiteSpace(star.ActorId) ? star.StarId : star.ActorId,
                DisplayName = string.IsNullOrWhiteSpace(star.DisplayName) ? star.StarId : star.DisplayName,
                ActorType = string.IsNullOrWhiteSpace(star.ActorType) ? "star" : star.ActorType,
                Active = star.Active,
                SortOrder = star.SortOrder,
                Description = (star.Element + " Star / " + star.Domain).Trim(' ', '/'),
                Personality = star.DeepPersonality ?? string.Empty,
                Background = star.CompleteBackgroundStory ?? star.BackstorySummary ?? string.Empty,
                Aliases = (star.NameAliases ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
                Quotes = (star.Quotes ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
                Behaviors = (star.Behaviors ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
                StorytellingRules = (star.StorytellingRules ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
                CanonNotes = (star.LibraryCanonNotes ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
                Soul = star.Soul ?? ActorSoulConfig.FullStarSoul()
            };
        }
    }

    /// <summary>
    /// Capability/depth declaration for a souled actor. Depth is intentionally open to partial
    /// actors: a slime can participate in story seeding without receiving full relationship memory,
    /// while a Star can opt into the full companion stack.
    /// </summary>
    public sealed class ActorSoulConfig
    {
        public int Depth { get; set; } = 25;
        public bool Memory { get; set; }
        public bool PlayerQuotes { get; set; }
        public bool Conversation { get; set; }
        public bool RelationshipState { get; set; }
        public bool Writing { get; set; }
        public bool StorySeeding { get; set; }
        public bool StoryAppearance { get; set; } = true;
        public bool RumorParticipation { get; set; }
        public List<string> Functions { get; set; } = new List<string>();

        public static ActorSoulConfig FullStarSoul()
        {
            return new ActorSoulConfig
            {
                Depth = 100,
                Memory = true,
                PlayerQuotes = true,
                Conversation = true,
                RelationshipState = true,
                Writing = true,
                StorySeeding = true,
                StoryAppearance = true,
                RumorParticipation = true,
                Functions = new List<string>
                {
                    "companion",
                    "sorceress",
                    "writing-shelf",
                    "constellation-forge"
                }
            };
        }
    }
}
