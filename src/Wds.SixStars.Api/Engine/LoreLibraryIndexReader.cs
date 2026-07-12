using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Wds.SixStars.Api.Engine
{
    /// <summary>
    /// Reads the compact, Star-safe markdown indexes used by information follow-up requests.
    /// The folder names intentionally match the content-backed informationNeeds data types.
    /// Dynamic types such as history, quotes, markers, and screen remain in saved/runtime state.
    /// </summary>
    internal static class LoreLibraryIndexReader
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, CachedIndex> Cache = new Dictionary<string, CachedIndex>(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] ContentBackedTypes = { "lore", "bestiary", "characters", "world", "story" };

        public static bool IsContentBackedType(string? dataType)
        {
            string normalized = Normalize(dataType);
            return ContentBackedTypes.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
        }

        public static string GetFolderName(string? dataType)
        {
            string normalized = Normalize(dataType);
            return IsContentBackedType(normalized) ? normalized : string.Empty;
        }

        public static bool TryRead(string? dataType, out LoreLibraryIndexDocument document)
        {
            document = new LoreLibraryIndexDocument();
            string folder = GetFolderName(dataType);
            if (string.IsNullOrWhiteSpace(folder)) return false;

            string? root = ResolveDocsRoot();
            if (string.IsNullOrWhiteSpace(root)) return false;

            string path = Path.Combine(root, folder, "index.md");
            if (!File.Exists(path)) return false;

            try
            {
                DateTime lastWriteUtc = File.GetLastWriteTimeUtc(path);
                lock (Gate)
                {
                    if (Cache.TryGetValue(path, out CachedIndex? cached) && cached.LastWriteUtc == lastWriteUtc)
                    {
                        document = cached.Document;
                        return !string.IsNullOrWhiteSpace(document.Content);
                    }

                    string content = File.ReadAllText(path);
                    document = new LoreLibraryIndexDocument
                    {
                        DataType = folder,
                        FolderName = folder,
                        RelativePath = folder + "/index.md",
                        FullPath = path,
                        Content = content,
                        CharacterCount = content.Length
                    };
                    Cache[path] = new CachedIndex { LastWriteUtc = lastWriteUtc, Document = document };
                    return !string.IsNullOrWhiteSpace(content);
                }
            }
            catch
            {
                return false;
            }
        }

        private static string Normalize(string? value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string? ResolveDocsRoot()
        {
            List<string> starts = new List<string>();
            if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory)) starts.Add(AppContext.BaseDirectory);
            string current = Directory.GetCurrentDirectory();
            if (!string.IsNullOrWhiteSpace(current)) starts.Add(current);

            foreach (string start in starts.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                DirectoryInfo? dir;
                try { dir = new DirectoryInfo(start); }
                catch { continue; }

                for (int depth = 0; depth < 9 && dir != null; depth++, dir = dir.Parent)
                {
                    string outputCandidate = Path.Combine(dir.FullName, "Content", "LoreLibrary", "docs");
                    if (Directory.Exists(outputCandidate)) return outputCandidate;

                    string sourceCandidate = Path.Combine(dir.FullName, "src", "Wds.SixStars.Api", "Content", "LoreLibrary", "docs");
                    if (Directory.Exists(sourceCandidate)) return sourceCandidate;
                }
            }

            return null;
        }

        private sealed class CachedIndex
        {
            public DateTime LastWriteUtc { get; set; }
            public LoreLibraryIndexDocument Document { get; set; } = new LoreLibraryIndexDocument();
        }
    }

    internal sealed class LoreLibraryIndexDocument
    {
        public string DataType { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public int CharacterCount { get; set; }
    }
}
