using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Screen;
using Wds.SixStars.Api.Speech;
using Wds.SixStars.Api.State;

namespace Wds.SixStars.Api.Engine
{
    /// <summary>
    /// UI/Unity-friendly facade over SixStarsRuntime. The desktop app can call this with text input;
    /// speech can call it with Whisper transcripts; Unity can also use it for state/history/save chunks.
    /// </summary>
    public sealed class SixStarsConversationApi
    {
        private readonly SixStarsRuntime _runtime;

        public SixStarsConversationApi(SixStarsRuntime runtime)
        {
            _runtime = runtime;
        }

        public Task AddScreenCaptureSummaryAsync(string sessionId, ScreenCaptureSummary capture, CancellationToken cancellationToken)
        {
            return _runtime.AddScreenCaptureAsync(sessionId, capture, cancellationToken);
        }

        public Task<StarState> GetStarStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            return _runtime.GetStarStateAsync(sessionId, starId, cancellationToken);
        }

        public Task<StarDecisionScoreResponse> ScoreDecisionAsync(StarDecisionScoreRequest request, CancellationToken cancellationToken)
        {
            return _runtime.ScoreDecisionAsync(request, cancellationToken);
        }

        public Task<StarInteractionHistoryPage> GetStarInteractionHistoryAsync(string sessionId, string starId, int skip, int take, CancellationToken cancellationToken)
        {
            return _runtime.GetStarInteractionHistoryAsync(sessionId, starId, skip, take, cancellationToken);
        }

        public Task<StarState> SetStarAllyAsync(string sessionId, string starId, bool isAlly, CancellationToken cancellationToken)
        {
            return _runtime.SetStarAllyAsync(sessionId, starId, isAlly, cancellationToken);
        }

        public Task<StarState> SetStarMetAsync(string sessionId, string starId, bool isMet, CancellationToken cancellationToken)
        {
            return _runtime.SetStarMetAsync(sessionId, starId, isMet, cancellationToken);
        }

        public Task<StarState> SetStarWorldStateAsync(StarWorldStateUpdateRequest request, CancellationToken cancellationToken)
        {
            return _runtime.SetStarWorldStateAsync(request, cancellationToken);
        }

        public Task<StarState> ApplyStarEffectorAsync(string sessionId, string starId, StarRelationshipEffector effector, CancellationToken cancellationToken)
        {
            return _runtime.ApplyStarEffectorAsync(sessionId, starId, effector, cancellationToken);
        }

        public Task<StarState> ClearStarEffectorAsync(string sessionId, string starId, string effectorId, CancellationToken cancellationToken)
        {
            return _runtime.ClearStarEffectorAsync(sessionId, starId, effectorId, cancellationToken);
        }

        public Task<StarState> AddStarDialogueEntryAsync(StarDialogueMemoryUpdateRequest request, CancellationToken cancellationToken)
        {
            return _runtime.AddStarDialogueEntryAsync(request, cancellationToken);
        }

        public Task<StarState> SetStarDialogueStateAsync(StarDialogueProgressUpdateRequest request, CancellationToken cancellationToken)
        {
            return _runtime.SetStarDialogueStateAsync(request, cancellationToken);
        }

        public Task<StarState> ClearStarDialogueStateAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            return _runtime.ClearStarDialogueStateAsync(sessionId, starId, cancellationToken);
        }

        public Task<SixStarsSaveData> ExportSaveDataAsync(string sessionId, CancellationToken cancellationToken)
        {
            return _runtime.ExportSaveDataAsync(sessionId, cancellationToken);
        }

        public Task ImportSaveDataAsync(SixStarsSaveData saveData, CancellationToken cancellationToken)
        {
            return _runtime.ImportSaveDataAsync(saveData, cancellationToken);
        }


        public Task<StarState> ResetStarMemoryAsync(string sessionId, string starId, CancellationToken cancellationToken)
        {
            return _runtime.ResetStarMemoryAsync(sessionId, starId, cancellationToken);
        }


        public bool HasPendingOrRunningBackgroundAnalysis => _runtime.HasPendingOrRunningBackgroundAnalysis;

        // Compatibility hook for generated story/proposal narration. Normal foreground turns
        // auto-queue their protected primary review at complete Gemma response.
        public bool StartBackgroundAnalysisAfterVoiceReady(string backgroundAnalysisId, string reason = "Generated speech path is ready")
        {
            return _runtime.StartBackgroundAnalysisAfterVoiceReady(backgroundAnalysisId, reason);
        }

        public void SuppressBackgroundAnalysisForPlayerActivity(System.TimeSpan quietWindow, string reason = "Player activity")
        {
            _runtime.SuppressBackgroundAnalysisForPlayerActivity(quietWindow, reason);
        }

        public void CancelBackgroundAnalysis(string reason = "Cancel background Star analysis")
        {
            _runtime.CancelBackgroundAnalysis(reason);
        }

        public Task<StarInteractionResponse> SendPlayerTextAsync(
            string sessionId,
            string starId,
            string playerText,
            RequestType requestType,
            GameContext gameContext,
            StoryOptions storyOptions,
            CancellationToken cancellationToken)
        {
            StarInteractionRequest request = new StarInteractionRequest
            {
                SessionId = sessionId,
                StarId = starId,
                Speaker = "LastCat",
                PlayerText = playerText,
                RequestType = requestType,
                GameContext = gameContext,
                StoryOptions = storyOptions
            };

            request.GameContext.Flags["inputMode"] = "text";
            return _runtime.ProcessAsync(request, cancellationToken);
        }

        public Task<StarInteractionResponse> SendSpeechTranscriptAsync(
            string sessionId,
            string starId,
            SpeechTranscript transcript,
            RequestType requestType,
            GameContext gameContext,
            StoryOptions storyOptions,
            CancellationToken cancellationToken)
        {
            StarInteractionRequest request = new StarInteractionRequest
            {
                SessionId = sessionId,
                StarId = string.IsNullOrWhiteSpace(transcript.SuggestedStarId) ? starId : transcript.SuggestedStarId!,
                Speaker = "LastCat",
                PlayerText = transcript.Text,
                RequestType = requestType,
                GameContext = gameContext,
                StoryOptions = storyOptions
            };

            request.GameContext.Flags["inputMode"] = "voice";
            request.GameContext.Flags["wakeWordDetected"] = transcript.WakeWordDetected ? "true" : "false";
            request.GameContext.Flags["speechConfidence"] = transcript.Confidence.ToString("0.00");
            request.GameContext.Flags["speechListenMode"] = transcript.ListenMode;
            request.GameContext.Flags["audioHotwordConfidence"] = transcript.HotwordConfidence.ToString("0.00");
            return _runtime.ProcessAsync(request, cancellationToken);
        }
    }
}
