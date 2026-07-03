using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Screen
{
    /// <summary>
    /// Converts a screen image into a compact, companion-safe summary that can be passed into Star prompts.
    /// This can be backed by a local VLM, remote endpoint, or test mock.
    /// </summary>
    public interface IScreenSummarizer
    {
        Task<ScreenCaptureSummary> SummarizeAsync(ScreenSummarizationRequest request, CancellationToken cancellationToken);
    }
}
