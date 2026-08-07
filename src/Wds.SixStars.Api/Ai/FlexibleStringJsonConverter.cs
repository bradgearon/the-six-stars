using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Local models sometimes return a small array/object for a field the schema described as a string
    /// (for example currentDesires: ["protect the Last Cat", "study mercy"]). This converter keeps
    /// daily/background JSON refreshes from failing by flattening those values into compact strings.
    /// </summary>
    public sealed class FlexibleStringJsonConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString() ?? string.Empty;
                case JsonTokenType.Null:
                    return string.Empty;
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                    using (JsonDocument scalar = JsonDocument.ParseValue(ref reader))
                    {
                        return JsonElementToText(scalar.RootElement);
                    }
                case JsonTokenType.StartArray:
                case JsonTokenType.StartObject:
                    using (JsonDocument doc = JsonDocument.ParseValue(ref reader))
                    {
                        return JsonElementToText(doc.RootElement);
                    }
                default:
                    using (JsonDocument fallback = JsonDocument.ParseValue(ref reader))
                    {
                        return JsonElementToText(fallback.RootElement);
                    }
            }
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value ?? string.Empty);
        }

        private static string JsonElementToText(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString() ?? string.Empty;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return element.GetRawText();
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return string.Empty;
                case JsonValueKind.Array:
                    List<string> values = new List<string>();
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        string text = JsonElementToText(item).Trim();
                        if (!string.IsNullOrWhiteSpace(text)) values.Add(text);
                    }
                    return string.Join("; ", values);
                case JsonValueKind.Object:
                    string preferred = FirstObjectString(element, "vector", "text", "summary", "value", "description", "name", "desire", "feeling", "intent");
                    if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
                    return element.GetRawText();
                default:
                    return element.GetRawText();
            }
        }

        private static string FirstObjectString(JsonElement element, params string[] names)
        {
            foreach (string name in names)
            {
                if (element.TryGetProperty(name, out JsonElement value))
                {
                    string text = JsonElementToText(value).Trim();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
            return string.Empty;
        }
    }
}
