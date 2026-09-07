using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Background LLM reviews sometimes return fractional suggested Heart/Trust/Romance values
    /// such as 0.25 even though the deterministic model stores those fields as integers.
    /// The suggestion is only advisory; a fractional suggestion should never make the whole
    /// background review fail and erase valid emotional vectors. This converter accepts ints,
    /// floats, numeric strings, and common camel/snake-case aliases.
    /// </summary>
    public sealed class RelationshipDeltaJsonConverter : JsonConverter<RelationshipDelta>
    {
        public override RelationshipDelta Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return RelationshipDelta.Zero();
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                return BackgroundReviewCompactCodec.ParseSuggestedStateDeltas(reader.GetString());
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                using (JsonDocument.ParseValue(ref reader)) { }
                return RelationshipDelta.Zero();
            }

            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            RelationshipDelta delta = RelationshipDelta.Zero();

            delta.Heart = ReadIntLike(root, "Heart", "heart", "heartDelta", "heart_delta");
            delta.Trust = ReadIntLike(root, "Trust", "trust", "trustDelta", "trust_delta");
            delta.Romance = ReadIntLike(root, "Romance", "romance", "romanceDelta", "romance_delta");
            delta.BenaveilInfluence = ReadIntLike(root, "BenaveilInfluence", "benaveilInfluence", "benaveil_influence", "benaveilDelta", "benaveil_delta");
            delta.EmotionalConnectionPercent = ReadDoubleLike(root, "EmotionalConnectionPercent", "emotionalConnectionPercent", "emotional_connection_percent", "connectionPercent", "connection_percent");

            return delta;
        }

        public override void Write(Utf8JsonWriter writer, RelationshipDelta value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("Heart", value?.Heart ?? 0);
            writer.WriteNumber("Trust", value?.Trust ?? 0);
            writer.WriteNumber("Romance", value?.Romance ?? 0);
            writer.WriteNumber("BenaveilInfluence", value?.BenaveilInfluence ?? 0);
            writer.WriteNumber("EmotionalConnectionPercent", value?.EmotionalConnectionPercent ?? 0);
            writer.WriteEndObject();
        }

        private static int ReadIntLike(JsonElement root, params string[] names)
        {
            double value = ReadDoubleLike(root, names);
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private static double ReadDoubleLike(JsonElement root, params string[] names)
        {
            foreach (string name in names)
            {
                if (!root.TryGetProperty(name, out JsonElement value)) continue;

                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
                {
                    return number;
                }

                if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                {
                    return parsed;
                }

                if (value.ValueKind == JsonValueKind.True) return 1;
                if (value.ValueKind == JsonValueKind.False || value.ValueKind == JsonValueKind.Null) return 0;
            }

            return 0;
        }
    }
}
