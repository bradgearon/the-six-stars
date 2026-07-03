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

        public Task<StarInteractionHistoryPage> GetStarInteractionHistoryAsync(string sessionId, string starId, int skip, int take, CancellationToken cancellationToken)
        {
            return _runtime.GetStarInteractionHistoryAsync(sessionId, starId, skip, take, cancellationToken);
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

        public bool StartBackgroundAnalysisAfterVoiceReady(string backgroundAnalysisId, string reason = "Foreground voice generation completed")
        {
            return _runtime.StartBackgroundAnalysisAfterVoiceReady(backgroundAnalysisId, reason);
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
