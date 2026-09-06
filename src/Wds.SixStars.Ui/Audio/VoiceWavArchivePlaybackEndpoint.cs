using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Voice;

namespace Wds.SixStars.Ui.Audio;

/// <summary>
/// Playback endpoint decorator that saves every generated voice payload before sending it to the real playback endpoint.
/// This is intentionally in the WPF project because it is a desktop-dev-harness convenience, not a Unity requirement.
/// </summary>
public sealed class VoiceWavArchivePlaybackEndpoint : IWavePlaybackEndpoint, IAudioOutputKeepAliveEndpoint, IStreamingWavePlaybackEndpoint
{
    private readonly IWavePlaybackEndpoint _inner;
    private readonly string _outputDirectory;
    private readonly bool _writeMetadataText;
    private int _sequence;

    public VoiceWavArchivePlaybackEndpoint(
        IWavePlaybackEndpoint inner,
        string? outputDirectory = null,
        bool writeMetadataText = true)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _outputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Directory.GetCurrentDirectory()
            : Environment.ExpandEnvironmentVariables(outputDirectory);
        _writeMetadataText = writeMetadataText;
    }


    public IDisposable StartOutputKeepAlive(string reason, CancellationToken cancellationToken)
    {
        if (_inner is IAudioOutputKeepAliveEndpoint keepAliveEndpoint)
        {
            return keepAliveEndpoint.StartOutputKeepAlive(reason, cancellationToken);
        }

        return NoopDisposable.Instance;
    }

    public async Task PlayWavAsync(VoiceSynthesisResult voiceAudio, CancellationToken cancellationToken)
    {
        SaveGeneratedAudio(voiceAudio);
        await _inner.PlayWavAsync(voiceAudio, cancellationToken).ConfigureAwait(false);
    }

    public IStreamingWavePlaybackSession CreateStreamingSession(string reason)
    {
        if (_inner is IStreamingWavePlaybackEndpoint streamingEndpoint)
        {
            return new ArchiveStreamingSession(this, streamingEndpoint.CreateStreamingSession(reason));
        }

        return new LegacyArchiveStreamingSession(this);
    }

    private void SaveGeneratedAudio(VoiceSynthesisResult voiceAudio)
    {
        byte[] audio = voiceAudio.WavAudio ?? Array.Empty<byte>();
        if (audio.Length == 0)
        {
            return;
        }

        Directory.CreateDirectory(_outputDirectory);

        int sequence = Interlocked.Increment(ref _sequence);
        string timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        string starId = SanitizeFileName(string.IsNullOrWhiteSpace(voiceAudio.StarId) ? "unknown-star" : voiceAudio.StarId);
        string voiceId = SanitizeFileName(string.IsNullOrWhiteSpace(voiceAudio.VoiceId) ? "unknown-voice" : voiceAudio.VoiceId);
        string source = SanitizeFileName(string.IsNullOrWhiteSpace(voiceAudio.Source) ? "tts" : voiceAudio.Source);
        string extension = GuessExtension(voiceAudio.MimeType);

        string baseName = $"sixstars-{timestamp}-{sequence:000}-{starId}-{voiceId}-{source}";
        string audioPath = Path.Combine(_outputDirectory, baseName + extension);
        File.WriteAllBytes(audioPath, audio);

        if (_writeMetadataText)
        {
            string metadataPath = Path.Combine(_outputDirectory, baseName + ".txt");
            File.WriteAllText(metadataPath, BuildMetadataText(voiceAudio, audioPath, audio.Length), Encoding.UTF8);
        }

        System.Diagnostics.Debug.WriteLine($"[VoiceArchive] Saved generated voice audio: {audioPath} ({audio.Length} bytes)");
    }

    private static string BuildMetadataText(VoiceSynthesisResult voiceAudio, string audioPath, int byteCount)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("Six Stars generated voice audio");
        builder.AppendLine("================================");
        builder.AppendLine("SavedAt: " + DateTimeOffset.Now.ToString("O"));
        builder.AppendLine("AudioPath: " + audioPath);
        builder.AppendLine("StarId: " + voiceAudio.StarId);
        builder.AppendLine("VoiceId: " + voiceAudio.VoiceId);
        builder.AppendLine("Source: " + voiceAudio.Source);
        builder.AppendLine("MimeType: " + voiceAudio.MimeType);
        builder.AppendLine("ByteCount: " + byteCount);
        builder.AppendLine("GeneratedAt: " + voiceAudio.GeneratedAt.ToString("O"));
        builder.AppendLine("DurationMs: " + voiceAudio.DurationMs.ToString("0.###"));
        builder.AppendLine();
        builder.AppendLine("Spoken text / chunk text:");
        builder.AppendLine(voiceAudio.Instructions ?? string.Empty);
        return builder.ToString();
    }

    private static string GuessExtension(string? mimeType)
    {
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            if (mimeType.IndexOf("mp3", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mimeType.IndexOf("mpeg", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ".mp3";
            }

            if (mimeType.IndexOf("ogg", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ".ogg";
            }
        }

        return ".wav";
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '-');
        }

        value = value.Trim();
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private sealed class ArchiveStreamingSession : IStreamingWavePlaybackSession
    {
        private readonly VoiceWavArchivePlaybackEndpoint _owner;
        private readonly IStreamingWavePlaybackSession _inner;

        public ArchiveStreamingSession(VoiceWavArchivePlaybackEndpoint owner, IStreamingWavePlaybackSession inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public Task EnqueueAsync(VoiceSynthesisResult voiceAudio, CancellationToken cancellationToken)
        {
            _owner.SaveGeneratedAudio(voiceAudio);
            return _inner.EnqueueAsync(voiceAudio, cancellationToken);
        }

        public Task CompleteAsync(CancellationToken cancellationToken)
            => _inner.CompleteAsync(cancellationToken);

        public void Cancel() => _inner.Cancel();

        public void Dispose() => _inner.Dispose();
    }

    private sealed class LegacyArchiveStreamingSession : IStreamingWavePlaybackSession
    {
        private readonly VoiceWavArchivePlaybackEndpoint _owner;

        public LegacyArchiveStreamingSession(VoiceWavArchivePlaybackEndpoint owner)
        {
            _owner = owner;
        }

        public Task EnqueueAsync(VoiceSynthesisResult voiceAudio, CancellationToken cancellationToken)
            => _owner.PlayWavAsync(voiceAudio, cancellationToken);

        public Task CompleteAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Cancel()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new NoopDisposable();

        public void Dispose()
        {
        }
    }
}
