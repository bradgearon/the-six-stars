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
        Task<StarWritingPerformancePlan> PrepareWritingPerformanceAsync(StarProfile profile, StarWritingNotesState state, IReadOnlyList<PersistedStarWriting> pendingWritings, CancellationToken cancellationToken);
        Task<StarXPostSelectionResult> PrepareXPostsAsync(StarProfile profile, StarWritingNotesState state, IReadOnlyList<PersistedStarWriting> unpostedWritings, int requestedCount, CancellationToken cancellationToken);
    }
}
