using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// Lightweight live speech queue for delimiter-streamed foreground replies.
    /// Text chunks may arrive while the LLM is still generating. TTS generation is serialized on
    /// the local qwentts.cpp HTTP service, while audio consumption/playback runs independently, so the
    /// next text chunk can be synthesized while the previous WAV is still playing.
    /// </summary>
    public sealed class LiveStarVoiceResponsePipeline : IDisposable
    {
        private readonly IStreamingVoiceGenerator _generator;
        private readonly IWavePlaybackEndpoint _playbackEndpoint;
        private readonly IStarSpeechPlaybackObserver? _playbackObserver;
        private readonly SpeechInterruptionManager? _interruptionManager;
        private readonly object _textGate = new object();
        private readonly object _audioGate = new object();
        private readonly Queue<List<string>> _textQueue = new Queue<List<string>>();
        private readonly Queue<LiveVoiceChunkReadyEventArgs> _audioQueue = new Queue<LiveVoiceChunkReadyEventArgs>();
        private readonly SemaphoreSlim _textSignal = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _audioSignal = new SemaphoreSlim(0);

        private CancellationTokenSource? _runCts;
        private Task? _generationTask;
        private Task? _consumerTask;
        private TaskCompletionSource<GenerationCompleteEventArgs>? _currentGenerationCompletion;
        private StarInteractionResponse? _voiceTemplate;
        private string _sessionId = string.Empty;
        private volatile bool _inputComplete;
        private volatile bool _generationDone;
        private volatile GenerationErrorEventArgs? _generationError;
        private int _audioSequence;
        private int _textChunksEnqueued;
        private bool _started;
        private IDisposable? _outputKeepAlive;

        public LiveStarVoiceResponsePipeline(
            IStreamingVoiceGenerator generator,
            IWavePlaybackEndpoint playbackEndpoint,
            IStarSpeechPlaybackObserver? playbackObserver = null,
            SpeechInterruptionManager? interruptionManager = null)
        {
            _generator = generator ?? throw new ArgumentNullException(nameof(generator));
            _playbackEndpoint = playbackEndpoint ?? throw new ArgumentNullException(nameof(playbackEndpoint));
            _playbackObserver = playbackObserver;
            _interruptionManager = interruptionManager;

            _generator.AudioChunkReady += OnAudioChunkReady;
            _generator.GenerationComplete += OnGenerationComplete;
            _generator.GenerationError += OnGenerationError;
        }

        public event EventHandler<LiveVoiceChunkReadyEventArgs>? ChunkReadyForConsumer;
        public event EventHandler<GenerationErrorEventArgs>? GenerationError;

        /// <summary>Desktop=true; Android/mobile bridge=false so the host can forward WAV bytes itself.</summary>
        public bool AutoPlayChunks { get; set; } = true;

        public bool IsStarted => _started;
        public int TextChunksEnqueued => Volatile.Read(ref _textChunksEnqueued);
        public int AudioChunksProduced => Volatile.Read(ref _audioSequence);

        public void Start(string sessionId, StarInteractionResponse voiceTemplate, CancellationToken cancellationToken)
        {
            if (_started) throw new InvalidOperationException("Live speech pipeline is already started.");
            if (voiceTemplate == null) throw new ArgumentNullException(nameof(voiceTemplate));

            _sessionId = sessionId ?? string.Empty;
            _voiceTemplate = voiceTemplate;
            _inputComplete = false;
            _generationDone = false;
            _generationError = null;
            _audioSequence = 0;
            _textChunksEnqueued = 0;
            _started = true;

            lock (_textGate) _textQueue.Clear();
            lock (_audioGate) _audioQueue.Clear();

            _runCts?.Dispose();
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken interruptionToken = _interruptionManager?.StartSpeechGeneration() ?? CancellationToken.None;
            if (interruptionToken != CancellationToken.None)
            {
                CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_runCts.Token, interruptionToken);
                _runCts.Dispose();
                _runCts = linked;
            }

            _playbackObserver?.NotifyStarSpeechStarted();
            if (AutoPlayChunks && _playbackEndpoint is IAudioOutputKeepAliveEndpoint keepAliveEndpoint)
            {
                _outputKeepAlive = keepAliveEndpoint.StartOutputKeepAlive("live LLM speech streaming", _runCts.Token);
            }

            _generationTask = Task.Run(() => GenerationLoopAsync(_runCts.Token), _runCts.Token);
            _consumerTask = Task.Run(() => ConsumerLoopAsync(_runCts.Token), _runCts.Token);
            Debug.WriteLine("[LiveStarVoiceResponsePipeline] Started live LLM -> TTS queue for " + voiceTemplate.StarId + ".");
        }

        /// <summary>
        /// Non-blocking producer hook intended for StarInteractionRequest.SpokenChunkReady.
        /// </summary>
        public void EnqueueTextChunk(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            EnqueueTextChunks(new[] { text });
        }

        /// <summary>
        /// Queues several already-final LLM speech chunks as one Qwen request. The Qwen server still
        /// returns one WAV per text part, in order, but batching avoids opening a brand-new synthesis
        /// request for every sentence after the foreground text has already reached the phone.
        /// </summary>
        public void EnqueueTextChunks(IEnumerable<string>? texts)
        {
            if (!_started || _inputComplete || _runCts?.IsCancellationRequested == true || texts == null) return;
            List<string> parts = texts
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();
            if (parts.Count == 0) return;

            lock (_textGate)
            {
                if (_inputComplete) return;
                _textQueue.Enqueue(parts);
                Interlocked.Add(ref _textChunksEnqueued, parts.Count);
            }
            _textSignal.Release();
            Debug.WriteLine("[LiveStarVoiceResponsePipeline] Queued " + parts.Count + " streamed text part(s) as one Qwen batch: " + Preview(parts[0]));
        }

        /// <summary>
        /// Marks the LLM stream complete and waits until every queued TTS request and every queued
        /// audio chunk has been generated/consumed. Already-playing audio is allowed to finish.
        /// </summary>
        public async Task CompleteInputAsync()
        {
            if (!_started) return;
            _inputComplete = true;
            _textSignal.Release();

            Exception? failure = null;
            try
            {
                if (_generationTask != null) await _generationTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                _generationDone = true;
                _audioSignal.Release();
            }

            if (failure == null && _consumerTask != null)
            {
                try
                {
                    await _consumerTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            if (failure != null)
            {
                try { _runCts?.Cancel(); } catch { }
                _textSignal.Release();
                _audioSignal.Release();
            }

            FinishRun();

            if (_generationError != null)
            {
                throw new InvalidOperationException("Live streaming TTS generation failed: " + _generationError.ErrorMessage, _generationError.Exception);
            }
            if (failure != null) throw failure;
        }

        public void CancelCurrentSpeech()
        {
            Debug.WriteLine("[LiveStarVoiceResponsePipeline] Cancelling live speech queue.");
            try { _runCts?.Cancel(); } catch { }
            try { _generator.CancelGeneration(); } catch { }
            _textSignal.Release();
            _audioSignal.Release();
        }

        private async Task GenerationLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Health-check the local qwentts.cpp HTTP service while the LLM is still working toward its first §.
                // This hides service connection/health-check latency without running any
                // TTS generation until an immutable text chunk actually arrives.
                if (!_generator.IsConnected)
                {
                    await _generator.ConnectAsync(cancellationToken).ConfigureAwait(false);
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    List<string>? textParts = null;
                    lock (_textGate)
                    {
                        if (_textQueue.Count > 0) textParts = _textQueue.Dequeue();
                    }

                    if (textParts == null)
                    {
                        if (_inputComplete) break;
                        await _textSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    StarInteractionResponse template = _voiceTemplate ?? throw new InvalidOperationException("Live voice template is missing.");
                    await GenerateTextPartsWithTailRetryAsync(template, textParts, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            finally
            {
                _generationDone = true;
                _audioSignal.Release();
                _interruptionManager?.SpeechGenerationCompleted();
                Debug.WriteLine("[LiveStarVoiceResponsePipeline] Text/TTS generation queue drained.");
            }
        }


        private async Task GenerateTextPartsWithTailRetryAsync(
            StarInteractionResponse template,
            List<string> requestedParts,
            CancellationToken cancellationToken)
        {
            List<string> pending = requestedParts;
            bool retriedMissingTail = false;

            while (pending.Count > 0)
            {
                int audioBefore = Volatile.Read(ref _audioSequence);
                TaskCompletionSource<GenerationCompleteEventArgs> completion =
                    new TaskCompletionSource<GenerationCompleteEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                _currentGenerationCompletion = completion;

                VoiceSynthesisRequest request = new VoiceSynthesisRequest
                {
                    SessionId = _sessionId,
                    StarId = template.StarId,
                    Text = string.Join(" ", pending),
                    TextParts = new List<string>(pending),
                    SourceResponse = template,
                    VoiceSettings = CreateSettingsFromResponse(template)
                };

                Debug.WriteLine("[LiveStarVoiceResponsePipeline] Starting Qwen TTS batch with " + pending.Count + " part(s): " + Preview(pending[0]));
                await _generator.GenerateStreamingAsync(request, cancellationToken).ConfigureAwait(false);
                GenerationCompleteEventArgs completed = await completion.Task.ConfigureAwait(false);
                _currentGenerationCompletion = null;

                if (completed.WasCancelled)
                {
                    throw new OperationCanceledException("Qwen reported that the live speech batch was cancelled.", cancellationToken);
                }

                int emitted = Math.Max(0, Volatile.Read(ref _audioSequence) - audioBefore);
                if (emitted >= pending.Count) return;

                // A WAV response normally emits one audio chunk per text part. PCM streaming may emit
                // several short WAV windows for one text part, so emitted can be greater than pending.
                // A successful PCM request throws if a text part returns no audio, preserving the same
                // missing-tail safety without delaying first playback for a whole sentence.
                if (retriedMissingTail)
                {
                    throw new InvalidOperationException(
                        "Qwen completed live speech with " + emitted + " WAV(s) for " + pending.Count + " remaining text part(s) after one missing-tail retry.");
                }

                int consumed = Math.Min(emitted, pending.Count);
                pending = pending.Skip(consumed).ToList();
                retriedMissingTail = true;
                Debug.WriteLine("[LiveStarVoiceResponsePipeline] Qwen omitted " + pending.Count + " trailing WAV part(s); retrying only the missing tail once.");
            }
        }

        private async Task ConsumerLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    LiveVoiceChunkReadyEventArgs? ready = null;
                    lock (_audioGate)
                    {
                        if (_audioQueue.Count > 0) ready = _audioQueue.Dequeue();
                    }

                    if (ready == null)
                    {
                        if (_generationDone)
                        {
                            lock (_audioGate)
                            {
                                if (_audioQueue.Count == 0) break;
                            }
                        }
                        await _audioSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // Surface ordered audio from a worker, never from the qwentts.cpp HTTP request callback.
                    ChunkReadyForConsumer?.Invoke(this, ready);

                    if (AutoPlayChunks)
                    {
                        CancellationToken playbackToken = cancellationToken;
                        CancellationTokenSource? playbackLinked = null;
                        CancellationToken interruptionToken = _interruptionManager?.StartSpeechPlayback() ?? CancellationToken.None;
                        if (interruptionToken != CancellationToken.None)
                        {
                            playbackLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interruptionToken);
                            playbackToken = playbackLinked.Token;
                        }

                        try
                        {
                            await _playbackEndpoint.PlayWavAsync(ready.Result, playbackToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            playbackLinked?.Dispose();
                            _interruptionManager?.SpeechPlaybackCompleted();
                        }
                    }
                }
            }
            finally
            {
                Debug.WriteLine("[LiveStarVoiceResponsePipeline] Audio consumer queue drained.");
            }
        }

        private void OnAudioChunkReady(object? sender, AudioChunkEventArgs e)
        {
            if (!_started || _runCts?.IsCancellationRequested == true) return;

            byte[] copy = CopyBytes(e.AudioData);
            VoiceSynthesisResult result = new VoiceSynthesisResult
            {
                StarId = e.StarId,
                VoiceId = e.VoiceId,
                WavAudio = copy,
                MimeType = e.MimeType,
                DurationMs = e.DurationMs,
                IsStreamingPcmWindow = e.IsStreamingPcmWindow,
                Instructions = e.Text,
                Source = "live-streaming-qwentts-http"
            };
            int sequence = Interlocked.Increment(ref _audioSequence) - 1;
            LiveVoiceChunkReadyEventArgs ready = new LiveVoiceChunkReadyEventArgs(sequence, e.Text, result);
            lock (_audioGate) _audioQueue.Enqueue(ready);
            _audioSignal.Release();
            Debug.WriteLine("[LiveStarVoiceResponsePipeline] Qwen audio ready for live sequence " + sequence + ": " + copy.Length + " bytes.");
        }

        private void OnGenerationComplete(object? sender, GenerationCompleteEventArgs e)
        {
            _currentGenerationCompletion?.TrySetResult(e);
        }

        private void OnGenerationError(object? sender, GenerationErrorEventArgs e)
        {
            _generationError = e;
            _currentGenerationCompletion?.TrySetException(new InvalidOperationException(e.ErrorMessage, e.Exception));
            GenerationError?.Invoke(this, e);
        }

        private void FinishRun()
        {
            if (!_started) return;
            _started = false;
            _outputKeepAlive?.Dispose();
            _outputKeepAlive = null;
            _interruptionManager?.SpeechPlaybackCompleted();
            _playbackObserver?.NotifyStarSpeechCompleted();
            _currentGenerationCompletion = null;
        }

        private static StarVoiceSettings CreateSettingsFromResponse(StarInteractionResponse response)
        {
            string strongestEmotion = response.EmotionScores
                .OrderByDescending(x => x.Value.Score * x.Value.Confidence)
                .Select(x => x.Key)
                .FirstOrDefault() ?? "neutral";
            double intensity = response.EmotionScores.Count == 0
                ? 0.5
                : Math.Min(1.0, Math.Max(0.0, response.EmotionScores.Max(x => x.Value.Score) / 100.0));

            VoiceDirective directive = response.VoiceDirective ?? VoiceDirective.Empty();
            string primaryEmotion = string.IsNullOrWhiteSpace(directive.PrimaryEmotion) ? strongestEmotion : directive.PrimaryEmotion;
            double directiveIntensity = directive.EmotionIntensity <= 0 ? intensity : Math.Min(1.0, Math.Max(0.0, directive.EmotionIntensity));

            StarVoiceSettings settings = new StarVoiceSettings
            {
                StarId = response.StarId,
                VoiceId = string.IsNullOrWhiteSpace(directive.VoiceId) ? response.StarId : directive.VoiceId,
                Style = primaryEmotion,
                PrimaryEmotion = primaryEmotion,
                EmotionIntensity = directiveIntensity,
                VoiceDesignPrompt = directive.VoiceDesignPrompt ?? string.Empty,
                VoiceCloneAudioFile = directive.VoiceCloneAudioFile ?? string.Empty,
                VoiceCloneRefText = directive.VoiceCloneRefText ?? string.Empty,
                XVectorOnly = directive.XVectorOnly,
                DeliveryInstructions = directive.DeliveryInstructions ?? string.Empty,
                Pace = string.IsNullOrWhiteSpace(directive.Pace) ? "normal" : directive.Pace,
                PitchDescription = string.IsNullOrWhiteSpace(directive.Pitch) ? "natural" : directive.Pitch,
                Volume = string.IsNullOrWhiteSpace(directive.Volume) ? "normal" : directive.Volume,
                Prosody = directive.Prosody ?? string.Empty,
                Language = VoiceLanguageNormalizer.Normalize(directive.Language, "English")
            };
            settings.AudioTags.AddRange(directive.AudioTags ?? Enumerable.Empty<string>());
            settings.Avoid.AddRange(directive.Avoid ?? Enumerable.Empty<string>());
            if (string.IsNullOrWhiteSpace(settings.DeliveryInstructions))
            {
                settings.DeliveryInstructions = "Speak in character with " + primaryEmotion + " feeling. Keep the delivery natural and emotionally grounded.";
            }
            return settings;
        }

        private static byte[] CopyBytes(byte[]? source)
        {
            if (source == null || source.Length == 0) return Array.Empty<byte>();
            byte[] copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }

        private static string Preview(string value)
        {
            string text = value ?? string.Empty;
            return text.Substring(0, Math.Min(80, text.Length));
        }

        public void Dispose()
        {
            CancelCurrentSpeech();
            try { _generationTask?.GetAwaiter().GetResult(); } catch { }
            try { _consumerTask?.GetAwaiter().GetResult(); } catch { }
            FinishRun();
            _generator.AudioChunkReady -= OnAudioChunkReady;
            _generator.GenerationComplete -= OnGenerationComplete;
            _generator.GenerationError -= OnGenerationError;
            _generator.Dispose();
            _runCts?.Dispose();
            _textSignal.Dispose();
            _audioSignal.Dispose();
        }
    }

    public sealed class LiveVoiceChunkReadyEventArgs : EventArgs
    {
        public LiveVoiceChunkReadyEventArgs(int sequence, string text, VoiceSynthesisResult result)
        {
            Sequence = sequence;
            Text = text ?? string.Empty;
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        public int Sequence { get; }
        public string Text { get; }
        public VoiceSynthesisResult Result { get; }
    }
}
