using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Audio;
using Wds.SixStars.Api.Voice;

namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// Reusable speech ingestion pipe for desktop and Unity.
    ///
    /// Any client converts microphone audio into PCM16 chunks and calls PushPcm16Async/PushAudioAsync.
    /// The intended live flow is:
    /// 1. Listen to the raw stream for any sound above VAD threshold.
    /// 2. After roughly 100 ms of sound, treat it as a candidate utterance.
    /// 3. In the default current mode, do not require an audio wake model. Capture by amplitude VAD.
    /// 4. Keep collecting chunks until configured silence ends the utterance.
    /// 5. Send the full utterance to local Whisper.
    /// 6. Optional text wake filtering can happen after transcription in SpeechToStarCoordinator.
    /// 7. The older audio-hotword path remains available behind RequireHotwordWhenIdle, but desktop no longer starts OpenWakeWord.
    ///
    /// Important threading rule:
    /// The capture lock only copies/resets in-memory segment state. Hotword awaits, Whisper awaits,
    /// and transcript events run after the lock is released so live microphone ingestion is not blocked.
    /// </summary>
    public sealed class WhisperSpeechInputPipeline : IStarSpeechPlaybackObserver
    {
        private readonly IWhisperSpeechStreamer _whisperStreamer;
        private readonly IAudioHotwordDetector _audioHotwordDetector;
        private readonly ISpeechActivityDetector _speechActivityDetector;
        private readonly VoiceCaptureSettings _settings;
        private readonly WhisperTranscriptionOptions _transcriptionOptions;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly Queue<PcmAudioChunk> _preRollChunks = new Queue<PcmAudioChunk>();
        private readonly List<PcmAudioChunk> _candidateSpeechChunks = new List<PcmAudioChunk>();
        private readonly List<Task<HotwordDetectionResult>> _activeHotwordTasks = new List<Task<HotwordDetectionResult>>();

        private bool _isListening;
        private bool _isSpeechActive;
        private bool _isStarSpeaking;
        private double _preRollMs;
        private double _candidateSoundMs;
        private double _activeSilenceMs;
        private double _conversationIdleMs;
        private string _activeSegmentId = string.Empty;
        private string _activeSource = string.Empty;
        private DateTimeOffset _activeStartedAt;
        private PcmAudioFormat _activeFormat = PcmAudioFormat.Mono16Khz();
        private MemoryStream? _activeSegmentBuffer;
        private double _activePeakRms;
        private int _activeChunkCount;
        private int _activePreRollBytes;
        private SpeechPipelineStatus _status = SpeechPipelineStatus.Stopped;
        private SpeechListenMode _listenMode = SpeechListenMode.HotwordDetection;
        private DateTimeOffset _lastWakeDetectedAt = DateTimeOffset.MinValue;
        private double _nextHotwordProbeAtMs;
        private double _lastHotwordProbeAtMs;
        private int _hotwordProbeCount;

        public WhisperSpeechInputPipeline(
            IWhisperSpeechStreamer whisperStreamer,
            ISpeechActivityDetector speechActivityDetector,
            VoiceCaptureSettings settings,
            WhisperTranscriptionOptions? transcriptionOptions = null)
            : this(
                whisperStreamer,
                new MockAudioHotwordDetector { DetectPreferredStarWhenQueueEmpty = true },
                speechActivityDetector,
                settings,
                transcriptionOptions)
        {
        }

        public WhisperSpeechInputPipeline(
            IWhisperSpeechStreamer whisperStreamer,
            IAudioHotwordDetector audioHotwordDetector,
            ISpeechActivityDetector speechActivityDetector,
            VoiceCaptureSettings settings,
            WhisperTranscriptionOptions? transcriptionOptions = null)
        {
            _whisperStreamer = whisperStreamer;
            _audioHotwordDetector = audioHotwordDetector;
            _speechActivityDetector = speechActivityDetector;
            _settings = settings;
            _transcriptionOptions = transcriptionOptions ?? new WhisperTranscriptionOptions();
        }

        public event EventHandler<SpeechSegmentEventArgs>? SpeechSegmentStarted;
        public event EventHandler<SpeechSegmentEventArgs>? SpeechSegmentCompleted;
        public event EventHandler<SpeechSegmentIgnoredEventArgs>? SpeechSegmentIgnored;
        public event EventHandler<SpeechTranscriptEventArgs>? TranscriptReady;
        public event EventHandler<SpeechPipelineStatusChangedEventArgs>? StatusChanged;
        public event EventHandler<SpeechInputLevelEventArgs>? InputLevelChanged;

        public SpeechPipelineStatus Status
        {
            get { return _status; }
        }

        public SpeechListenMode ListenMode
        {
            get { return _listenMode; }
        }

        public WakeAliasConfig WakeAliases { get; set; } = new WakeAliasConfig();
        public string PreferredStarId { get; set; } = string.Empty;
        public string CurrentConversationStarId { get; private set; } = string.Empty;

        public Task StartListeningAsync(CancellationToken cancellationToken)
        {
            _isListening = true;
            _conversationIdleMs = 0;
            _listenMode = _settings.RequireHotwordWhenIdle ? SpeechListenMode.HotwordDetection : SpeechListenMode.Conversation;
            SetIdleStatus("Listening started.");
            return Task.CompletedTask;
        }

        public async Task StopListeningAsync(CancellationToken cancellationToken)
        {
            CompletedSpeechSegment? completed = null;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_isSpeechActive)
                {
                    completed = EndActiveSegmentLocked(cancellationToken);
                }

                ClearBuffersLocked(resetConversationIdle: true);
                _isListening = false;
                SetStatus(SpeechPipelineStatus.Stopped, "Stopped.");
            }
            finally
            {
                _gate.Release();
            }

            if (completed != null)
            {
                await ProcessCompletedSegmentAsync(completed, cancellationToken).ConfigureAwait(false);
            }
        }


        public async Task CancelListeningAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ClearBuffersLocked(resetConversationIdle: true);
                _isListening = false;
                CurrentConversationStarId = string.Empty;
                _listenMode = SpeechListenMode.HotwordDetection;
                SetStatus(SpeechPipelineStatus.Stopped, "Listening cancelled and buffered audio discarded.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public void EnterConversationMode(string starId)
        {
            CurrentConversationStarId = starId ?? string.Empty;
            _listenMode = SpeechListenMode.Conversation;
            _conversationIdleMs = 0;
            SetStatus(SpeechPipelineStatus.ConversationListening, "Conversation mode open for " + DisplayStar(CurrentConversationStarId) + ".");
        }

        public void ReturnToHotwordDetectionMode(string reason)
        {
            CurrentConversationStarId = string.Empty;
            _listenMode = SpeechListenMode.HotwordDetection;
            _conversationIdleMs = 0;
            SetStatus(SpeechPipelineStatus.HotwordListening, string.IsNullOrWhiteSpace(reason) ? "Returned to hotword detection mode." : reason);
        }

        public void NotifyStarSpeechStarted()
        {
            _isStarSpeaking = true;
            SetStatus(SpeechPipelineStatus.StarSpeaking, "Star voice playback started. Microphone input is muted if configured.");
        }

        public void NotifyStarSpeechCompleted()
        {
            _isStarSpeaking = false;
            _conversationIdleMs = 0;
            SetIdleStatus("Star voice playback completed.");
        }

        public Task PushPcm16Async(byte[] pcm16Buffer, int bytesRecorded, PcmAudioFormat format, string source, CancellationToken cancellationToken)
        {
            PcmAudioChunk chunk = new PcmAudioChunk
            {
                Buffer = pcm16Buffer,
                BytesRecorded = bytesRecorded,
                Format = format,
                CapturedAt = DateTimeOffset.UtcNow,
                Source = source
            };

            return PushAudioAsync(chunk, cancellationToken);
        }

        public async Task PushAudioAsync(PcmAudioChunk inputChunk, CancellationToken cancellationToken)
        {
            if (!_isListening || inputChunk.BytesRecorded <= 0)
            {
                return;
            }

            if (_settings.MuteInputWhileStarSpeaking && _isStarSpeaking)
            {
                return;
            }

            CompletedSpeechSegment? completed = null;
            PcmAudioChunk chunk = inputChunk.CloneRecordedBytes();
            SpeechActivityResult activity = _speechActivityDetector.Analyze(chunk, _settings);
            InputLevelChanged?.Invoke(this, new SpeechInputLevelEventArgs(
                activity.Rms,
                activity.Threshold,
                activity.IsSpeech,
                _isListening,
                _isSpeechActive,
                _isStarSpeaking,
                _listenMode,
                chunk.Source,
                chunk.DurationMs,
                chunk.CapturedAt));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_isListening)
                {
                    return;
                }

                if (_isSpeechActive)
                {
                    PushToActiveSegmentLocked(chunk, activity.Rms);

                    if (activity.IsSpeech)
                    {
                        _activeSilenceMs = 0;
                    }
                    else
                    {
                        _activeSilenceMs += chunk.DurationMs;
                    }

                    if (ShouldRequireHotwordForActiveSegment())
                    {
                        MaybeLaunchHotwordProbeLocked(false, cancellationToken);
                    }

                    if (_activeSilenceMs >= _settings.SilenceEndMs || GetActiveDurationMs() >= _settings.MaxSegmentMs || chunk.IsEndOfStream)
                    {
                        completed = EndActiveSegmentLocked(cancellationToken);
                    }
                }
                else if (activity.IsSpeech)
                {
                    _conversationIdleMs = 0;
                    _candidateSpeechChunks.Add(chunk);
                    _candidateSoundMs += chunk.DurationMs;

                    if (_candidateSoundMs >= _settings.MinSoundMs)
                    {
                        BeginActiveSegmentLocked(chunk.Format, chunk.Source, cancellationToken);
                    }
                }
                else
                {
                    TrackConversationIdle(chunk.DurationMs);
                    _candidateSpeechChunks.Clear();
                    _candidateSoundMs = 0;
                    AddPreRollChunk(chunk);
                }
            }
            finally
            {
                _gate.Release();
            }

            if (completed != null)
            {
                await ProcessCompletedSegmentAsync(completed, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            CompletedSpeechSegment? completed = null;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_isSpeechActive)
                {
                    completed = EndActiveSegmentLocked(cancellationToken);
                }
            }
            finally
            {
                _gate.Release();
            }

            if (completed != null)
            {
                await ProcessCompletedSegmentAsync(completed, cancellationToken).ConfigureAwait(false);
            }
        }

        private void BeginActiveSegmentLocked(PcmAudioFormat format, string source, CancellationToken cancellationToken)
        {
            _isSpeechActive = true;
            _activeSegmentId = Guid.NewGuid().ToString("N");
            _activeStartedAt = DateTimeOffset.UtcNow;
            _activeFormat = format;
            _activeSource = source;
            _activeSegmentBuffer = new MemoryStream();
            _activePeakRms = 0;
            _activeChunkCount = 0;
            _activePreRollBytes = 0;
            _activeSilenceMs = 0;
            _activeHotwordTasks.Clear();
            _nextHotwordProbeAtMs = Math.Max(1, _settings.MinSoundMs);
            _lastHotwordProbeAtMs = 0;
            _hotwordProbeCount = 0;

            SetStatus(
                ShouldRequireHotwordForActiveSegment() ? SpeechPipelineStatus.HotwordCandidate : SpeechPipelineStatus.SpeechActive,
                ShouldRequireHotwordForActiveSegment()
                    ? "Sound detected. Capturing utterance and probing hotword."
                    : "Voice detected by amplitude VAD. Recording until silence, then sending to Whisper.");

            foreach (PcmAudioChunk preRoll in _preRollChunks)
            {
                PushToActiveSegmentLocked(preRoll, 0);
                _activePreRollBytes += preRoll.BytesRecorded;
            }

            foreach (PcmAudioChunk candidate in _candidateSpeechChunks)
            {
                SpeechActivityResult activity = _speechActivityDetector.Analyze(candidate, _settings);
                PushToActiveSegmentLocked(candidate, activity.Rms);
            }

            _preRollChunks.Clear();
            _preRollMs = 0;
            _candidateSpeechChunks.Clear();
            _candidateSoundMs = 0;

            MaybeLaunchHotwordProbeLocked(false, cancellationToken);
            SpeechSegmentStarted?.Invoke(this, new SpeechSegmentEventArgs(CreateActiveSegmentSnapshot(source)));
        }

        private void PushToActiveSegmentLocked(PcmAudioChunk chunk, double rms)
        {
            if (!_isSpeechActive || _activeSegmentBuffer == null)
            {
                return;
            }

            _activeSegmentBuffer.Write(chunk.Buffer, 0, chunk.BytesRecorded);
            _activePeakRms = Math.Max(_activePeakRms, rms);
            _activeChunkCount++;
        }

        private CompletedSpeechSegment? EndActiveSegmentLocked(CancellationToken cancellationToken)
        {
            if (!_isSpeechActive)
            {
                return null;
            }

            if (ShouldRequireHotwordForActiveSegment())
            {
                MaybeLaunchHotwordProbeLocked(true, cancellationToken);
            }

            SpeechSegment segment = CreateActiveSegmentSnapshot(_activeSource);
            List<Task<HotwordDetectionResult>> hotwordTasks = new List<Task<HotwordDetectionResult>>(_activeHotwordTasks);
            bool requiresHotword = ShouldRequireHotwordForActiveSegment();
            string conversationStarIdAtCapture = CurrentConversationStarId;
            SpeechListenMode listenModeAtCapture = _listenMode;

            ResetActiveSegmentStateLocked();

            return new CompletedSpeechSegment(
                segment,
                hotwordTasks,
                requiresHotword,
                conversationStarIdAtCapture,
                listenModeAtCapture);
        }

        private async Task ProcessCompletedSegmentAsync(CompletedSpeechSegment completed, CancellationToken cancellationToken)
        {
            SpeechSegmentCompleted?.Invoke(this, new SpeechSegmentEventArgs(completed.Segment));

            HotwordDetectionResult? hotwordResult = null;
            if (completed.RequiresHotword)
            {
                SetStatus(SpeechPipelineStatus.HotwordCandidate, "Waiting for hotword probe result.");
                hotwordResult = await ResolveBestHotwordResultAsync(completed.HotwordTasks, cancellationToken).ConfigureAwait(false);

                if (!hotwordResult.Detected)
                {
                    SpeechSegmentIgnored?.Invoke(this, new SpeechSegmentIgnoredEventArgs(completed.Segment, "No configured Star hotword was detected, so the utterance was not sent to Whisper.", hotwordResult));
                    SetIdleStatus("Hotword not detected. Listening for the next utterance.");
                    return;
                }

                _lastWakeDetectedAt = DateTimeOffset.UtcNow;
                CurrentConversationStarId = string.IsNullOrWhiteSpace(hotwordResult.StarId) ? PreferredStarId : hotwordResult.StarId;
                if (_settings.EnterConversationModeOnHotword)
                {
                    _listenMode = SpeechListenMode.Conversation;
                    _conversationIdleMs = 0;
                }
            }

            SetStatus(SpeechPipelineStatus.Transcribing, "Sending captured utterance to Whisper.");
            SpeechTranscript transcript = await TranscribeSegmentAsync(completed, hotwordResult, cancellationToken).ConfigureAwait(false);
            TranscriptReady?.Invoke(this, new SpeechTranscriptEventArgs(transcript));
            SetIdleStatus("Whisper transcript completed.");
        }

        private async Task<HotwordDetectionResult> ResolveBestHotwordResultAsync(IReadOnlyList<Task<HotwordDetectionResult>> hotwordTasks, CancellationToken cancellationToken)
        {
            if (hotwordTasks.Count == 0)
            {
                return HotwordDetectionResult.None("no-hotword-task");
            }

            HotwordDetectionResult best = HotwordDetectionResult.None("audio-hotword-window");
            List<Exception> failures = new List<Exception>();

            foreach (Task<HotwordDetectionResult> hotwordTask in hotwordTasks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    HotwordDetectionResult result = await hotwordTask.ConfigureAwait(false);
                    if (result.Detected && (!best.Detected || result.Confidence > best.Confidence))
                    {
                        best = result;
                    }
                    else if (!best.Detected && result.Confidence > best.Confidence)
                    {
                        best = result;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            if (best.Detected || failures.Count == 0)
            {
                return best;
            }

            return new HotwordDetectionResult
            {
                Detected = false,
                Confidence = 0,
                Source = "audio-hotword-window-error",
                DebugText = "All hotword probe tasks failed. First error: " + failures[0].Message
            };
        }

        private async Task<SpeechTranscript> TranscribeSegmentAsync(CompletedSpeechSegment completed, HotwordDetectionResult? hotwordResult, CancellationToken cancellationToken)
        {
            SpeechSegment segment = completed.Segment;
            await _whisperStreamer.BeginSegmentAsync(segment.SegmentId, segment.Format, _transcriptionOptions, cancellationToken).ConfigureAwait(false);
            await _whisperStreamer.PushAudioAsync(segment.SegmentId, segment.Pcm16Audio, 0, segment.Pcm16Audio.Length, cancellationToken).ConfigureAwait(false);
            SpeechTranscript transcript = await _whisperStreamer.EndSegmentAsync(segment.SegmentId, cancellationToken).ConfigureAwait(false);

            if (transcript.StartedAt == default(DateTimeOffset))
            {
                transcript.StartedAt = segment.StartedAt;
            }

            if (transcript.EndedAt == default(DateTimeOffset))
            {
                transcript.EndedAt = segment.EndedAt;
            }

            transcript.SegmentId = string.IsNullOrWhiteSpace(transcript.SegmentId) ? segment.SegmentId : transcript.SegmentId;
            transcript.AudioDurationMs = segment.DurationMs;
            transcript.WakeWordDetected = hotwordResult != null && hotwordResult.Detected;
            transcript.SuggestedStarId = transcript.WakeWordDetected
                ? (hotwordResult == null ? string.Empty : hotwordResult.StarId)
                : completed.ConversationStarIdAtCapture;
            transcript.HotwordConfidence = hotwordResult == null ? 0 : hotwordResult.Confidence;
            transcript.ListenMode = completed.ListenModeAtCapture.ToString();
            return transcript;
        }

        private void MaybeLaunchHotwordProbeLocked(bool force, CancellationToken cancellationToken)
        {
            if (!ShouldRequireHotwordForActiveSegment() || _activeSegmentBuffer == null)
            {
                return;
            }

            double durationMs = GetActiveSpeechAudioDurationMs();
            double maxSearchMs = _settings.HotwordMaxSearchMs <= 0
                ? _settings.HotwordProbeMs
                : _settings.HotwordMaxSearchMs;
            double probeEveryMs = Math.Max(1, _settings.HotwordProbeEveryMs);

            while (_nextHotwordProbeAtMs <= maxSearchMs && durationMs >= _nextHotwordProbeAtMs)
            {
                LaunchHotwordProbeLocked(_nextHotwordProbeAtMs, false, cancellationToken);
                _nextHotwordProbeAtMs += probeEveryMs;
            }

            if (force && durationMs > 0)
            {
                double finalProbeAtMs = Math.Min(durationMs, Math.Max(1, maxSearchMs));
                if (_activeHotwordTasks.Count == 0 || Math.Abs(finalProbeAtMs - _lastHotwordProbeAtMs) >= 50)
                {
                    LaunchHotwordProbeLocked(finalProbeAtMs, true, cancellationToken);
                }
            }
        }

        private void LaunchHotwordProbeLocked(double windowEndMs, bool isFinalProbe, CancellationToken cancellationToken)
        {
            SpeechSegment probe = CreateHotwordProbeSegment(windowEndMs);
            _hotwordProbeCount++;
            _lastHotwordProbeAtMs = windowEndMs;
            _activeHotwordTasks.Add(_audioHotwordDetector.DetectAsync(new AudioHotwordDetectionRequest
            {
                ProbeSegment = probe,
                WakeAliases = WakeAliases,
                PreferredStarId = PreferredStarId,
                ProbeIndex = _hotwordProbeCount,
                ProbeWindowEndMs = windowEndMs,
                IsFinalProbe = isFinalProbe
            }, cancellationToken));
        }

        private SpeechSegment CreateHotwordProbeSegment(double windowEndMs)
        {
            byte[] activeAudio = _activeSegmentBuffer == null ? new byte[0] : _activeSegmentBuffer.ToArray();
            int speechStart = Math.Max(0, Math.Min(_activePreRollBytes, activeAudio.Length));
            int speechAvailableBytes = Math.Max(0, activeAudio.Length - speechStart);
            int bytesPerSecond = Math.Max(1, _activeFormat.BytesPerSecond);
            int windowEndBytes = Math.Min(speechAvailableBytes, Math.Max(0, (int)(bytesPerSecond * windowEndMs / 1000.0)));
            int windowBytes = _settings.HotwordProbeMs <= 0
                ? windowEndBytes
                : Math.Min(windowEndBytes, Math.Max(0, bytesPerSecond * _settings.HotwordProbeMs / 1000));
            int start = speechStart + Math.Max(0, windowEndBytes - windowBytes);
            int count = Math.Max(0, Math.Min(windowBytes, activeAudio.Length - start));
            byte[] probeBytes = new byte[count];
            if (count > 0)
            {
                Array.Copy(activeAudio, start, probeBytes, 0, count);
            }

            double probeStartMs = count == 0 ? 0 : Math.Max(0, windowEndMs - ((double)count / bytesPerSecond * 1000.0));

            return new SpeechSegment
            {
                SegmentId = _activeSegmentId + "-hotword-" + (_hotwordProbeCount + 1).ToString("000"),
                Pcm16Audio = probeBytes,
                Format = _activeFormat,
                StartedAt = _activeStartedAt.AddMilliseconds(probeStartMs),
                EndedAt = _activeStartedAt.AddMilliseconds(windowEndMs),
                PeakRms = _activePeakRms,
                AudioChunkCount = _activeChunkCount,
                Source = _activeSource
            };
        }

        private bool ShouldRequireHotwordForActiveSegment()
        {
            if (!_settings.RequireHotwordWhenIdle)
            {
                return false;
            }

            if (_listenMode == SpeechListenMode.Conversation)
            {
                return false;
            }

            if (_settings.WakeCooldownMs > 0 && _lastWakeDetectedAt != DateTimeOffset.MinValue)
            {
                TimeSpan sinceWake = DateTimeOffset.UtcNow - _lastWakeDetectedAt;
                if (sinceWake.TotalMilliseconds < _settings.WakeCooldownMs)
                {
                    return false;
                }
            }

            return true;
        }

        private SpeechSegment CreateActiveSegmentSnapshot(string source)
        {
            byte[] audio = _activeSegmentBuffer == null ? new byte[0] : _activeSegmentBuffer.ToArray();
            return new SpeechSegment
            {
                SegmentId = _activeSegmentId,
                Pcm16Audio = audio,
                Format = _activeFormat,
                StartedAt = _activeStartedAt,
                EndedAt = DateTimeOffset.UtcNow,
                PeakRms = _activePeakRms,
                AudioChunkCount = _activeChunkCount,
                Source = source
            };
        }

        private void AddPreRollChunk(PcmAudioChunk chunk)
        {
            _preRollChunks.Enqueue(chunk);
            _preRollMs += chunk.DurationMs;

            while (_preRollMs > _settings.PreRollMs && _preRollChunks.Count > 0)
            {
                PcmAudioChunk removed = _preRollChunks.Dequeue();
                _preRollMs -= removed.DurationMs;
            }
        }

        private void TrackConversationIdle(double chunkDurationMs)
        {
            if (_listenMode != SpeechListenMode.Conversation || _settings.ConversationIdleMs <= 0)
            {
                return;
            }

            _conversationIdleMs += chunkDurationMs;
            if (_conversationIdleMs >= _settings.ConversationIdleMs)
            {
                ReturnToHotwordDetectionMode("Conversation idled out. Returning to hotword detection mode.");
            }
        }

        private double GetActiveDurationMs()
        {
            if (_activeSegmentBuffer == null || _activeFormat.BytesPerSecond <= 0)
            {
                return 0;
            }

            return (double)_activeSegmentBuffer.Length / _activeFormat.BytesPerSecond * 1000.0;
        }

        private double GetActiveSpeechAudioDurationMs()
        {
            if (_activeSegmentBuffer == null || _activeFormat.BytesPerSecond <= 0)
            {
                return 0;
            }

            int speechBytes = Math.Max(0, (int)_activeSegmentBuffer.Length - _activePreRollBytes);
            return (double)speechBytes / _activeFormat.BytesPerSecond * 1000.0;
        }

        private void ClearBuffersLocked(bool resetConversationIdle)
        {
            _preRollChunks.Clear();
            _candidateSpeechChunks.Clear();
            _preRollMs = 0;
            _candidateSoundMs = 0;
            _activeSilenceMs = 0;
            if (resetConversationIdle)
            {
                _conversationIdleMs = 0;
            }
            ResetActiveSegmentStateLocked();
        }

        private void ResetActiveSegmentStateLocked()
        {
            _activeSegmentBuffer?.Dispose();
            _activeSegmentBuffer = null;
            _activeSegmentId = string.Empty;
            _activeSource = string.Empty;
            _isSpeechActive = false;
            _activeSilenceMs = 0;
            _activePeakRms = 0;
            _activeChunkCount = 0;
            _activePreRollBytes = 0;
            _activeHotwordTasks.Clear();
            _nextHotwordProbeAtMs = Math.Max(1, _settings.MinSoundMs);
            _lastHotwordProbeAtMs = 0;
            _hotwordProbeCount = 0;
        }

        private void SetIdleStatus(string message)
        {
            if (!_isListening)
            {
                SetStatus(SpeechPipelineStatus.Stopped, message);
                return;
            }

            if (_listenMode == SpeechListenMode.Conversation)
            {
                SetStatus(SpeechPipelineStatus.ConversationListening, message + " Listening for follow-up speech" + (string.IsNullOrWhiteSpace(CurrentConversationStarId) ? "." : " to " + CurrentConversationStarId + "."));
            }
            else
            {
                SetStatus(SpeechPipelineStatus.HotwordListening, message + " Listening for voice activity.");
            }
        }

        private static string DisplayStar(string starId)
        {
            return string.IsNullOrWhiteSpace(starId) ? "selected Star" : starId;
        }

        private void SetStatus(SpeechPipelineStatus status, string message)
        {
            _status = status;
            StatusChanged?.Invoke(this, new SpeechPipelineStatusChangedEventArgs(status, message));
        }

        private sealed class CompletedSpeechSegment
        {
            public CompletedSpeechSegment(
                SpeechSegment segment,
                IReadOnlyList<Task<HotwordDetectionResult>> hotwordTasks,
                bool requiresHotword,
                string conversationStarIdAtCapture,
                SpeechListenMode listenModeAtCapture)
            {
                Segment = segment;
                HotwordTasks = hotwordTasks;
                RequiresHotword = requiresHotword;
                ConversationStarIdAtCapture = conversationStarIdAtCapture;
                ListenModeAtCapture = listenModeAtCapture;
            }

            public SpeechSegment Segment { get; }
            public IReadOnlyList<Task<HotwordDetectionResult>> HotwordTasks { get; }
            public bool RequiresHotword { get; }
            public string ConversationStarIdAtCapture { get; }
            public SpeechListenMode ListenModeAtCapture { get; }
        }
    }
}
