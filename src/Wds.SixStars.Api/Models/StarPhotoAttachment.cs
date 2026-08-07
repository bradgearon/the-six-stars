using System;

namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// Unity-compatible photo attachment passed into a Star request. Hosts may use the raw bytes for
    /// vision summarization, then pass PhotoSummary into the prompt so text-only Star LLMs can react.
    /// </summary>
    public sealed class StarPhotoAttachment
    {
        public byte[] ImageBytes { get; set; } = Array.Empty<byte>();
        public string MimeType { get; set; } = "image/jpeg";
        public string Caption { get; set; } = string.Empty;
        public string ImageReference { get; set; } = string.Empty;
        public string PhotoSummary { get; set; } = string.Empty;
        public string PhotoError { get; set; } = string.Empty;
        public bool IsAttached => ImageBytes != null && ImageBytes.Length > 0;
    }
}
