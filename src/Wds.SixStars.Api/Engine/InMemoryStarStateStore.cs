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
            StarState state = _states.GetOrAdd(starId, _ => CreateDefaultState(starId));
            NormalizeState(state);
            return Task.FromResult(state);
        }

        public Task SaveAsync(string sessionId, StarState state, CancellationToken cancellationToken)
        {
            _states[state.StarId] = state;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StarState>> GetAllAsync(string sessionId, CancellationToken cancellationToken)
        {
            IReadOnlyList<StarState> states = _states
                .Select(x => x.Value)
                .ToList();
            return Task.FromResult(states);
        }

        public Task ReplaceSessionAsync(string sessionId, IEnumerable<StarState> states, CancellationToken cancellationToken)
        {
            _states.Clear();

            foreach (StarState state in states)
            {
                _states[state.StarId] = state;
            }

            return Task.CompletedTask;
        }


        public Task ResetStarAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            _states[starId] = CreateDefaultState(starId);
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
                GirlfriendDesiredNeedVectorHistory = new List<string>(),
                GirlfriendDesiredWantVectorHistory = new List<string>(),
                GirlfriendMetNeedVectorHistory = new List<string>(),
                GirlfriendMetWantVectorHistory = new List<string>(),
                GirlfriendNeedsMetPerformanceVector = new List<GirlfriendVectorItem>(),
                GirlfriendWantsMetPerformanceVector = new List<GirlfriendVectorItem>(),
                GirlfriendCoupleActivityScores = new List<GirlfriendCoupleActivityScore>(),
                GirlfriendCoupleActivityHistory = new List<GirlfriendCoupleActivityHistoryEntry>(),
                GirlfriendCoupleActivityBestExamples = new List<GirlfriendCoupleActivityBestExample>(),
                IncomingUnresolvedVectors = new List<string>(),
                OutgoingUnresolvedVectors = new List<string>(),
                UnresolvedResolutionHistory = new List<string>(),
                OtherStars = new List<InterstarRelationshipState>()
            };
        }

        private static void NormalizeState(StarState state)
        {
            if (state.RelationshipEvents == null) state.RelationshipEvents = new List<string>();
            if (state.ShortTermMemory == null) state.ShortTermMemory = new List<StarShortTermMemoryEntry>();
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
            if (state.GirlfriendDesiredNeedVectorHistory == null) state.GirlfriendDesiredNeedVectorHistory = new List<string>();
            if (state.GirlfriendDesiredWantVectorHistory == null) state.GirlfriendDesiredWantVectorHistory = new List<string>();
            if (state.GirlfriendMetNeedVectorHistory == null) state.GirlfriendMetNeedVectorHistory = new List<string>();
            if (state.GirlfriendMetWantVectorHistory == null) state.GirlfriendMetWantVectorHistory = new List<string>();
            if (state.GirlfriendNeedsMetPerformanceVector == null) state.GirlfriendNeedsMetPerformanceVector = new List<GirlfriendVectorItem>();
            if (state.GirlfriendWantsMetPerformanceVector == null) state.GirlfriendWantsMetPerformanceVector = new List<GirlfriendVectorItem>();
            if (state.GirlfriendCoupleActivityScores == null) state.GirlfriendCoupleActivityScores = new List<GirlfriendCoupleActivityScore>();
            if (state.GirlfriendCoupleActivityHistory == null) state.GirlfriendCoupleActivityHistory = new List<GirlfriendCoupleActivityHistoryEntry>();
            if (state.GirlfriendCoupleActivityBestExamples == null) state.GirlfriendCoupleActivityBestExamples = new List<GirlfriendCoupleActivityBestExample>();
            if (state.IncomingUnresolvedVectors == null) state.IncomingUnresolvedVectors = new List<string>();
            if (state.OutgoingUnresolvedVectors == null) state.OutgoingUnresolvedVectors = new List<string>();
            if (state.UnresolvedResolutionHistory == null) state.UnresolvedResolutionHistory = new List<string>();
            if (state.IsAlly) state.IsMet = true;
            if (string.Equals(state.StarId, "Elusiaa", StringComparison.OrdinalIgnoreCase))
            {
                state.IsMet = true;
                state.IsAlly = true;
            }
        }

    }
}
