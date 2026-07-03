using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Screen
{
    /// <summary>
    /// Captures a desktop/game screen image. Desktop implements this with Win32/WPF; Unity can implement it
    /// by sending a Texture2D/screenshot encoded as JPEG/PNG. The API only needs bytes + basic app metadata.
    /// </summary>
    public interface IScreenImageCaptureService
    {
        Task<ScreenImageCapture> CaptureAsync(CancellationToken cancellationToken);
    }
}
