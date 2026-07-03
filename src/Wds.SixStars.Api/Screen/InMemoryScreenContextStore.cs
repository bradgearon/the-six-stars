using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Screen
{
    public sealed class InMemoryScreenContextStore : IScreenContextStore
    {
        private readonly ConcurrentDictionary<string, List<ScreenCaptureSummary>> _captures = new ConcurrentDictionary<string, List<ScreenCaptureSummary>>(StringComparer.OrdinalIgnoreCase);
        private readonly int _maxRetained;

        public InMemoryScreenContextStore(int maxRetained = 5)
        {
            _maxRetained = Math.Max(1, maxRetained);
        }

        public Task AddAsync(string sessionId, ScreenCaptureSummary capture, CancellationToken cancellationToken)
        {
            List<ScreenCaptureSummary> list = _captures.GetOrAdd(sessionId ?? string.Empty, _ => new List<ScreenCaptureSummary>());
            lock (list)
            {
                list.Add(capture);
                list.Sort((a, b) => b.CapturedAt.CompareTo(a.CapturedAt));
                while (list.Count > _maxRetained)
                {
                    list.RemoveAt(list.Count - 1);
                }
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScreenCaptureSummary>> GetRecentAsync(string sessionId, int maxCount, CancellationToken cancellationToken)
        {
            if (!_captures.TryGetValue(sessionId ?? string.Empty, out List<ScreenCaptureSummary>? list))
            {
                return Task.FromResult((IReadOnlyList<ScreenCaptureSummary>)Array.Empty<ScreenCaptureSummary>());
            }

            lock (list)
            {
                IReadOnlyList<ScreenCaptureSummary> result = list
                    .OrderByDescending(x => x.CapturedAt)
                    .Take(Math.Max(1, maxCount))
                    .Select(Clone)
                    .ToList();
                return Task.FromResult(result);
            }
        }

        public Task ClearAsync(string sessionId, CancellationToken cancellationToken)
        {
            _captures.TryRemove(sessionId ?? string.Empty, out _);
            return Task.CompletedTask;
        }

        private static ScreenCaptureSummary Clone(ScreenCaptureSummary source)
        {
            ScreenCaptureSummary clone = new ScreenCaptureSummary
            {
                CapturedAt = source.CapturedAt,
                ActiveApp = source.ActiveApp,
                ActivityType = source.ActivityType,
                LikelyActivity = source.LikelyActivity,
                CompanionSafeSummary = source.CompanionSafeSummary,
                Sensitivity = source.Sensitivity,
                ImageReference = source.ImageReference,
                ImageBase64Jpeg = source.ImageBase64Jpeg,
                Confidence = source.Confidence
            };
            clone.VisibleTextHighlights.AddRange(source.VisibleTextHighlights);
            clone.VisibleProblems.AddRange(source.VisibleProblems);
            return clone;
        }
    }
}
