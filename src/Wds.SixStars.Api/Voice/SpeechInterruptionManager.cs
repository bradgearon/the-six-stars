using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// Options for controlling speech interruption behavior
    /// </summary>
    public sealed class SpeechInterruptionOptions
    {
        /// <summary>
        /// Minimum duration (ms) user must speak before interrupting Star
        /// </summary>
        public int MinInterruptionDurationMs { get; set; } = 300;

        /// <summary>
        /// Time to wait (ms) after user speech starts before canceling Star's speech
        /// </summary>
        public int InterruptionDelayMs { get; set; } = 200;

        /// <summary>
        /// Enable speech interruption feature
        /// </summary>
        public bool EnableInterruption { get; set; } = true;

        /// <summary>
        /// Cancel TTS generation if user interrupts while it's being generated
        /// </summary>
        public bool CancelGenerationOnInterruption { get; set; } = true;

        /// <summary>
        /// Cancel audio playback if user interrupts while it's playing
        /// </summary>
        public bool CancelPlaybackOnInterruption { get; set; } = true;
    }

    /// <summary>
    /// Manages speech interruption detection and handling
    /// </summary>
    public sealed class SpeechInterruptionManager : IDisposable
    {
        private readonly SpeechInterruptionOptions _options;
        private CancellationTokenSource? _currentSpeechCts;
        private CancellationTokenSource? _interruptionTimerCts;
        private bool _isStarSpeaking;
        private bool _isGeneratingSpeech;
        private DateTimeOffset _userSpeechStartTime;

        public SpeechInterruptionManager(SpeechInterruptionOptions? options = null)
        {
            _options = options ?? new SpeechInterruptionOptions();
        }

        public event EventHandler<SpeechInterruptedEventArgs>? SpeechInterrupted;

        /// <summary>
        /// Indicates if Star is currently speaking or generating speech
        /// </summary>
        public bool IsStarSpeaking => _isStarSpeaking || _isGeneratingSpeech;

        /// <summary>
        /// Start tracking Star speech generation
        /// </summary>
        public CancellationToken StartSpeechGeneration()
        {
            if (!_options.EnableInterruption)
            {
                return CancellationToken.None;
            }

            _currentSpeechCts?.Cancel();
            _currentSpeechCts?.Dispose();
            _currentSpeechCts = new CancellationTokenSource();
            _isGeneratingSpeech = true;

            System.Diagnostics.Debug.WriteLine("[SpeechInterruption] Started speech generation (interruptible)");
            return _currentSpeechCts.Token;
        }

        /// <summary>
        /// Notify that speech generation completed
        /// </summary>
        public void SpeechGenerationCompleted()
        {
            _isGeneratingSpeech = false;
            System.Diagnostics.Debug.WriteLine("[SpeechInterruption] Speech generation completed");
        }

        /// <summary>
        /// Start tracking Star speech playback
        /// </summary>
        public CancellationToken StartSpeechPlayback()
        {
            if (!_options.EnableInterruption)
            {
                return CancellationToken.None;
            }

            // Reuse or create new CTS for playback
            if (_currentSpeechCts == null || _currentSpeechCts.IsCancellationRequested)
            {
                _currentSpeechCts = new CancellationTokenSource();
            }

            _isStarSpeaking = true;
            _isGeneratingSpeech = false;

            System.Diagnostics.Debug.WriteLine("[SpeechInterruption] Started speech playback (interruptible)");
            return _currentSpeechCts.Token;
        }

        /// <summary>
        /// Notify that speech playback completed
        /// </summary>
        public void SpeechPlaybackCompleted()
        {
            _isStarSpeaking = false;
            System.Diagnostics.Debug.WriteLine("[SpeechInterruption] Speech playback completed");
        }

        /// <summary>
        /// User started speaking - start interruption timer
        /// </summary>
        public void OnUserSpeechStarted()
        {
            if (!_options.EnableInterruption || !IsStarSpeaking)
            {
                return;
            }

            bool canInterruptGeneration = _isGeneratingSpeech && _options.CancelGenerationOnInterruption;
            bool canInterruptPlayback = _isStarSpeaking && _options.CancelPlaybackOnInterruption;
            if (!canInterruptGeneration && !canInterruptPlayback)
            {
                System.Diagnostics.Debug.WriteLine("[SpeechInterruption] User speech ignored for interruption because current Star speech phase is not interruptible.");
                return;
            }

            _userSpeechStartTime = DateTimeOffset.Now;

            // Start interruption timer
            _interruptionTimerCts?.Cancel();
            _interruptionTimerCts?.Dispose();
            _interruptionTimerCts = new CancellationTokenSource();

            _ = StartInterruptionTimerAsync(_interruptionTimerCts.Token);

            System.Diagnostics.Debug.WriteLine($"[SpeechInterruption] User started speaking (timer: {_options.InterruptionDelayMs}ms)");
        }

        /// <summary>
        /// User stopped speaking - cancel interruption timer
        /// </summary>
        public void OnUserSpeechStopped()
        {
            _interruptionTimerCts?.Cancel();
            System.Diagnostics.Debug.WriteLine("[SpeechInterruption] User stopped speaking (interruption cancelled)");
        }

        /// <summary>
        /// Force cancel current Star speech
        /// </summary>
        public void CancelCurrentSpeech(string reason = "User interrupted")
        {
            if (_currentSpeechCts != null && !_currentSpeechCts.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine($"[SpeechInterruption] \u26a0 Canceling Star speech: {reason}");
                bool wasGenerating = _isGeneratingSpeech;
                bool wasPlaying = _isStarSpeaking;

                _currentSpeechCts.Cancel();
                _isStarSpeaking = false;
                _isGeneratingSpeech = false;

                SpeechInterrupted?.Invoke(this, new SpeechInterruptedEventArgs
                {
                    Reason = reason,
                    InterruptedDuringGeneration = wasGenerating,
                    InterruptedDuringPlayback = wasPlaying,
                    UserSpeechDuration = DateTimeOffset.Now - _userSpeechStartTime
                });
            }
        }

        private async Task StartInterruptionTimerAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Wait for interruption delay
                await Task.Delay(_options.InterruptionDelayMs, cancellationToken).ConfigureAwait(false);

                // Check if user is still speaking
                TimeSpan speakingDuration = DateTimeOffset.Now - _userSpeechStartTime;
                if (speakingDuration.TotalMilliseconds >= _options.MinInterruptionDurationMs)
                {
                    System.Diagnostics.Debug.WriteLine($"[SpeechInterruption] \u2713 User spoke for {speakingDuration.TotalMilliseconds:0}ms - interrupting Star!");
                    CancelCurrentSpeech($"User spoke for {speakingDuration.TotalMilliseconds:0}ms");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[SpeechInterruption] User speech too short ({speakingDuration.TotalMilliseconds:0}ms < {_options.MinInterruptionDurationMs}ms)");
                }
            }
            catch (TaskCanceledException)
            {
                // User stopped speaking before timer completed - this is expected
            }
        }

        public void Dispose()
        {
            _currentSpeechCts?.Cancel();
            _currentSpeechCts?.Dispose();
            _interruptionTimerCts?.Cancel();
            _interruptionTimerCts?.Dispose();
        }
    }

    public sealed class SpeechInterruptedEventArgs : EventArgs
    {
        public string Reason { get; set; } = string.Empty;
        public bool InterruptedDuringGeneration { get; set; }
        public bool InterruptedDuringPlayback { get; set; }
        public TimeSpan UserSpeechDuration { get; set; }
    }
}
