using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Audio;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// Converts Whisper transcripts into StarInteractionRequest objects and sends them to SixStarsRuntime.
    /// This is still UI/Unity agnostic: the game can either use this coordinator or build requests itself.
    ///
    /// The preferred live path is audio hotword gating in WhisperSpeechInputPipeline.
    /// This coordinator still supports text wake detection as a fallback for typed text, mocked Whisper,
    /// or speech pipelines that do not have an audio hotword model wired yet.
    /// </summary>
    public sealed class SpeechToStarCoordinator
    {
        private readonly SixStarsRuntime _runtime;
        private readonly IWakeWordDetector _wakeWordDetector;
        private readonly SpeechToStarOptions _options;

        public SpeechToStarCoordinator(SixStarsRuntime runtime, IWakeWordDetector wakeWordDetector, SpeechToStarOptions options)
        {
            _runtime = runtime;
            _wakeWordDetector = wakeWordDetector;
            _options = options;
        }

        public event EventHandler<RecognizedSpeechEventArgs>? SpeechRecognized;
        public event EventHandler<StarSpeechResponseEventArgs>? StarResponded;

        public async Task<RecognizedSpeech> RecognizeAsync(SpeechTranscript transcript, CancellationToken cancellationToken)
        {
            string? detectedStar = string.IsNullOrWhiteSpace(transcript.SuggestedStarId)
                ? null
                : transcript.SuggestedStarId;

            bool wakeDetected = transcript.WakeWordDetected && !string.IsNullOrWhiteSpace(detectedStar);

            if (string.IsNullOrWhiteSpace(detectedStar))
            {
                detectedStar = await _wakeWordDetector.TryDetectStarAsync(transcript.Text, _options.WakeAliases, cancellationToken).ConfigureAwait(false);
                wakeDetected = !string.IsNullOrWhiteSpace(detectedStar);
            }

            RecognizedSpeech recognized = new RecognizedSpeech
            {
                Transcript = transcript,
                TargetStarId = !string.IsNullOrWhiteSpace(detectedStar) ? detectedStar! : _options.DefaultStarId,
                WakeWordDetected = wakeDetected
            };

            if (_options.RequireWakeWord && !wakeDetected && string.IsNullOrWhiteSpace(transcript.SuggestedStarId))
            {
                recognized.WasIgnored = true;
                recognized.IgnoreReason = "No equipped Star wake alias was detected.";
            }

            SpeechRecognized?.Invoke(this, new RecognizedSpeechEventArgs(recognized));
            return recognized;
        }

        public async Task<StarInteractionResponse?> ProcessTranscriptAsync(SpeechTranscript transcript, CancellationToken cancellationToken)
        {
            RecognizedSpeech recognized = await RecognizeAsync(transcript, cancellationToken).ConfigureAwait(false);
            if (recognized.WasIgnored)
            {
                return null;
            }

            StarInteractionRequest request = new StarInteractionRequest
            {
                SessionId = _options.SessionId,
                StarId = recognized.TargetStarId,
                Speaker = _options.Speaker,
                RequestType = _options.RequestType,
                PlayerText = transcript.Text,
                GameContext = CloneGameContext(_options.GameContext),
                StoryOptions = _options.StoryOptions
            };

            request.GameContext.RecentEvents.Add("Voice input captured by WhisperSpeechInputPipeline");
            request.GameContext.Flags["inputMode"] = "voice";
            request.GameContext.Flags["wakeWordDetected"] = recognized.WakeWordDetected ? "true" : "false";
            request.GameContext.Flags["speechConfidence"] = transcript.Confidence.ToString("0.00");
            request.GameContext.Flags["speechListenMode"] = transcript.ListenMode;
            request.GameContext.Flags["audioHotwordConfidence"] = transcript.HotwordConfidence.ToString("0.00");

            StarInteractionResponse response = await _runtime.ProcessAsync(request, cancellationToken).ConfigureAwait(false);
            StarResponded?.Invoke(this, new StarSpeechResponseEventArgs(recognized, response));
            return response;
        }

        private static GameContext CloneGameContext(GameContext source)
        {
            GameContext clone = new GameContext
            {
                Area = source.Area,
                Quest = source.Quest,
                BattleState = source.BattleState,
                RomanceMode = source.RomanceMode,
                BenaveilPressureLevel = source.BenaveilPressureLevel
            };

            foreach (string star in source.EquippedStars)
            {
                clone.EquippedStars.Add(star);
            }

            foreach (string recentEvent in source.RecentEvents)
            {
                clone.RecentEvents.Add(recentEvent);
            }

            foreach (System.Collections.Generic.KeyValuePair<string, string> flag in source.Flags)
            {
                clone.Flags[flag.Key] = flag.Value;
            }

            return clone;
        }
    }
}
