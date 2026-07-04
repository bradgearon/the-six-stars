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
    }
}
