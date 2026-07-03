using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Accepts the new spokenText array contract while remaining tolerant of older local-model
    /// replies that still return spokenText as one string.
    /// </summary>
    public sealed class SpokenTextPartsJsonConverter : JsonConverter<List<string>>
    {
        public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            List<string> parts = new List<string>();

            if (reader.TokenType == JsonTokenType.String)
            {
                string? value = reader.GetString();
                AddClean(parts, value);
                return parts;
            }

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        return parts;
                    }

                    if (reader.TokenType == JsonTokenType.String)
                    {
                        AddClean(parts, reader.GetString());
                    }
                    else if (reader.TokenType == JsonTokenType.Number || reader.TokenType == JsonTokenType.True || reader.TokenType == JsonTokenType.False)
                    {
                        using JsonDocument scalar = JsonDocument.ParseValue(ref reader);
                        AddClean(parts, scalar.RootElement.ToString());
                    }
                    else
                    {
                        using JsonDocument nested = JsonDocument.ParseValue(ref reader);
                        AddClean(parts, nested.RootElement.ToString());
                    }
                }
            }

            return parts;
        }

        public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (string part in value ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(part))
                {
                    writer.WriteStringValue(part.Trim());
                }
            }
            writer.WriteEndArray();
        }

        private static void AddClean(List<string> parts, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string cleaned = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (cleaned.Length > 0) parts.Add(cleaned);
        }
    }
}
