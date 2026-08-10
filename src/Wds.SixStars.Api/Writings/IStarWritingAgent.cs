using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Stars;
using Wds.SixStars.Api.XPosting;

namespace Wds.SixStars.Api.Writings
{
    public interface IStarWritingAgent
    {
        Task<StarWritingGenerationResult> GenerateWritingCandidatesAsync(StarProfile profile, StarWritingNotesState state, IReadOnlyList<string> seedWords, string writingMethod, CancellationToken cancellationToken);
        Task<StarWritingImprovementResult> ImproveWritingCandidatesAsync(StarProfile profile, StarWritingNotesState state, StarWritingGenerationResult generation, int passNumber, int totalPasses, CancellationToken cancellationToken);
        Task<StarWritingReviewResult> RateWritingCandidatesAsync(StarProfile profile, StarWritingNotesState state, StarWritingGenerationResult generation, CancellationToken cancellationToken);
        Task<StarWritingStoryTermContributionResult> ContributeStoryTermsAsync(StarProfile profile, StarWritingNotesState viewpointState, IReadOnlyList<string> nounSeeds, IReadOnlyList<string> verbSeeds, IReadOnlyList<string> subjectsSoFar, IReadOnlyList<string> verbsSoFar, int phaseNumber, CancellationToken cancellationToken);
        Task<StarWritingStoryOutlineResult> CreateStoryOutlineAsync(StarProfile viewpointProfile, StarWritingNotesState state, IReadOnlyList<string> subjects, IReadOnlyList<string> verbs, IReadOnlyList<string> seedWords, CancellationToken cancellationToken);
        Task<StarWritingStoryBuildResult> BuildStoryPhaseAsync(StarProfile contributorProfile, StarProfile viewpointProfile, StarWritingNotesState viewpointState, StarWritingStoryOutlineResult outline, string currentStory, IReadOnlyList<string> subjects, IReadOnlyList<string> verbs, IReadOnlyList<string> seedWords, IReadOnlyList<string> priorContributorStarIds, int phaseNumber, bool pendingFinish, string finishRequestedByStarId, CancellationToken cancellationToken);
        Task<StarWritingStoryFinalResult> FinalizeStoryAsync(StarProfile viewpointProfile, StarWritingNotesState state, StarWritingStoryOutlineResult outline, string currentStory, IReadOnlyList<string> subjects, IReadOnlyList<string> verbs, IReadOnlyList<string> contributorStarIds, IReadOnlyList<string> seedWords, CancellationToken cancellationToken);
        Task<StarWritingRating> RateStoryAsync(StarProfile viewpointProfile, StarWritingNotesState state, string title, string story, IReadOnlyList<string> contributorStarIds, CancellationToken cancellationToken);
        Task<StarWritingPerformancePlan> PrepareWritingPerformanceAsync(StarProfile profile, StarWritingNotesState state, IReadOnlyList<PersistedStarWriting> pendingWritings, CancellationToken cancellationToken);
        Task<StarWritingPerformancePlan> PrepareConstellationStoryPerformanceAsync(StarProfile profile, StarWritingNotesState state, PersistedStarWriting story, CancellationToken cancellationToken);
        Task<StarXPostSelectionResult> PrepareXPostsAsync(StarProfile profile, StarWritingNotesState state, IReadOnlyList<PersistedStarWriting> unpostedWritings, int requestedCount, CancellationToken cancellationToken);
    }
}
