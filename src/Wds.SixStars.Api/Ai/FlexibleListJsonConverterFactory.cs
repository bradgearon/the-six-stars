using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Local models sometimes return {} for an empty array slot such as rememberedMarkers,
    /// or return one object where a list was expected. That should not make the whole
    /// background emotion review fail, because it would erase valid emotional vectors.
    /// This converter is intentionally lenient for List&lt;T&gt; fields used in model responses.
    /// </summary>
    public sealed class FlexibleListJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
        {
            return typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(List<>);
        }

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            Type itemType = typeToConvert.GetGenericArguments()[0];
            Type converterType = typeof(FlexibleListJsonConverter<>).MakeGenericType(itemType);
            return (JsonConverter)Activator.CreateInstance(converterType)!;
        }
    }

    public sealed class FlexibleListJsonConverter<T> : JsonConverter<List<T>>
    {
        public override List<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            List<T> result = new List<T>();

            if (reader.TokenType == JsonTokenType.Null)
            {
                return result;
            }

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                using JsonDocument document = JsonDocument.ParseValue(ref reader);
                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    TryAddElement(result, item, options);
                }
                return result;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using JsonDocument document = JsonDocument.ParseValue(ref reader);
                JsonElement root = document.RootElement;

                // The most common bad response is rememberedMarkers: {} meaning empty.
                if (!root.EnumerateObject().MoveNext())
                {
                    return result;
                }

                // Also tolerate wrappers like { "items": [...] } or { "values": [...] }.
                foreach (string propertyName in new[] { "items", "values", "markers", "entries", "list" })
                {
                    if (root.TryGetProperty(propertyName, out JsonElement wrapped) && wrapped.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in wrapped.EnumerateArray())
                        {
                            TryAddElement(result, item, options);
                        }
                        return result;
                    }
                }

                // If the object looks like one item, keep it instead of failing the whole response.
                TryAddElement(result, root, options);
                return result;
            }

            if (typeof(T) == typeof(string))
            {
                string? scalar = reader.TokenType switch
                {
                    JsonTokenType.String => reader.GetString(),
                    JsonTokenType.Number => JsonDocument.ParseValue(ref reader).RootElement.ToString(),
                    JsonTokenType.True => "true",
                    JsonTokenType.False => "false",
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(scalar))
                {
                    result.Add((T)(object)scalar.Trim());
                }
                return result;
            }

            // Consume and ignore any unsupported scalar so parsing can continue.
            using (JsonDocument.ParseValue(ref reader)) { }
            return result;
        }

        public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (T item in value ?? new List<T>())
            {
                JsonSerializer.Serialize(writer, item, options);
            }
            writer.WriteEndArray();
        }

        private static void TryAddElement(List<T> result, JsonElement element, JsonSerializerOptions options)
        {
            try
            {
                if (typeof(T) == typeof(string))
                {
                    string? value = element.ValueKind == JsonValueKind.String
                        ? element.GetString()
                        : element.ValueKind == JsonValueKind.Number || element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False
                            ? element.ToString()
                            : null;
                    if (!string.IsNullOrWhiteSpace(value)) result.Add((T)(object)value.Trim());
                    return;
                }

                T? item = element.Deserialize<T>(options);
                if (item != null) result.Add(item);
            }
            catch
            {
                // One malformed item should not sink the full model response.
            }
        }
    }
}
