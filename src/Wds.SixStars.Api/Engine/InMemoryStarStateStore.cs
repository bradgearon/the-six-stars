using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Engine
{
    public sealed class InMemoryStarStateStore : IStarStateStore
    {
        private readonly ConcurrentDictionary<string, StarState> _states = new ConcurrentDictionary<string, StarState>(StringComparer.OrdinalIgnoreCase);

        public Task<StarState> GetAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            string key = BuildKey(sessionId, starId);
            StarState state = _states.GetOrAdd(key, _ => CreateDefaultState(starId));
            NormalizeState(state);
            return Task.FromResult(state);
        }

        public Task SaveAsync(string sessionId, StarState state, CancellationToken cancellationToken)
        {
            string key = BuildKey(sessionId, state.StarId);
            _states[key] = state;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StarState>> GetAllAsync(string sessionId, CancellationToken cancellationToken)
        {
            string prefix = sessionId + ":";
            IReadOnlyList<StarState> states = _states
                .Where(x => x.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)
                .ToList();
            return Task.FromResult(states);
        }

        public Task ReplaceSessionAsync(string sessionId, IEnumerable<StarState> states, CancellationToken cancellationToken)
        {
            string prefix = sessionId + ":";
            foreach (string key in _states.Keys.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _states.TryRemove(key, out _);
            }

            foreach (StarState state in states)
            {
                _states[BuildKey(sessionId, state.StarId)] = state;
            }

            return Task.CompletedTask;
        }


        public Task ResetStarAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            string key = BuildKey(sessionId, starId);
            _states[key] = CreateDefaultState(starId);
            return Task.CompletedTask;
        }

        private static StarState CreateDefaultState(string starId)
        {
            bool elusiaa = string.Equals(starId, "Elusiaa", StringComparison.OrdinalIgnoreCase);
            return new StarState
            {
                StarId = starId,
                IsMet = elusiaa,
                IsAlly = elusiaa,
                LastDailyStateSourceUpdateAt = DateTimeOffset.UtcNow,
                TodayState = new StarDailyStateContext(),
                GirlfriendHourlyState = new StarGirlfriendHourlyStateContext(),
                GirlfriendDesiredStateHistory = new List<GirlfriendVectorHistoryEntry>(),
                GirlfriendNeedsMetHistory = new List<GirlfriendNeedFulfillmentHistoryEntry>(),
                GirlfriendNeedsMetPerformanceVector = new List<GirlfriendVectorItem>(),
                GirlfriendWantsMetPerformanceVector = new List<GirlfriendVectorItem>(),
                OtherStars = new List<InterstarRelationshipState>()
            };
        }

        private static void NormalizeState(StarState state)
        {
            if (state.RelationshipEvents == null) state.RelationshipEvents = new List<string>();
            if (state.OtherStars == null) state.OtherStars = new List<InterstarRelationshipState>();
            foreach (InterstarRelationshipState relation in state.OtherStars.Where(x => x != null))
            {
                if (relation.History == null) relation.History = new List<InterstarRelationshipHistoryEntry>();
            }
            if (state.ActiveEffectors == null) state.ActiveEffectors = new List<StarRelationshipEffector>();
            if (state.EmotionVectorHistory == null) state.EmotionVectorHistory = new List<StarEmotionVectorHistoryEntry>();
            if (state.RelationshipScoreAccumulators == null) state.RelationshipScoreAccumulators = new List<StarRelationshipScoreAccumulator>();
            if (state.TodayState == null) state.TodayState = new StarDailyStateContext();
            if (state.GirlfriendHourlyState == null) state.GirlfriendHourlyState = new StarGirlfriendHourlyStateContext();
            if (state.GirlfriendDesiredStateHistory == null) state.GirlfriendDesiredStateHistory = new List<GirlfriendVectorHistoryEntry>();
            if (state.GirlfriendNeedsMetHistory == null) state.GirlfriendNeedsMetHistory = new List<GirlfriendNeedFulfillmentHistoryEntry>();
            if (state.GirlfriendNeedsMetPerformanceVector == null) state.GirlfriendNeedsMetPerformanceVector = new List<GirlfriendVectorItem>();
            if (state.GirlfriendWantsMetPerformanceVector == null) state.GirlfriendWantsMetPerformanceVector = new List<GirlfriendVectorItem>();
            if (state.IsAlly) state.IsMet = true;
            if (string.Equals(state.StarId, "Elusiaa", StringComparison.OrdinalIgnoreCase))
            {
                state.IsMet = true;
                state.IsAlly = true;
            }
        }

        private static string BuildKey(string sessionId, string starId)
        {
            return sessionId + ":" + starId;
        }
    }
}
