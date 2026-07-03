using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// Separate output pipeline: Star response text -> voice generation -> playback endpoint.
    /// Keep this separate from speech input so Unity can choose whether to play audio, show subtitles, or both.
    ///
    /// If a playback observer is supplied, this pipeline notifies it immediately before and after playback.
    /// The speech input pipeline implements that observer so it can mute input while generated Star speech is playing.
    /// 
    /// Supports speech interruption - user can interrupt Star's speech by speaking.
    /// </summary>
    public sealed class StarVoiceResponsePipeline
    {
        private readonly IStarVoiceGenerator _voiceGenerator;
        private readonly IWavePlaybackEndpoint _playbackEndpoint;
        private readonly IStarSpeechPlaybackObserver? _playbackObserver;
        private readonly SpeechInterruptionManager? _interruptionManager;

        public StarVoiceResponsePipeline(
            IStarVoiceGenerator voiceGenerator,
            IWavePlaybackEndpoint playbackEndpoint,
            IStarSpeechPlaybackObserver? playbackObserver = null,
            SpeechInterruptionManager? interruptionManager = null)
        {
            _voiceGenerator = voiceGenerator;
            _playbackEndpoint = playbackEndpoint;
            _playbackObserver = playbackObserver;
            _interruptionManager = interruptionManager;
        }

        public event EventHandler<VoiceSynthesisResultEventArgs>? VoiceGenerated;
        public event EventHandler<VoiceSynthesisResultEventArgs>? VoicePlaybackStarting;
        public event EventHandler<VoiceSynthesisResultEventArgs>? VoicePlaybackCompleted;
        public event EventHandler<VoiceSynthesisResultEventArgs>? VoicePlaybackSubmitted;
        public event EventHandler<SpeechInterruptedEventArgs>? VoiceInterrupted;

        public async Task<VoiceSynthesisResult> SpeakAsync(string sessionId, StarInteractionResponse response, CancellationToken cancellationToken)
        {
            // Start tracking speech generation for interruption
            CancellationToken interruptionToken = _interruptionManager?.StartSpeechGeneration() ?? CancellationToken.None;
            CancellationTokenSource? linkedCts = null;

            try
            {
                // Link cancellation tokens (external + interruption)
                if (interruptionToken != CancellationToken.None)
                {
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interruptionToken);
                    cancellationToken = linkedCts.Token;
                }

                VoiceSynthesisRequest request = new VoiceSynthesisRequest
                {
                    SessionId = sessionId,
                    StarId = response.StarId,
                    Text = response.SpokenText,
                    TextParts = response.SpokenParts
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim())
                        .ToList(),
                    SourceResponse = response,
                    VoiceSettings = CreateSettingsFromResponse(response)
                };

                VoiceSynthesisResult result = await _voiceGenerator.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
                _interruptionManager?.SpeechGenerationCompleted();
                VoiceGenerated?.Invoke(this, new VoiceSynthesisResultEventArgs(result));

                // Start tracking playback for interruption
                CancellationToken playbackInterruptionToken = _interruptionManager?.StartSpeechPlayback() ?? CancellationToken.None;
                if (playbackInterruptionToken != CancellationToken.None)
                {
                    linkedCts?.Dispose();
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, playbackInterruptionToken);
                    cancellationToken = linkedCts.Token;
                }

                VoicePlaybackStarting?.Invoke(this, new VoiceSynthesisResultEventArgs(result));
                _playbackObserver?.NotifyStarSpeechStarted();

                try
                {
                    await _playbackEndpoint.PlayWavAsync(result, cancellationToken).ConfigureAwait(false);
                    VoicePlaybackSubmitted?.Invoke(this, new VoiceSynthesisResultEventArgs(result));
                }
                finally
                {
                    _interruptionManager?.SpeechPlaybackCompleted();
                    _playbackObserver?.NotifyStarSpeechCompleted();
                    VoicePlaybackCompleted?.Invoke(this, new VoiceSynthesisResultEventArgs(result));
                }

                return result;
            }
            catch (OperationCanceledException) when (interruptionToken.IsCancellationRequested)
            {
                // Speech was interrupted by user
                System.Diagnostics.Debug.WriteLine("[StarVoiceResponsePipeline] Speech interrupted by user");
                VoiceInterrupted?.Invoke(this, new SpeechInterruptedEventArgs 
                { 
                    Reason = "User interrupted",
                    InterruptedDuringGeneration = _interruptionManager?.IsStarSpeaking == false
                });
                throw;
            }
            finally
            {
                linkedCts?.Dispose();
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
    }
}
