using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.State
{
    public interface IStarMemoryApi
    {
        Task<StarState> GetStarStateAsync(string sessionId, string starId, CancellationToken cancellationToken);
        Task<StarMemorySnapshot> GetStarMemorySnapshotAsync(string sessionId, string starId, int recentHistoryCount, CancellationToken cancellationToken);
        Task<StarInteractionHistoryPage> GetStarInteractionHistoryAsync(string sessionId, string starId, int skip, int take, CancellationToken cancellationToken);
        Task<StarStateUpdateResult> UpdateStarWithInteractionAsync(StarStateUpdateRequest request, CancellationToken cancellationToken);
        Task<SixStarsSaveData> ExportSaveDataAsync(string sessionId, CancellationToken cancellationToken);
        Task ImportSaveDataAsync(SixStarsSaveData saveData, CancellationToken cancellationToken);
        Task<StarState> ResetStarMemoryAsync(string sessionId, string starId, CancellationToken cancellationToken);
        Task<StarState> SetStarAllyAsync(string sessionId, string starId, bool isAlly, CancellationToken cancellationToken);
        Task<StarState> SetStarMetAsync(string sessionId, string starId, bool isMet, CancellationToken cancellationToken);
        Task<StarState> SetStarWorldStateAsync(StarWorldStateUpdateRequest request, CancellationToken cancellationToken);
        Task<StarState> ApplyStarEffectorAsync(string sessionId, string starId, StarRelationshipEffector effector, CancellationToken cancellationToken);
        Task<StarState> ClearStarEffectorAsync(string sessionId, string starId, string effectorId, CancellationToken cancellationToken);
        Task<StarState> AddStarDialogueEntryAsync(StarDialogueMemoryUpdateRequest request, CancellationToken cancellationToken);
        Task<StarState> SetStarDialogueStateAsync(StarDialogueProgressUpdateRequest request, CancellationToken cancellationToken);
        Task<StarState> ClearStarDialogueStateAsync(string sessionId, string starId, CancellationToken cancellationToken);
        Task<StarState> SetStarDailyStateContextAsync(string sessionId, string starId, StarDailyStateContext context, CancellationToken cancellationToken);
    }
}
