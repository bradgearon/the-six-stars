using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Screen
{
    /// <summary>
    /// Periodic screen-awareness loop. It captures a screen image, asks a summarizer for a safe compact summary,
    /// and stores the latest summaries in the same context store SixStarsRuntime reads before Star requests.
    /// </summary>
    public sealed class ScreenAwarenessLoop : IDisposable
    {
        private readonly string _sessionId;
        private readonly IScreenContextStore _screenContextStore;
        private readonly IScreenImageCaptureService _captureService;
        private readonly IScreenSummarizer _summarizer;
        private readonly ScreenAwarenessOptions _options;
        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private int _consecutiveFailures;

        public ScreenAwarenessLoop(
            string sessionId,
            IScreenContextStore screenContextStore,
            IScreenImageCaptureService captureService,
            IScreenSummarizer summarizer,
            ScreenAwarenessOptions? options = null)
        {
            _sessionId = string.IsNullOrWhiteSpace(sessionId) ? "local-dev" : sessionId;
            _screenContextStore = screenContextStore;
            _captureService = captureService;
            _summarizer = summarizer;
            _options = options ?? new ScreenAwarenessOptions();
        }

        public bool IsRunning => _loopTask != null && !_loopTask.IsCompleted;
        public event EventHandler<ScreenCaptureSummary>? CaptureStored;
        public event EventHandler<string>? StatusChanged;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (IsRunning)
            {
                StatusChanged?.Invoke(this, "Screen awareness already running.");
                return Task.CompletedTask;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
            StatusChanged?.Invoke(this, "Screen awareness started.");
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_cts == null)
            {
                StatusChanged?.Invoke(this, "Screen awareness is not running.");
                return;
            }

            _cts.Cancel();
            try
            {
                if (_loopTask != null)
                {
                    await _loopTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                _loopTask = null;
                StatusChanged?.Invoke(this, "Screen awareness stopped.");
            }
        }

        public async Task<ScreenCaptureSummary> CaptureOnceAsync(CancellationToken cancellationToken)
        {
            ScreenImageCapture image = await _captureService.CaptureAsync(cancellationToken).ConfigureAwait(false);
            ScreenSummarizationRequest request = new ScreenSummarizationRequest
            {
                SessionId = _sessionId,
                Capture = image,
                IncludeBase64InSummary = _options.StoreRawImageBase64
            };
            request.BlockedWindowTitleKeywords.AddRange(_options.BlockedWindowTitleKeywords);
            request.BlockedProcessNames.AddRange(_options.BlockedProcessNames);

            ScreenCaptureSummary summary;
            if (request.IsBlockedByPolicy())
            {
                summary = new ScreenCaptureSummary
                {
                    CapturedAt = image.CapturedAt,
                    ActiveApp = string.IsNullOrWhiteSpace(image.ActiveApp) ? image.ActiveProcessName : image.ActiveApp,
                    ActivityType = "blocked",
                    LikelyActivity = "The active screen was blocked by privacy rules.",
                    CompanionSafeSummary = "The user is looking at a private or sensitive window. Do not comment on the screen contents.",
                    Sensitivity = "highly_sensitive",
                    ImageReference = image.ImageReference,
                    Confidence = 1.0
                };
            }
            else
            {
                summary = await _summarizer.SummarizeAsync(request, cancellationToken).ConfigureAwait(false);
                summary.CapturedAt = image.CapturedAt;
                summary.ImageReference = string.IsNullOrWhiteSpace(summary.ImageReference) ? image.ImageReference : summary.ImageReference;
                if (_options.StoreRawImageBase64 && string.IsNullOrWhiteSpace(summary.ImageBase64Jpeg))
                {
                    summary.ImageBase64Jpeg = Convert.ToBase64String(image.ImageBytes ?? Array.Empty<byte>());
                }
            }

            await _screenContextStore.AddAsync(_sessionId, summary, cancellationToken).ConfigureAwait(false);
            CaptureStored?.Invoke(this, summary);
            _consecutiveFailures = 0;
            StatusChanged?.Invoke(this, "Screen captured: " + summary.ActivityType + " / " + summary.CompanionSafeSummary);
            return summary;
        }

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            if (_options.CaptureImmediatelyOnStart)
            {
                await TryCaptureOnceAsync(cancellationToken).ConfigureAwait(false);
            }

            TimeSpan interval = _options.CaptureInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : _options.CaptureInterval;
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                await TryCaptureOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task TryCaptureOnceAsync(CancellationToken cancellationToken)
        {
            try
            {
                await CaptureOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                StatusChanged?.Invoke(this, "Screen awareness error: " + ex.Message);
                if (_options.MaxConsecutiveFailures > 0 && _consecutiveFailures >= _options.MaxConsecutiveFailures)
                {
                    StatusChanged?.Invoke(this, "Screen awareness hit failure limit and will stop.");
                    _cts?.Cancel();
                }
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
