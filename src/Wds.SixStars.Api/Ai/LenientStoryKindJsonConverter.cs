using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Local models sometimes return storyKind values such as "warm poetic reply".
    /// Unknown values should not throw away an otherwise good response.
    /// </summary>
    public sealed class LenientStoryKindJsonConverter : JsonConverter<StoryKind>
    {
        public override StoryKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number))
            {
                return Enum.IsDefined(typeof(StoryKind), number) ? (StoryKind)number : StoryKind.None;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                return StoryKind.None;
            }

            string? value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value)) return StoryKind.None;

            string compact = Compact(value);
            switch (compact)
            {
                case "none":
                case "chat":
                case "reply":
                case "normalreply":
                    return StoryKind.None;
                case "personalmemory":
                case "memory":
                    return StoryKind.PersonalMemory;
                case "moralparable":
                case "parable":
                    return StoryKind.MoralParable;
                case "ancientstarfable":
                case "starfable":
                case "fable":
                    return StoryKind.AncientStarFable;
                case "poeticabstraction":
                case "poetic":
                case "poetry":
                    return StoryKind.PoeticAbstraction;
                case "comfortstory":
                case "comfort":
                    return StoryKind.ComfortStory;
                case "flirtystory":
                case "flirty":
                case "romanticstory":
                    return StoryKind.FlirtyStory;
                case "horrorwarning":
                case "warning":
                    return StoryKind.HorrorWarning;
                case "comedystory":
                case "comedy":
                    return StoryKind.ComedyStory;
                case "battlememory":
                case "battle":
                    return StoryKind.BattleMemory;
                case "secretunlockstory":
                case "secret":
                    return StoryKind.SecretUnlockStory;
                default:
                    if (Enum.TryParse(value, ignoreCase: true, out StoryKind parsed)) return parsed;
                    return StoryKind.None;
            }
        }

        public override void Write(Utf8JsonWriter writer, StoryKind value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }

        private static string Compact(string value)
        {
            char[] buffer = new char[value.Length];
            int length = 0;
            foreach (char ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    buffer[length++] = char.ToLowerInvariant(ch);
                }
            }
            return new string(buffer, 0, length);
        }
    }
}
