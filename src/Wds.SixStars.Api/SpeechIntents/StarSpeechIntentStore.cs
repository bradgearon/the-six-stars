using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.SpeechIntents
{
    internal sealed class StarSpeechIntentStore
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public string FilePath => _filePath;

        public StarSpeechIntentStore(string? configuredRoot)
        {
            string root = ResolveRoot(configuredRoot);
            Directory.CreateDirectory(root);
            _filePath = Path.Combine(root, "intents.json");
        }

        public async Task<T> WithLockAsync<T>(Func<List<StarSpeechIntent>, Task<T>> action, bool persist, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                List<StarSpeechIntent> values = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
                T result = await action(values).ConfigureAwait(false);
                if (persist) await WriteUnsafeAsync(values, cancellationToken).ConfigureAwait(false);
                return result;
            }
            finally { _gate.Release(); }
        }

        private async Task<List<StarSpeechIntent>> ReadUnsafeAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_filePath)) return new List<StarSpeechIntent>();
            try
            {
                using (FileStream stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, true))
                {
                    List<StarSpeechIntent>? values = await JsonSerializer.DeserializeAsync<List<StarSpeechIntent>>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
                    return values ?? new List<StarSpeechIntent>();
                }
            }
            catch (JsonException) { return new List<StarSpeechIntent>(); }
            catch (FileNotFoundException) { return new List<StarSpeechIntent>(); }
            catch (DirectoryNotFoundException) { return new List<StarSpeechIntent>(); }
        }

        private async Task WriteUnsafeAsync(List<StarSpeechIntent> values, CancellationToken cancellationToken)
        {
            string temp = _filePath + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? Environment.CurrentDirectory);
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                await JsonSerializer.SerializeAsync(stream, values, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(_filePath)) File.Delete(_filePath);
            File.Move(temp, _filePath);
        }

        private static string ResolveRoot(string? configured)
        {
            string root = string.IsNullOrWhiteSpace(configured) ? "state/star-speech-intents" : configured.Trim();
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
