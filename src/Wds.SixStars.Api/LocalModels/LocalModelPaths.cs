using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Wds.SixStars.Api.LocalModels
{
    public static class LocalModelPaths
    {
        public static string FindRepositoryRoot(string? startDirectory = null)
        {
            DirectoryInfo? directory = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SixStars.sln")) || Directory.Exists(Path.Combine(directory.FullName, "models")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return Directory.GetCurrentDirectory();
        }

        public static string ResolveModelsRoot(string? configuredRoot = null)
        {
            string? env = Environment.GetEnvironmentVariable("SIX_STARS_MODELS_ROOT");
            string root = !string.IsNullOrWhiteSpace(env) ? env! : (!string.IsNullOrWhiteSpace(configuredRoot) ? configuredRoot! : "models");
            if (Path.IsPathRooted(root)) return Path.GetFullPath(root);
            return Path.GetFullPath(Path.Combine(FindRepositoryRoot(), root));
        }

        public static LocalModelServiceOptions BuildDefaultServiceOptions(string? configuredModelsRoot = null)
        {
            string modelsRoot = ResolveModelsRoot(configuredModelsRoot);
            string hfHome = Path.Combine(modelsRoot, "hf");
            string hfHub = Path.Combine(hfHome, "hub");

            return new LocalModelServiceOptions
            {
                ModelsRoot = modelsRoot,
                HuggingFaceHome = hfHome,
                HuggingFaceHubCache = hfHub
            };
        }

        public static void ApplyEnvironment(LocalModelServiceOptions options)
        {
            Directory.CreateDirectory(options.ModelsRoot);
            Directory.CreateDirectory(Path.Combine(options.ModelsRoot, "llm"));
            Directory.CreateDirectory(Path.Combine(options.ModelsRoot, "tts"));
            Directory.CreateDirectory(Path.Combine(options.ModelsRoot, "asr"));
            Directory.CreateDirectory(options.HuggingFaceHome);
            Directory.CreateDirectory(options.HuggingFaceHubCache);

            SetIfEmpty("SIX_STARS_MODELS_ROOT", options.ModelsRoot);
            SetIfEmpty("HF_HOME", options.HuggingFaceHome);
            SetIfEmpty("HF_HUB_CACHE", options.HuggingFaceHubCache);
        }

        public static string ResolveLocalModelPath(string category, string modelRef, string? explicitLocalPath = null, params string[] aliases)
        {
            if (!string.IsNullOrWhiteSpace(explicitLocalPath))
            {
                string path = ExpandPath(explicitLocalPath!);
                return Directory.Exists(path) || File.Exists(path) ? Path.GetFullPath(path) : path;
            }

            if (string.IsNullOrWhiteSpace(modelRef)) return string.Empty;
            string trimmed = modelRef.Trim();
            if (Path.IsPathRooted(trimmed) || trimmed.StartsWith(".", StringComparison.Ordinal) || trimmed.Contains('\\'))
            {
                string path = ExpandPath(trimmed);
                if (Directory.Exists(path) || File.Exists(path)) return Path.GetFullPath(path);
            }

            string categoryRoot = Path.Combine(ResolveModelsRoot(), category);
            foreach (string name in CandidateNames(trimmed, aliases))
            {
                string path = Path.Combine(categoryRoot, name);
                if (Directory.Exists(path) || File.Exists(path)) return Path.GetFullPath(path);
            }

            return trimmed;
        }

        private static string ExpandPath(string path)
        {
            string expanded = Environment.ExpandEnvironmentVariables(path);
            if (Path.IsPathRooted(expanded)) return expanded;
            return Path.Combine(FindRepositoryRoot(), expanded);
        }

        private static string[] CandidateNames(string modelRef, string[] aliases)
        {
            string last = modelRef.Replace('\\', '/').TrimEnd('/');
            int slash = last.LastIndexOf('/');
            if (slash >= 0) last = last.Substring(slash + 1);
            string sanitized = Regex.Replace(last, "[^A-Za-z0-9._-]+", "-").Trim('-', '.', '_');
            return aliases
                .Concat(new[]
                {
                    sanitized,
                    sanitized.ToLowerInvariant(),
                    modelRef.Replace("/", "__").Replace("\\", "__"),
                    modelRef.Replace("/", "_").Replace("\\", "_")
                })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void SetIfEmpty(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
