using System;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Ai
{
    public sealed class FallbackStarAgent : IStarAgent
    {
        private readonly IStarAgent _primary;
        private readonly IStarAgent _fallback;

        public FallbackStarAgent(IStarAgent primary, IStarAgent fallback)
        {
            _primary = primary;
            _fallback = fallback;
        }

        public async Task<StarInteractionResponse> GenerateAsync(StarProfile profile, StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[FallbackStarAgent] Attempting to use primary LLM for {request.StarId}...");
                StarInteractionResponse response = await _primary.GenerateAsync(profile, request, state, cancellationToken).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[FallbackStarAgent] ? Primary LLM succeeded. Generated {response.SpokenText.Length} characters.");
                return response;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FallbackStarAgent] ? Primary LLM failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[FallbackStarAgent] Falling back to MockStarAgent...");
                StarInteractionResponse response = await _fallback.GenerateAsync(profile, request, state, cancellationToken).ConfigureAwait(false);
                response.Debug.RiskFlags.Add("llm_primary_failed");
                response.Debug.Notes.Add("? FallbackStarAgent used MOCK data after LLM failure: " + ex.Message);
                response.Debug.Notes.Add("Configure LLM endpoint in appsettings.json to use real AI responses.");
                return response;
            }
        }
    }
}
