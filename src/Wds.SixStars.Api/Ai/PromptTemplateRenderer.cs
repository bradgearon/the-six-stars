using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using Scriban;
using Scriban.Runtime;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Renders prompt templates with Scriban. Callers pass one JSON-compatible object;
    /// the renderer serializes it once, exposes it to the template as <c>data</c>, and
    /// lets the .sbn file decide where every value belongs.
    /// </summary>
    public sealed class PromptTemplateRenderer
    {
        private sealed class CachedTemplate
        {
            public long LastWriteUtcTicks { get; set; }
            public Template Template { get; set; } = null!;
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly string _templateDirectory;
        private readonly ConcurrentDictionary<string, CachedTemplate> _templateCache =
            new ConcurrentDictionary<string, CachedTemplate>(StringComparer.OrdinalIgnoreCase);

        private PromptTemplateRenderer(string templateDirectory)
        {
            _templateDirectory = templateDirectory;
        }

        public static PromptTemplateRenderer CreateDefault()
        {
            string baseDirectory = AppContext.BaseDirectory;
            string outputPath = Path.Combine(baseDirectory, "Content", "PromptTemplates");
            if (Directory.Exists(outputPath)) return new PromptTemplateRenderer(outputPath);

            // Dev fallback for running from source without a prior copy-to-output step.
            DirectoryInfo? dir = new DirectoryInfo(baseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string sourcePath = Path.Combine(dir.FullName, "src", "Wds.SixStars.Api", "Content", "PromptTemplates");
                if (Directory.Exists(sourcePath)) return new PromptTemplateRenderer(sourcePath);
            }

            return new PromptTemplateRenderer(outputPath);
        }

        public string Render(string templateName, object? model)
        {
            if (string.IsNullOrWhiteSpace(templateName)) throw new ArgumentException("Template name is required.", nameof(templateName));

            string path = Path.Combine(_templateDirectory, templateName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Prompt template was not found: " + path, path);
            }

            Template template = GetTemplate(path);
            JsonElement json = JsonSerializer.SerializeToElement(model ?? new { }, JsonOptions);

            ScriptObject globals = new ScriptObject();
            globals.SetValue("data", ConvertJson(json), true);

            TemplateContext context = new TemplateContext
            {
                StrictVariables = true,
                EnableRelaxedMemberAccess = false,
                EnableRelaxedIndexerAccess = false,
                LoopLimit = 1000,
                RecursiveLimit = 32
            };
            context.PushGlobal(globals);

            string rendered = template.Render(context);
            return rendered.Trim() + Environment.NewLine;
        }

        private Template GetTemplate(string path)
        {
            long stamp = File.GetLastWriteTimeUtc(path).Ticks;
            if (_templateCache.TryGetValue(path, out CachedTemplate? cached) && cached.LastWriteUtcTicks == stamp)
            {
                return cached.Template;
            }

            string source = File.ReadAllText(path);
            Template parsed = Template.Parse(source, path);
            if (parsed.HasErrors)
            {
                string errors = string.Join(Environment.NewLine, parsed.Messages.Select(x => x.ToString()));
                throw new InvalidOperationException("Prompt template could not be parsed: " + path + Environment.NewLine + errors);
            }

            CachedTemplate replacement = new CachedTemplate
            {
                LastWriteUtcTicks = stamp,
                Template = parsed
            };
            _templateCache[path] = replacement;
            return parsed;
        }

        private static object? ConvertJson(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    ScriptObject obj = new ScriptObject();
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        obj.SetValue(property.Name, ConvertJson(property.Value), false);
                    }
                    return obj;

                case JsonValueKind.Array:
                    ScriptArray array = new ScriptArray();
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        array.Add(ConvertJson(item));
                    }
                    return array;

                case JsonValueKind.String:
                    return element.GetString() ?? string.Empty;

                case JsonValueKind.Number:
                    if (element.TryGetInt64(out long int64)) return int64;
                    if (element.TryGetDecimal(out decimal number)) return number;
                    return element.GetDouble();

                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                default:
                    return null;
            }
        }
    }
}
