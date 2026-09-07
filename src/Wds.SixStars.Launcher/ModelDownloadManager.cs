using Downloader;
using System.Diagnostics;

namespace Wds.SixStars.Launcher;

/// <summary>
/// Makes the model bundle self-installing. URLs live in config/models.json so a release can
/// replace or add mirrors without changing the native service manifests. Existing final files
/// are left alone; incomplete .download files are resumed by bezzad/Downloader.
/// </summary>
internal static class ModelDownloadManager
{
    private sealed record ModelFile(string Label, string? RelativePath, IReadOnlyList<string> Urls, bool Required);

    public static async Task EnsureModelsAsync(string root, ModelManifest manifest, CancellationToken cancellationToken = default)
    {
        var files = BuildModelFileList(manifest);
        if (files.Count == 0)
        {
            Console.WriteLine("No downloadable model files are configured in config/models.json.");
            return;
        }

        var missing = files
            .Where(x => !string.IsNullOrWhiteSpace(x.RelativePath))
            .Where(x => !IsUsableFile(Program.FullPath(root, x.RelativePath!)))
            .ToList();

        if (missing.Count == 0)
        {
            Console.WriteLine("Models: all configured model files are already present.");
            return;
        }

        Console.WriteLine($"Models: {missing.Count} file(s) need to be downloaded.");
        Console.WriteLine("Downloader: bezzad/Downloader multipart + auto-resume.");
        Console.WriteLine();

        foreach (var file in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureFileAsync(root, file, cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine("Models: all required downloads are ready.");
    }

    private static List<ModelFile> BuildModelFileList(ModelManifest manifest)
    {
        var result = new List<ModelFile>();
        var models = manifest.Models;

        if (models.Llm is { Enabled: true } llm)
        {
            result.Add(new ModelFile("LLM / Gemma 4", llm.LocalPath, Urls(llm.DownloadUrl, llm.DownloadUrls), Required: true));
        }

        if (models.Vision is { Enabled: true } vision)
        {
            result.Add(new ModelFile("Vision projector / Gemma 4 mmproj", vision.LocalPath, Urls(vision.DownloadUrl, vision.DownloadUrls), Required: false));
        }

        if (models.Asr is { Enabled: true } asr)
        {
            result.Add(new ModelFile("ASR / Whisper large-v3-turbo", asr.LocalPath, Urls(asr.DownloadUrl, asr.DownloadUrls), Required: true));
        }

        if (models.Tts is { Enabled: true } tts)
        {
            result.Add(new ModelFile("TTS / Qwen talker", tts.LocalPath, Urls(tts.DownloadUrl, tts.DownloadUrls), Required: true));
            result.Add(new ModelFile("TTS / Qwen tokenizer-codec", tts.CodecLocalPath, Urls(tts.CodecDownloadUrl, tts.CodecDownloadUrls), Required: true));
        }

        return result;
    }

    private static IReadOnlyList<string> Urls(string? legacySingleUrl, IEnumerable<string>? urls)
    {
        var result = new List<string>();
        if (urls != null)
        {
            foreach (var url in urls)
            {
                if (!string.IsNullOrWhiteSpace(url) && !result.Contains(url, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(url.Trim());
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(legacySingleUrl) && !result.Contains(legacySingleUrl, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(legacySingleUrl.Trim());
        }
        return result;
    }

    private static async Task EnsureFileAsync(string root, ModelFile file, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file.RelativePath))
        {
            if (file.Required) throw new InvalidOperationException(file.Label + " has no localPath configured in config/models.json.");
            return;
        }

        var destination = Program.FullPath(root, file.RelativePath);
        if (IsUsableFile(destination))
        {
            Console.WriteLine($"MODEL READY     {file.Label}: {destination}");
            return;
        }

        if (file.Urls.Count == 0)
        {
            if (file.Required)
            {
                throw new InvalidOperationException($"{file.Label} is missing and has no downloadUrls configured in config/models.json: {destination}");
            }

            Console.WriteLine($"MODEL OPTIONAL  {file.Label}: missing and no download URL is configured; continuing without it.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? root);
        Console.WriteLine($"MODEL DOWNLOAD  {file.Label}");
        Console.WriteLine($"                -> {destination}");
        Console.WriteLine($"                sources: {file.Urls.Count}");

        var config = new DownloadConfiguration
        {
            ChunkCount = 8,
            ParallelDownload = true,
            ParallelCount = 4,
            MaxTryAgainOnFailure = 5,
            EnableAutoResumeDownload = true,
            MaximumMemoryBufferBytes = 50L * 1024 * 1024,
            CheckDiskSizeBeforeDownload = true,
            HttpClientTimeout = (int)TimeSpan.FromMinutes(10).TotalMilliseconds
        };

        using var downloader = new DownloadService(config);
        var progressClock = Stopwatch.StartNew();
        long lastProgressPrintMs = -1000;
        var progressLock = new object();

        downloader.DownloadStarted += (_, e) =>
        {
            var size = e.TotalBytesToReceive > 0 ? FormatBytes(e.TotalBytesToReceive) : "unknown size";
            Console.WriteLine($"                remote size: {size}");
        };

        downloader.DownloadProgressChanged += (_, e) =>
        {
            var now = progressClock.ElapsedMilliseconds;
            lock (progressLock)
            {
                if (now - lastProgressPrintMs < 1000 && e.ProgressPercentage < 100) return;
                lastProgressPrintMs = now;
            }

            var total = e.TotalBytesToReceive > 0 ? FormatBytes(e.TotalBytesToReceive) : "?";
            Console.WriteLine(
                $"                {e.ProgressPercentage,6:0.0}%  {FormatBytes(e.ReceivedBytesSize)} / {total}  {FormatBytes((long)e.BytesPerSecondSpeed)}/s");
        };

        try
        {
            // Downloader accepts mirror URLs for the same file. With one URL this is an ordinary
            // multipart download; additional URLs in the manifest become automatic alternate sources.
            await downloader.DownloadFileTaskAsync(file.Urls.ToArray(), destination, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"Failed to download {file.Label}. The partial .download file is kept for automatic resume on the next launch. {ex.Message}",
                ex);
        }

        if (!IsUsableFile(destination))
        {
            throw new IOException($"Downloader completed but {file.Label} was not written correctly: {destination}");
        }

        Console.WriteLine($"MODEL READY     {file.Label}: {FormatBytes(new FileInfo(destination).Length)}");
    }

    private static bool IsUsableFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "?";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }
}
