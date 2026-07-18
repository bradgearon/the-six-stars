using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.DailyState
{
    public sealed class StarDailyStateFileStore
    {
        private const string CanonicalSessionId = "local-dev";
        private readonly string _rootDirectory;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public StarDailyStateFileStore(string rootDirectory)
        {
            _rootDirectory = ResolveRoot(rootDirectory);
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
            _jsonOptions.Converters.Add(new JsonStringEnumConverter());
        }

        public string RootDirectory => _rootDirectory;

        public async Task<StarDailyStateRecord?> ReadCurrentAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string metaPath = BuildCurrentMetaPath(sessionId, starId);
                if (!File.Exists(metaPath))
                {
                    return await TryReadCurrentLegacyXmlOnlyAsync(sessionId, starId, "Daily Star state legacy XML loaded from current.xml. Metadata JSON was missing.", cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    using (FileStream stream = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true))
                    {
                        StarDailyStateRecord? record = await JsonSerializer.DeserializeAsync<StarDailyStateRecord>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
                        if (record == null)
                        {
                            return await TryReadCurrentLegacyXmlOnlyAsync(sessionId, starId, "Daily Star state legacy XML loaded from current.xml. Metadata JSON was empty.", cancellationToken).ConfigureAwait(false);
                        }

                        if (!string.IsNullOrWhiteSpace(record.FilePath) && File.Exists(record.FilePath))
                        {
                            string content = await ReadAllTextAsync(record.FilePath, cancellationToken).ConfigureAwait(false);
                            if (string.Equals(Path.GetExtension(record.FilePath), ".xml", StringComparison.OrdinalIgnoreCase)) record.XmlContent = content;
                            else record.JsonContent = content;
                        }
                        else
                        {
                            string currentJsonPath = BuildCurrentJsonPath(sessionId, starId);
                            if (File.Exists(currentJsonPath))
                            {
                                record.FilePath = currentJsonPath;
                                record.JsonContent = await ReadAllTextAsync(currentJsonPath, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                string currentXmlPath = BuildCurrentXmlPath(sessionId, starId);
                                if (File.Exists(currentXmlPath))
                                {
                                    record.FilePath = currentXmlPath;
                                    record.XmlContent = await ReadAllTextAsync(currentXmlPath, cancellationToken).ConfigureAwait(false);
                                }
                            }
                        }
                        return record;
                    }
                }
                catch (FileNotFoundException)
                {
                    return await TryReadCurrentLegacyXmlOnlyAsync(sessionId, starId, "Daily Star state legacy XML loaded from current.xml. Metadata JSON disappeared during read.", cancellationToken).ConfigureAwait(false);
                }
                catch (DirectoryNotFoundException)
                {
                    return await TryReadCurrentLegacyXmlOnlyAsync(sessionId, starId, "Daily Star state legacy XML loaded from current.xml. Metadata directory was missing.", cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return await TryReadCurrentLegacyXmlOnlyAsync(sessionId, starId, "Daily Star state legacy XML loaded from current.xml. Metadata JSON was unreadable.", cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> HasSlotAsync(string sessionId, string starId, string localDate, string slotId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return File.Exists(BuildSlotJsonPath(sessionId, starId, localDate, slotId));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<StarDailyStateRecord> WriteAsync(StarDailyStateRecord record, CancellationToken cancellationToken)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                record.SessionId = CanonicalSessionId;
                string starFolder = BuildStarFolder(record.SessionId, record.StarId);
                Directory.CreateDirectory(starFolder);
                string dayFolder = Path.Combine(starFolder, SafeName(record.LocalDate));
                Directory.CreateDirectory(dayFolder);

                string slotJsonPath = BuildSlotJsonPath(record.SessionId, record.StarId, record.LocalDate, record.SlotId);
                record.FilePath = slotJsonPath;
                string jsonContent = !string.IsNullOrWhiteSpace(record.JsonContent) ? record.JsonContent : (record.XmlContent ?? string.Empty);
                record.JsonContent = jsonContent;
                await WriteAllTextAsync(slotJsonPath, jsonContent, cancellationToken).ConfigureAwait(false);

                string currentJsonPath = BuildCurrentJsonPath(record.SessionId, record.StarId);
                await WriteAllTextAsync(currentJsonPath, jsonContent, cancellationToken).ConfigureAwait(false);

                string currentMetaPath = BuildCurrentMetaPath(record.SessionId, record.StarId);
                await WriteAllTextAsync(currentMetaPath, JsonSerializer.Serialize(record, _jsonOptions), cancellationToken).ConfigureAwait(false);

                string slotMetaPath = Path.ChangeExtension(slotJsonPath, ".meta.json");
                await WriteAllTextAsync(slotMetaPath, JsonSerializer.Serialize(record, _jsonOptions), cancellationToken).ConfigureAwait(false);

                return record;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task ClearStarAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string starFolder = BuildStarFolder(sessionId, starId);
                if (Directory.Exists(starFolder))
                {
                    Directory.Delete(starFolder, true);
                }
            }
            finally
            {
                _gate.Release();
            }
        }



        private async Task<StarDailyStateRecord?> TryReadCurrentLegacyXmlOnlyAsync(string sessionId, string starId, string summary, CancellationToken cancellationToken)
        {
            string currentXmlPath = BuildCurrentXmlPath(sessionId, starId);
            if (!File.Exists(currentXmlPath)) return null;
            string xml = await ReadAllTextAsync(currentXmlPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(xml)) return null;
            return new StarDailyStateRecord
            {
                SessionId = CanonicalSessionId,
                StarId = string.IsNullOrWhiteSpace(starId) ? "UnknownStar" : starId,
                LocalDate = DateTime.Now.ToString("yyyy-MM-dd"),
                SlotId = "current-xml-fallback",
                GeneratedAt = File.GetLastWriteTimeUtc(currentXmlPath),
                FilePath = currentXmlPath,
                Summary = summary,
                XmlContent = xml
            };
        }

        private string BuildCurrentJsonPath(string sessionId, string starId)
        {
            return Path.Combine(BuildStarFolder(sessionId, starId), "current.json");
        }

        private string BuildCurrentXmlPath(string sessionId, string starId)
        {
            return Path.Combine(BuildStarFolder(sessionId, starId), "current.xml");
        }

        private string BuildCurrentMetaPath(string sessionId, string starId)
        {
            return Path.Combine(BuildStarFolder(sessionId, starId), "current.meta.json");
        }

        private string BuildSlotJsonPath(string sessionId, string starId, string localDate, string slotId)
        {
            return Path.Combine(BuildStarFolder(sessionId, starId), SafeName(localDate), SafeName(slotId) + ".json");
        }

        private string BuildSlotXmlPath(string sessionId, string starId, string localDate, string slotId)
        {
            return Path.Combine(BuildStarFolder(sessionId, starId), SafeName(localDate), SafeName(slotId) + ".xml");
        }

        private string BuildStarFolder(string sessionId, string starId)
        {
            // Daily state belongs to the same single Star save regardless of which client asked.
            return Path.Combine(_rootDirectory, SafeName(starId));
        }

        private static string SafeName(string? value)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
            return text.Replace(' ', '_');
        }

        private static string ResolveRoot(string? configured)
        {
            string root = string.IsNullOrWhiteSpace(configured) ? "state/daily-star-state" : configured.Trim();
            if (Path.IsPathRooted(root)) return root;
            string projectRoot = FindProjectRoot(AppContext.BaseDirectory);
            return Path.GetFullPath(Path.Combine(projectRoot, root));
        }

        private static string FindProjectRoot(string start)
        {
            DirectoryInfo? dir = new DirectoryInfo(string.IsNullOrWhiteSpace(start) ? Environment.CurrentDirectory : start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "SixStars.sln")) || Directory.Exists(Path.Combine(dir.FullName, "src"))) return dir.FullName;
                dir = dir.Parent;
            }
            return Environment.CurrentDirectory;
        }

        private static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true))
            using (StreamReader reader = new StreamReader(stream))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        private static async Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, true))
            using (StreamWriter writer = new StreamWriter(stream))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteAsync(text ?? string.Empty).ConfigureAwait(false);
            }
        }
    }
}
