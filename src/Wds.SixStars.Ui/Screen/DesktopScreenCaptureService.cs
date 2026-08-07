using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wds.SixStars.Api.Screen;

namespace Wds.SixStars.Ui.Screen;

/// <summary>
/// Captures the whole virtual desktop as a JPEG and records active-window metadata.
/// Kept in the WPF app because this is Windows-desktop-specific. The API only depends on IScreenImageCaptureService.
/// </summary>
public sealed class DesktopScreenCaptureService : IScreenImageCaptureService
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SRCCOPY = 0x00CC0020;
    private const int MaxVisionImageWidth = 1024;

    public Task<ScreenImageCapture> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ActiveWindowInfo active = GetActiveWindowInfo();
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int height = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        IntPtr bitmap = CreateCompatibleBitmap(screenDc, width, height);
        IntPtr oldObject = SelectObject(memoryDc, bitmap);

        try
        {
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SRCCOPY))
            {
                throw new InvalidOperationException("BitBlt failed while capturing desktop.");
            }

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();

            // Vision models do not need the full desktop resolution. Keeping the image compact
            // avoids oversized OpenAI-compatible JSON posts and makes llama.cpp/mmproj vision faster.
            if (source.PixelWidth > MaxVisionImageWidth)
            {
                double scale = MaxVisionImageWidth / (double)source.PixelWidth;
                TransformedBitmap resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                resized.Freeze();
                source = resized;
            }

            JpegBitmapEncoder encoder = new JpegBitmapEncoder { QualityLevel = 70 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using MemoryStream stream = new MemoryStream();
            encoder.Save(stream);

            return Task.FromResult(new ScreenImageCapture
            {
                CapturedAt = DateTimeOffset.UtcNow,
                ImageBytes = stream.ToArray(),
                MimeType = "image/jpeg",
                ImageReference = "desktop-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Width = source.PixelWidth,
                Height = source.PixelHeight,
                ActiveApp = active.ProcessName,
                ActiveProcessName = active.ProcessName,
                ActiveWindowTitle = active.Title
            });
        }
        finally
        {
            SelectObject(memoryDc, oldObject);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static ActiveWindowInfo GetActiveWindowInfo()
    {
        IntPtr hwnd = GetForegroundWindow();
        string title = string.Empty;
        string processName = string.Empty;
        if (hwnd != IntPtr.Zero)
        {
            int length = GetWindowTextLength(hwnd);
            StringBuilder builder = new StringBuilder(Math.Max(256, length + 1));
            GetWindowText(hwnd, builder, builder.Capacity);
            title = builder.ToString();
            GetWindowThreadProcessId(hwnd, out int processId);
            try
            {
                using Process process = Process.GetProcessById(processId);
                processName = process.ProcessName;
            }
            catch
            {
                processName = string.Empty;
            }
        }

        return new ActiveWindowInfo(title, processName);
    }

    private readonly record struct ActiveWindowInfo(string Title, string ProcessName);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
}
