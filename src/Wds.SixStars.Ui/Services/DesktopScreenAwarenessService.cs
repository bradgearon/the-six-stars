using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Screen;

namespace Wds.SixStars.Ui.Services;

internal sealed class DesktopScreenAwarenessService : IDisposable
{
    private readonly ScreenAwarenessLoop _loop;

    public DesktopScreenAwarenessService(ScreenAwarenessLoop loop)
    {
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
    }

    public bool IsRunning => _loop.IsRunning;
    public event EventHandler<ScreenCaptureSummary>? CaptureStored
    {
        add => _loop.CaptureStored += value;
        remove => _loop.CaptureStored -= value;
    }
    public event EventHandler<string>? StatusChanged
    {
        add => _loop.StatusChanged += value;
        remove => _loop.StatusChanged -= value;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _loop.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => _loop.StopAsync(cancellationToken);
    public Task<ScreenCaptureSummary> CaptureNowAsync(CancellationToken cancellationToken) => _loop.CaptureOnceAsync(cancellationToken);
    public void Dispose() => _loop.Dispose();
}
