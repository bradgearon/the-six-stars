using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.PreparedSpeech
{
    public sealed class PreparedSpeechArtifactStore
    {
        private readonly PreparedSpeechOptions _options;
        private readonly string _root;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public event EventHandler<PreparedSpeechArtifactReadyEventArgs>? ArtifactReady;
        public string RootDirectory => _root;

        public PreparedSpeechArtifactStore(PreparedSpeechOptions? options = null)
        {
            _options = options ?? new PreparedSpeechOptions();
            _root = ResolveRoot(_options.StateRoot);
            Directory.CreateDirectory(_root);
        }

        public async Task<PreparedSpeechArtifact> SaveAsync(
            string intentId,
            string starId,
            string spokenText,
            string voiceVersion,
            IEnumerable<PreparedSpeechChunkData> chunks,
            CancellationToken cancellationToken)
        {
            if (!_options.Enabled) throw new InvalidOperationException("Prepared speech artifact storage is disabled.");
            List<PreparedSpeechChunkData> chunkList = (chunks ?? Enumerable.Empty<PreparedSpeechChunkData>())
                .Where(x => x != null && x.Audio != null && x.Audio.Length > 0)
                .OrderBy(x => x.ChunkIndex)
                .ToList();
            if (chunkList.Count == 0) throw new InvalidOperationException("Prepared speech did not contain any WAV chunks.");

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            PreparedSpeechArtifact artifact;
            try
            {
                string safeIntent = SafeName(string.IsNullOrWhiteSpace(intentId) ? Guid.NewGuid().ToString("N") : intentId);
                string textHash = HashText(spokenText ?? string.Empty);
                string artifactId = safeIntent + "-" + textHash.Substring(0, Math.Min(12, textHash.Length));
                string folder = Path.Combine(_root, artifactId);
                string stagingFolder = folder + ".staging-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(stagingFolder);

                artifact = new PreparedSpeechArtifact
                {
                    ArtifactId = artifactId,
                    IntentId = intentId ?? string.Empty,
                    StarId = starId ?? string.Empty,
                    SpokenText = spokenText ?? string.Empty,
                    TextHash = textHash,
                    VoiceVersion = voiceVersion ?? string.Empty,
                    Status = "ready",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                try
                {
                    foreach (PreparedSpeechChunkData chunk in chunkList)
                    {
                        int index = Math.Max(0, chunk.ChunkIndex);
                        string fileName = "chunk-" + index.ToString("000") + ".wav";
                        string path = Path.Combine(stagingFolder, fileName);
                        await WriteBytesAsync(path, chunk.Audio, cancellationToken).ConfigureAwait(false);
                        string sha = HashBytes(chunk.Audio);
                        artifact.Chunks.Add(new PreparedSpeechChunkManifest
                        {
                            ChunkIndex = index,
                            TotalChunks = Math.Max(chunkList.Count, chunk.TotalChunks),
                            MimeType = string.IsNullOrWhiteSpace(chunk.MimeType) ? "audio/wav" : chunk.MimeType,
                            FileName = fileName,
                            Url = "/api/prepared-speech/" + Uri.EscapeDataString(artifactId) + "/chunks/" + index,
                            Sha256 = sha,
                            ByteLength = chunk.Audio.Length,
                            SpeechActivitySegmentsMs = chunk.SpeechActivitySegmentsMs ?? string.Empty
                        });
                    }

                    await WriteMetadataAsync(stagingFolder, artifact, cancellationToken).ConfigureAwait(false);
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                    Directory.Move(stagingFolder, folder);
                }
                catch
                {
                    try { if (Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, true); } catch { }
                    throw;
                }

                await DeleteOtherArtifactsForIntentUnsafeAsync(intentId, artifactId, cancellationToken).ConfigureAwait(false);
                await CleanupUnsafeAsync(starId, cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }

            ArtifactReady?.Invoke(this, new PreparedSpeechArtifactReadyEventArgs { Artifact = artifact });
            return artifact;
        }

        public async Task<PreparedSpeechManifest> GetManifestAsync(string? starId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                List<PreparedSpeechArtifact> artifacts = new List<PreparedSpeechArtifact>();
                if (!Directory.Exists(_root)) return new PreparedSpeechManifest { GeneratedAt = DateTimeOffset.UtcNow };
                foreach (string folder in Directory.GetDirectories(_root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsStagingFolder(folder))
                    {
                        try { Directory.Delete(folder, true); } catch { }
                        continue;
                    }
                    PreparedSpeechArtifact? artifact = await ReadMetadataAsync(folder, cancellationToken).ConfigureAwait(false);
                    if (artifact == null) continue;
                    if (!string.IsNullOrWhiteSpace(starId) && !string.Equals(artifact.StarId, starId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (DateTimeOffset.UtcNow - artifact.UpdatedAt > TimeSpan.FromHours(Math.Max(1, _options.MaximumArtifactAgeHours))) continue;
                    artifacts.Add(artifact);
                }
                return new PreparedSpeechManifest
                {
                    Ok = true,
                    GeneratedAt = DateTimeOffset.UtcNow,
                    Artifacts = artifacts.OrderByDescending(x => x.UpdatedAt).ToList()
                };
            }
            finally { _gate.Release(); }
        }

        public async Task<PreparedSpeechArtifact?> GetByIntentAsync(string intentId, CancellationToken cancellationToken)
        {
            PreparedSpeechManifest manifest = await GetManifestAsync(null, cancellationToken).ConfigureAwait(false);
            return manifest.Artifacts.FirstOrDefault(x => string.Equals(x.IntentId, intentId, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<byte[]?> ReadChunkAsync(string artifactId, int chunkIndex, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string folder = Path.Combine(_root, SafeName(artifactId));
                PreparedSpeechArtifact? artifact = await ReadMetadataAsync(folder, cancellationToken).ConfigureAwait(false);
                PreparedSpeechChunkManifest? chunk = artifact?.Chunks.FirstOrDefault(x => x.ChunkIndex == chunkIndex);
                if (chunk == null) return null;
                string path = Path.Combine(folder, SafeName(chunk.FileName));
                if (!File.Exists(path)) return null;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true))
                using (MemoryStream memory = new MemoryStream())
                {
                    await stream.CopyToAsync(memory, 8192, cancellationToken).ConfigureAwait(false);
                    return memory.ToArray();
                }
            }
            finally { _gate.Release(); }
        }

        public async Task DeleteByIntentAsync(string intentId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!Directory.Exists(_root)) return;
                foreach (string folder in Directory.GetDirectories(_root))
                {
                    PreparedSpeechArtifact? artifact = await ReadMetadataAsync(folder, cancellationToken).ConfigureAwait(false);
                    if (artifact != null && string.Equals(artifact.IntentId, intentId, StringComparison.OrdinalIgnoreCase))
                    {
                        try { Directory.Delete(folder, true); } catch { }
                    }
                }
            }
            finally { _gate.Release(); }
        }

        private async Task DeleteOtherArtifactsForIntentUnsafeAsync(string intentId, string keepArtifactId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(intentId) || !Directory.Exists(_root)) return;
            foreach (string folder in Directory.GetDirectories(_root))
            {
                if (string.Equals(Path.GetFileName(folder), keepArtifactId, StringComparison.OrdinalIgnoreCase)) continue;
                PreparedSpeechArtifact? artifact = await ReadMetadataAsync(folder, cancellationToken).ConfigureAwait(false);
                if (artifact != null && string.Equals(artifact.IntentId, intentId, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(folder, true); } catch { }
                }
            }
        }

        private async Task CleanupUnsafeAsync(string starId, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(_root)) return;
            List<(string Folder, PreparedSpeechArtifact Artifact)> artifacts = new List<(string, PreparedSpeechArtifact)>();
            foreach (string folder in Directory.GetDirectories(_root))
            {
                if (IsStagingFolder(folder))
                {
                    try { Directory.Delete(folder, true); } catch { }
                    continue;
                }
                PreparedSpeechArtifact? artifact = await ReadMetadataAsync(folder, cancellationToken).ConfigureAwait(false);
                if (artifact == null) continue;
                if (DateTimeOffset.UtcNow - artifact.UpdatedAt > TimeSpan.FromHours(Math.Max(1, _options.MaximumArtifactAgeHours)))
                {
                    try { Directory.Delete(folder, true); } catch { }
                    continue;
                }
                if (string.Equals(artifact.StarId, starId, StringComparison.OrdinalIgnoreCase)) artifacts.Add((folder, artifact));
            }
            foreach ((string Folder, PreparedSpeechArtifact Artifact) extra in artifacts.OrderByDescending(x => x.Artifact.UpdatedAt).Skip(Math.Max(1, _options.MaximumArtifactsPerStar)))
            {
                try { Directory.Delete(extra.Folder, true); } catch { }
            }
        }

        private async Task<PreparedSpeechArtifact?> ReadMetadataAsync(string folder, CancellationToken cancellationToken)
        {
            string path = Path.Combine(folder, "metadata.json");
            if (!File.Exists(path)) return null;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, true))
                {
                    return await JsonSerializer.DeserializeAsync<PreparedSpeechArtifact>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
                }
            }
            catch { return null; }
        }

        private async Task WriteMetadataAsync(string folder, PreparedSpeechArtifact artifact, CancellationToken cancellationToken)
        {
            string path = Path.Combine(folder, "metadata.json");
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, true))
            {
                await JsonSerializer.SerializeAsync(stream, artifact, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
        {
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, true))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static string HashText(string text) => HashBytes(Encoding.UTF8.GetBytes(text ?? string.Empty));
        private static string HashBytes(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes ?? Array.Empty<byte>());
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static bool IsStagingFolder(string folder)
        {
            return Path.GetFileName(folder).IndexOf(".staging-", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SafeName(string? value)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
            StringBuilder safe = new StringBuilder(Math.Min(180, text.Length));
            foreach (char c in text)
            {
                if (safe.Length >= 180) break;
                safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            }
            string result = safe.ToString().Trim();
            if (string.IsNullOrWhiteSpace(result) || result == "." || result == "..") return "default";
            return result;
        }

        private static string ResolveRoot(string? configured)
        {
            string root = string.IsNullOrWhiteSpace(configured) ? "state/prepared-star-speech" : configured.Trim();
            if (Path.IsPathRooted(root)) return root;
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "SixStars.sln")) || Directory.Exists(Path.Combine(dir.FullName, "src")))
                {
                    return Path.GetFullPath(Path.Combine(dir.FullName, root));
                }
                dir = dir.Parent;
            }
            return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, root));
        }
    }
}
