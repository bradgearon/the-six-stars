using System;

namespace Wds.SixStars.Api.Screen
{
    public sealed class ScreenImageCapture
    {
        public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
        public byte[] ImageBytes { get; set; } = Array.Empty<byte>();
        public string MimeType { get; set; } = "image/jpeg";
        public string ImageReference { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public string ActiveApp { get; set; } = string.Empty;
        public string ActiveWindowTitle { get; set; } = string.Empty;
        public string ActiveProcessName { get; set; } = string.Empty;

        public string ToDataUrl()
        {
            string mime = string.IsNullOrWhiteSpace(MimeType) ? "image/jpeg" : MimeType;
            return "data:" + mime + ";base64," + Convert.ToBase64String(ImageBytes ?? Array.Empty<byte>());
        }
    }
}
