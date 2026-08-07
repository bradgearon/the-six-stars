using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.PreparedSpeech
{
    public sealed class PreparedSpeechOptions
    {
        public bool Enabled { get; set; } = true;
        public string StateRoot { get; set; } = "state/prepared-star-speech";
        public int MaximumArtifactsPerStar { get; set; } = 3;
        public int MaximumArtifactAgeHours { get; set; } = 72;
    }

    public sealed class PreparedSpeechChunkData
    {
        public int ChunkIndex { get; set; }
        public int TotalChunks { get; set; } = 1;
        public string MimeType { get; set; } = "audio/wav";
        public byte[] Audio { get; set; } = Array.Empty<byte>();
        public string Sha256 { get; set; } = string.Empty;
        public string SpeechActivitySegmentsMs { get; set; } = string.Empty;
    }

    public sealed class PreparedSpeechChunkManifest
    {
        public int ChunkIndex { get; set; }
        public int TotalChunks { get; set; } = 1;
        public string MimeType { get; set; } = "audio/wav";
        public string Url { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public int ByteLength { get; set; }
        public string SpeechActivitySegmentsMs { get; set; } = string.Empty;
    }

    public sealed class PreparedSpeechArtifact
    {
        public string ArtifactId { get; set; } = string.Empty;
        public string IntentId { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string SpokenText { get; set; } = string.Empty;
        public string TextHash { get; set; } = string.Empty;
        public string VoiceVersion { get; set; } = string.Empty;
        public string Status { get; set; } = "ready";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<PreparedSpeechChunkManifest> Chunks { get; set; } = new List<PreparedSpeechChunkManifest>();
    }

    public sealed class PreparedSpeechManifest
    {
        public bool Ok { get; set; } = true;
        public DateTimeOffset GeneratedAt { get; set; }
        public List<PreparedSpeechArtifact> Artifacts { get; set; } = new List<PreparedSpeechArtifact>();
    }

    public sealed class PreparedSpeechArtifactReadyEventArgs : EventArgs
    {
        public PreparedSpeechArtifact Artifact { get; set; } = new PreparedSpeechArtifact();
    }
}
