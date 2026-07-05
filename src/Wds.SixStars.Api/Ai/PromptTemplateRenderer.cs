using System;
using System.Collections.Generic;
using System.IO;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Small Scriban-style prompt renderer used by the runtime prompt builder.
    /// Templates live under Content/PromptTemplates and use {{ Key }} placeholders.
    /// The data is still assembled in C# so the existing prompt behavior can be moved
    /// into editable .sbn files without adding a hard external runtime dependency.
    /// </summary>
    public sealed class PromptTemplateRenderer
    {
        private readonly string _templateDirectory;

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

        public string Render(string templateName, IReadOnlyDictionary<string, string> values)
        {
            string path = Path.Combine(_templateDirectory, templateName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Prompt template was not found: " + path, path);
            }

            string template = File.ReadAllText(path);
            foreach (KeyValuePair<string, string> pair in values)
            {
                template = template.Replace("{{ " + pair.Key + " }}", pair.Value ?? string.Empty);
                template = template.Replace("{{" + pair.Key + "}}", pair.Value ?? string.Empty);
            }

            return template.Trim() + Environment.NewLine;
        }
    }
}
