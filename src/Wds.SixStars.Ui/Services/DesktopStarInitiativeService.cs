using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Ui.Services;

/// <summary>Owns randomized at-rest scheduling; request generation remains in the conversation service.</summary>
internal sealed class DesktopStarInitiativeService : IDisposable
{
    private readonly Func<bool> _enabled;
    private readonly Func<string, bool, Task> _request;
    private readonly Random _random = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public DesktopStarInitiativeService(Func<bool> enabled, Func<string, bool, Task> request)
    {
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _request = request ?? throw new ArgumentNullException(nameof(request));
    }

    public void Start()
    {
        if (_loopTask == null || _loopTask.IsCompleted) _loopTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public Task RequestNowAsync(string reason) => _request(reason, true);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                int delaySeconds;
                lock (_random) delaySeconds = _random.Next(110, 290);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                if (_enabled()) await _request("desktop at-rest timer", false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
