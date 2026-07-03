using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// Streaming voice generator that produces audio chunks as they are generated.
    /// This allows for faster response times and the ability to cancel ongoing generation.
    /// </summary>
    public interface IStreamingVoiceGenerator : IDisposable
    {
        /// <summary>
        /// Fired when a chunk of audio is ready to play.
        /// </summary>
        event EventHandler<AudioChunkEventArgs>? AudioChunkReady;

        /// <summary>
        /// Fired when all chunks for the current request have been generated.
        /// </summary>
        event EventHandler<GenerationCompleteEventArgs>? GenerationComplete;

        /// <summary>
        /// Fired when an error occurs during generation.
        /// </summary>
        event EventHandler<GenerationErrorEventArgs>? GenerationError;

        /// <summary>
        /// Start generating voice for the given request. Audio chunks will be delivered via events.
        /// </summary>
        Task GenerateStreamingAsync(VoiceSynthesisRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// Cancel any ongoing generation.
        /// </summary>
        void CancelGeneration();

        /// <summary>
        /// Check if the generator is currently connected and ready.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Connect to the WebSocket endpoint. This is called automatically if needed.
        /// </summary>
        Task ConnectAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Disconnect from the WebSocket endpoint.
        /// </summary>
        Task DisconnectAsync();
    }

    public sealed class AudioChunkEventArgs : EventArgs
    {
        public int ChunkIndex { get; set; }
        public int TotalChunks { get; set; }
        public byte[] AudioData { get; set; } = Array.Empty<byte>();
        public string MimeType { get; set; } = "audio/wav";
        public string Text { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string VoiceId { get; set; } = string.Empty;
    }

    public sealed class GenerationCompleteEventArgs : EventArgs
    {
        public int TotalChunks { get; set; }
        public double TotalDurationMs { get; set; }
        public string StarId { get; set; } = string.Empty;
        public bool WasCancelled { get; set; }
    }

    public sealed class GenerationErrorEventArgs : EventArgs
    {
        public string ErrorMessage { get; set; } = string.Empty;
        public Exception? Exception { get; set; }
        public string StarId { get; set; } = string.Empty;
    }
}
