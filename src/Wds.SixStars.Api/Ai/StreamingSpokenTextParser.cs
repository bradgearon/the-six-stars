using System;
using System.Collections.Generic;
using System.Text;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Converts the normal foreground LLM text stream into immutable speakable chunks. The model
    /// emits the section-sign delimiter after each complete thought; the delimiter is protocol only
    /// and never becomes part of displayed or spoken text.
    /// </summary>
    internal sealed class StreamingSpokenTextParser
    {
        public const char Delimiter = '§';

        private readonly StringBuilder _buffer = new StringBuilder();
        private readonly List<string> _parts = new List<string>();
        private readonly Action<string>? _onChunkReady;

        public StreamingSpokenTextParser(Action<string>? onChunkReady)
        {
            _onChunkReady = onChunkReady;
        }

        public IReadOnlyList<string> Parts => _parts;

        public void Append(string? delta)
        {
            if (string.IsNullOrEmpty(delta)) return;

            foreach (char ch in delta)
            {
                if (ch == Delimiter)
                {
                    EmitBufferedChunk();
                }
                else
                {
                    _buffer.Append(ch);
                }
            }
        }

        public void Complete()
        {
            // Robust fallback when a local model forgets the final delimiter. We still preserve
            // every earlier real-time chunk and flush only the unsent tail at EOS.
            EmitBufferedChunk();
        }

        private void EmitBufferedChunk()
        {
            string chunk = NormalizeChunk(_buffer.ToString());
            _buffer.Clear();
            if (string.IsNullOrWhiteSpace(chunk)) return;

            _parts.Add(chunk);
            _onChunkReady?.Invoke(chunk);
        }

        private static string NormalizeChunk(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
            while (normalized.Contains("  ", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("  ", " ");
            }
            return normalized;
        }
    }
}
