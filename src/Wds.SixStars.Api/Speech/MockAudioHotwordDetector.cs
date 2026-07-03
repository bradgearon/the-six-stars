using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Speech
{
    /// <summary>
    /// Test double for a real audio hotword model. Queue a Star id before pushing fake PCM.
    /// If no result is queued, it can optionally accept the preferred Star so local UI tests keep moving.
    /// </summary>
    public sealed class MockAudioHotwordDetector : IAudioHotwordDetector
    {
        private readonly ConcurrentQueue<HotwordDetectionResult> _queuedResults = new ConcurrentQueue<HotwordDetectionResult>();

        public bool DetectPreferredStarWhenQueueEmpty { get; set; }

        public void QueueDetectedStar(string starId, double confidence)
        {
            _queuedResults.Enqueue(new HotwordDetectionResult
            {
                Detected = !string.IsNullOrWhiteSpace(starId),
                StarId = starId ?? string.Empty,
                MatchedAlias = starId ?? string.Empty,
                Confidence = confidence,
                Source = "mock-audio-hotword"
            });
        }

        public void QueueMiss()
        {
            _queuedResults.Enqueue(HotwordDetectionResult.None("mock-audio-hotword"));
        }

        public Task<HotwordDetectionResult> DetectAsync(AudioHotwordDetectionRequest request, CancellationToken cancellationToken)
        {
            if (_queuedResults.TryDequeue(out HotwordDetectionResult? result))
            {
                result.DebugText = string.IsNullOrWhiteSpace(result.DebugText)
                    ? "Mock queued result used for probe #" + request.ProbeIndex + " ending at " + request.ProbeWindowEndMs.ToString("0") + " ms."
                    : result.DebugText;
                return Task.FromResult(result);
            }

            if (DetectPreferredStarWhenQueueEmpty && !string.IsNullOrWhiteSpace(request.PreferredStarId))
            {
                return Task.FromResult(new HotwordDetectionResult
                {
                    Detected = true,
                    StarId = request.PreferredStarId,
                    MatchedAlias = request.PreferredStarId,
                    Confidence = 0.90,
                    Source = "mock-audio-hotword-preferred",
                    DebugText = "No queued hotword result existed, so the preferred Star was accepted for local development on probe #" + request.ProbeIndex + "."
                });
            }

            string firstEquipped = request.WakeAliases.EquippedStars.FirstOrDefault() ?? string.Empty;
            if (DetectPreferredStarWhenQueueEmpty && !string.IsNullOrWhiteSpace(firstEquipped))
            {
                return Task.FromResult(new HotwordDetectionResult
                {
                    Detected = true,
                    StarId = firstEquipped,
                    MatchedAlias = firstEquipped,
                    Confidence = 0.85,
                    Source = "mock-audio-hotword-equipped"
                });
            }

            return Task.FromResult(HotwordDetectionResult.None("mock-audio-hotword"));
        }
    }
}
