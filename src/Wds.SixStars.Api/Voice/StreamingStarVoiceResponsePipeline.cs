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
    /// Streaming voice pipeline that receives generated WAV chunks from an IStreamingVoiceGenerator,
    /// buffers them by chunk index, and consumes them in strict order. Desktop uses AutoPlayChunks=true.
    /// Unity can set AutoPlayChunks=false, subscribe to ChunkReadyForConsumer, and play/store the WAV
    /// bytes with its own AudioSource queue.
    /// </summary>
    public sealed class StreamingStarVoiceResponsePipeline : IDisposable
    {
        private readonly IStreamingVoiceGenerator _streamingGenerator;
        private readonly IWavePlaybackEndpoint _playbackEndpoint;
        private readonly IStarSpeechPlaybackObserver? _playbackObserver;
        private readonly SpeechInterruptionManager? _interruptionManager;
        private readonly SortedDictionary<int, AudioChunkEventArgs> _audioQueue = new SortedDictionary<int, AudioChunkEventArgs>();
        private readonly SemaphoreSlim _queueLock = new SemaphoreSlim(1, 1);
        private readonly ManualResetEventSlim _playbackPause = new ManualResetEventSlim(true);
        private Task? _playbackTask;
        private CancellationTokenSource? _playbackCts;
        private bool _isPlayingChunks;
        private int _chunksReceived;
        private int _chunksPlayed;
        private volatile bool _generationComplete;
        private volatile int _expectedTotalChunks;
        private GenerationErrorEventArgs? _generationError;

        public StreamingStarVoiceResponsePipeline(
            IStreamingVoiceGenerator streamingGenerator,
            IWavePlaybackEndpoint playbackEndpoint,
            IStarSpeechPlaybackObserver? playbackObserver = null,
            SpeechInterruptionManager? interruptionManager = null)
        {
            _streamingGenerator = streamingGenerator ?? throw new ArgumentNullException(nameof(streamingGenerator));
            _playbackEndpoint = playbackEndpoint ?? throw new ArgumentNullException(nameof(playbackEndpoint));
            _playbackObserver = playbackObserver;
            _interruptionManager = interruptionManager;

            _streamingGenerator.AudioChunkReady += OnAudioChunkReady;
            _streamingGenerator.GenerationComplete += OnGenerationComplete;
            _streamingGenerator.GenerationError += OnGenerationError;
        }

        public event EventHandler<AudioChunkEventArgs>? ChunkReceived;
        public event EventHandler<VoiceSynthesisResultEventArgs>? ChunkReadyForConsumer;
        public event EventHandler<AudioChunkEventArgs>? ChunkPlaybackStarted;
        public event EventHandler<AudioChunkEventArgs>? ChunkPlaybackCompleted;
        public event EventHandler<GenerationCompleteEventArgs>? AllChunksComplete;
        public event EventHandler<SpeechInterruptedEventArgs>? VoiceInterrupted;
        public event EventHandler<GenerationErrorEventArgs>? GenerationError;

        /// <summary>
        /// When true, the pipeline plays each ordered WAV chunk through IWavePlaybackEndpoint.
        /// When false, chunks are still ordered and surfaced through ChunkReadyForConsumer so Unity
        /// or another host can enqueue/play the WAV bytes itself.
        /// </summary>
        public bool AutoPlayChunks { get; set; } = true;

        public bool IsPaused => !_playbackPause.IsSet;

        public async Task SpeakStreamingAsync(string sessionId, StarInteractionResponse response, CancellationToken cancellationToken)
        {
            Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Starting streaming speech for '{response.SpokenText?.Substring(0, Math.Min(50, response.SpokenText?.Length ?? 0))}...'");

            _chunksReceived = 0;
            _chunksPlayed = 0;
            _generationComplete = false;
            _expectedTotalChunks = 0;
            _generationError = null;
            _playbackPause.Set();
            await _queueLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _audioQueue.Clear();
            }
            finally
            {
                _queueLock.Release();
            }

            _playbackCts?.Cancel();
            _playbackCts = new CancellationTokenSource();
            CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _playbackCts.Token);

            IDisposable? outputKeepAlive = null;

            try
            {
                CancellationToken interruptionToken = _interruptionManager?.StartSpeechGeneration() ?? CancellationToken.None;
                if (interruptionToken != CancellationToken.None)
                {
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, interruptionToken);
                }

                _isPlayingChunks = true;
                _playbackTask = Task.Run(() => PlaybackLoopAsync(linkedCts.Token), linkedCts.Token);

                _playbackObserver?.NotifyStarSpeechStarted();

                if (AutoPlayChunks && _playbackEndpoint is IAudioOutputKeepAliveEndpoint keepAliveEndpoint)
                {
                    outputKeepAlive = keepAliveEndpoint.StartOutputKeepAlive("streaming speech generation and playback", linkedCts.Token);
                }

                VoiceSynthesisRequest request = new VoiceSynthesisRequest
                {
                    SessionId = sessionId,
                    StarId = response.StarId,
                    // Preserve the LLM-provided speech array. Each spokenText item is one intentional
                    // TTS/breath chunk. The server must not re-split or merge these parts.
                    Text = response.SpokenText,
                    TextParts = response.SpokenParts
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim())
                        .ToList(),
                    SourceResponse = response,
                    VoiceSettings = CreateSettingsFromResponse(response)
                };

                await _streamingGenerator.GenerateStreamingAsync(request, linkedCts.Token).ConfigureAwait(false);

                if (_playbackTask != null)
                {
                    await _playbackTask.ConfigureAwait(false);
                }

                if (_generationError != null)
                {
                    throw new InvalidOperationException(
                        "Streaming TTS generation failed: " + _generationError.ErrorMessage,
                        _generationError.Exception);
                }

                Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Streaming speech complete: {_chunksPlayed} chunks consumed");
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[StreamingStarVoiceResponsePipeline] Speech interrupted by user");
                _streamingGenerator.CancelGeneration();
                VoiceInterrupted?.Invoke(this, new SpeechInterruptedEventArgs
                {
                    Reason = "User interrupted",
                    InterruptedDuringGeneration = _isPlayingChunks
                });
                throw;
            }
            finally
            {
                // GenerateStreamingAsync can fail before the server sends a completion event
                // (for example while qwentts.cpp is still starting). Always cancel
                // and join the ordered consumer so an empty playback loop cannot survive forever.
                try
                {
                    linkedCts.Cancel();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[StreamingStarVoiceResponsePipeline] Could not cancel the playback consumer during cleanup: " + ex.Message);
                }
                if (_playbackTask != null && !_playbackTask.IsCompleted)
                {
                    try
                    {
                        await _playbackTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected while unwinding a failed or replaced TTS request.
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[StreamingStarVoiceResponsePipeline] Playback consumer ended with an error during cleanup: " + ex.Message);
                    }
                }
                outputKeepAlive?.Dispose();
                _isPlayingChunks = false;
                _playbackPause.Set();
                _interruptionManager?.SpeechGenerationCompleted();
                _interruptionManager?.SpeechPlaybackCompleted();
                _playbackObserver?.NotifyStarSpeechCompleted();
                linkedCts.Dispose();
            }
        }

        public void CancelCurrentSpeech()
        {
            Debug.WriteLine("[StreamingStarVoiceResponsePipeline] Cancelling current speech");
            _playbackPause.Set();
            _playbackCts?.Cancel();
            _streamingGenerator.CancelGeneration();
        }

        public void PausePlayback()
        {
            Debug.WriteLine("[StreamingStarVoiceResponsePipeline] Pausing queued playback. Current chunk will finish; next chunk waits.");
            _playbackPause.Reset();
        }

        public void ResumePlayback()
        {
            Debug.WriteLine("[StreamingStarVoiceResponsePipeline] Resuming queued playback.");
            _playbackPause.Set();
        }

        private void OnAudioChunkReady(object? sender, AudioChunkEventArgs e)
        {
            _chunksReceived++;
            if (e.TotalChunks > _expectedTotalChunks)
            {
                _expectedTotalChunks = e.TotalChunks;
            }

            byte[] audioCopy = CopyBytes(e.AudioData);
            AudioChunkEventArgs queuedChunk = new AudioChunkEventArgs
            {
                ChunkIndex = e.ChunkIndex,
                TotalChunks = e.TotalChunks,
                AudioData = audioCopy,
                MimeType = e.MimeType,
                Text = e.Text,
                StarId = e.StarId,
                VoiceId = e.VoiceId
            };

            Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Audio chunk {queuedChunk.ChunkIndex + 1}/{queuedChunk.TotalChunks} received ({queuedChunk.AudioData.Length} bytes, copied to owned buffer)");

            _queueLock.Wait();
            try
            {
                _audioQueue[queuedChunk.ChunkIndex] = queuedChunk;
            }
            finally
            {
                _queueLock.Release();
            }

            ChunkReceived?.Invoke(this, queuedChunk);
        }

        private void OnGenerationComplete(object? sender, GenerationCompleteEventArgs e)
        {
            _expectedTotalChunks = Math.Max(_expectedTotalChunks, e.TotalChunks);
            _generationComplete = true;
            Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Generation complete: {e.TotalChunks} chunks, cancelled={e.WasCancelled}. Playback will continue until ordered queue is drained.");
            AllChunksComplete?.Invoke(this, e);
        }

        private void OnGenerationError(object? sender, GenerationErrorEventArgs e)
        {
            Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Generation error: {e.ErrorMessage}");
            _generationError = e;
            _generationComplete = true;
            GenerationError?.Invoke(this, e);
        }

        private async Task PlaybackLoopAsync(CancellationToken cancellationToken)
        {
            Debug.WriteLine("[StreamingStarVoiceResponsePipeline] Ordered playback loop started");

            int chunksProcessed = 0;
            int nextChunkIndex = 0;
            int emptyPollsAfterComplete = 0;
            int waitForMissingChunkPolls = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);

                    AudioChunkEventArgs? chunk = null;
                    int queueCountAfterDequeue = 0;
                    int? lowestQueuedIndex = null;

                    await _queueLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (_audioQueue.TryGetValue(nextChunkIndex, out AudioChunkEventArgs? readyChunk))
                        {
                            chunk = readyChunk;
                            _audioQueue.Remove(nextChunkIndex);
                        }
                        else if (_audioQueue.Count > 0)
                        {
                            lowestQueuedIndex = _audioQueue.Keys.First();
                        }

                        queueCountAfterDequeue = _audioQueue.Count;
                    }
                    finally
                    {
                        _queueLock.Release();
                    }

                    if (chunk != null)
                    {
                        emptyPollsAfterComplete = 0;
                        waitForMissingChunkPolls = 0;

                        try
                        {
                            chunksProcessed++;
                            int expected = Math.Max(_expectedTotalChunks, chunk.TotalChunks);
                            if (expected > _expectedTotalChunks)
                            {
                                _expectedTotalChunks = expected;
                            }

                            VoiceSynthesisResult result = new VoiceSynthesisResult
                            {
                                StarId = chunk.StarId,
                                VoiceId = chunk.VoiceId,
                                WavAudio = CopyBytes(chunk.AudioData),
                                MimeType = chunk.MimeType,
                                DurationMs = chunk.DurationMs,
                                IsStreamingPcmWindow = chunk.IsStreamingPcmWindow,
                                Instructions = chunk.Text,
                                Source = "streaming-qwentts-http"
                            };

                            Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Consuming ordered chunk {chunk.ChunkIndex + 1}/{chunk.TotalChunks} ({chunk.AudioData.Length} bytes). Queue remaining after dequeue: {queueCountAfterDequeue}. Processed so far: {chunksProcessed}.");
                            ChunkReadyForConsumer?.Invoke(this, new VoiceSynthesisResultEventArgs(result));

                            if (AutoPlayChunks)
                            {
                                ChunkPlaybackStarted?.Invoke(this, chunk);

                                CancellationToken playbackToken = cancellationToken;
                                CancellationTokenSource? playbackLinkedCts = null;
                                CancellationToken playbackInterruptionToken = _interruptionManager?.StartSpeechPlayback() ?? CancellationToken.None;
                                if (playbackInterruptionToken != CancellationToken.None)
                                {
                                    playbackLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, playbackInterruptionToken);
                                    playbackToken = playbackLinkedCts.Token;
                                }

                                try
                                {
                                    Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Calling PlayWavAsync for chunk {chunk.ChunkIndex + 1}...");
                                    await _playbackEndpoint.PlayWavAsync(result, playbackToken).ConfigureAwait(false);
                                    Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] PlayWavAsync returned for chunk {chunk.ChunkIndex + 1}");
                                }
                                finally
                                {
                                    playbackLinkedCts?.Dispose();
                                    _interruptionManager?.SpeechPlaybackCompleted();
                                }

                                ChunkPlaybackCompleted?.Invoke(this, chunk);
                            }
                            else
                            {
                                Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] AutoPlayChunks=false; chunk {chunk.ChunkIndex + 1}/{chunk.TotalChunks} surfaced to host and marked consumed.");
                            }

                            _chunksPlayed++;
                            nextChunkIndex++;
                            Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Chunk {chunk.ChunkIndex + 1}/{chunk.TotalChunks} consumed. Played/consumed {_chunksPlayed}, received {_chunksReceived}, expected {_expectedTotalChunks}.");

                            // Small device reopen breath for WPF/NAudio. Unity hosts can set AutoPlayChunks=false.
                            if (AutoPlayChunks)
                            {
                                await Task.Delay(35, cancellationToken).ConfigureAwait(false);
                            }

                            continue;
                        }
                        catch (OperationCanceledException)
                        {
                            Debug.WriteLine("[StreamingStarVoiceResponsePipeline] Playback cancelled");
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Playback error on chunk {chunk.ChunkIndex + 1}: {ex.Message}");
                            Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Stack trace: {ex.StackTrace}");
                            nextChunkIndex++;
                        }
                    }
                    else
                    {
                        if (lowestQueuedIndex.HasValue)
                        {
                            waitForMissingChunkPolls++;
                            if (waitForMissingChunkPolls % 20 == 0)
                            {
                                Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Waiting for chunk {nextChunkIndex + 1}; lowest queued chunk is {lowestQueuedIndex.Value + 1}. Received {_chunksReceived}, expected {_expectedTotalChunks}.");
                            }
                        }

                        if (_generationComplete)
                        {
                            int queueCount;
                            await _queueLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                            try
                            {
                                queueCount = _audioQueue.Count;
                            }
                            finally
                            {
                                _queueLock.Release();
                            }

                            int expected = Math.Max(_expectedTotalChunks, _chunksReceived);
                            bool allKnownChunksProcessed = expected <= 0 || chunksProcessed >= expected;
                            if (queueCount == 0 && allKnownChunksProcessed)
                            {
                                Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Generation complete and ordered queue empty. Processed {chunksProcessed}, received {_chunksReceived}, expected {_expectedTotalChunks}. Exiting playback loop.");
                                break;
                            }

                            // If generation is complete and one chunk is missing forever, do not deadlock.
                            // After a short wait, skip the missing index and continue with whatever is queued.
                            if (queueCount > 0 && waitForMissingChunkPolls >= 120)
                            {
                                Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Missing chunk {nextChunkIndex + 1} after generation complete. Skipping to avoid deadlock.");
                                nextChunkIndex++;
                                waitForMissingChunkPolls = 0;
                                continue;
                            }

                            emptyPollsAfterComplete++;
                            if (emptyPollsAfterComplete % 20 == 0)
                            {
                                Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Waiting for remaining ordered chunks after generation complete. Processed {chunksProcessed}, received {_chunksReceived}, expected {_expectedTotalChunks}, queue={queueCount}.");
                            }
                        }

                        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                Debug.WriteLine($"[StreamingStarVoiceResponsePipeline] Playback loop ended. Total chunks processed: {chunksProcessed}, played/consumed {_chunksPlayed}, received {_chunksReceived}, expected {_expectedTotalChunks}, generationComplete={_generationComplete}");
            }
        }

        private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
        {
            while (!_playbackPause.IsSet)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
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
            if (source == null || source.Length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }

        public void Dispose()
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
            _queueLock?.Dispose();
            _playbackPause?.Dispose();

            if (_streamingGenerator is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
