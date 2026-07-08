using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Engine
{
    /// <summary>
    /// Runtime-level rolling conversation/emotion trace. This intentionally lives in the API layer
    /// so the WPF harness, Android bridge, and future Unity host all get the same debug artifact.
    /// A new file is created for every settled primary player->Star turn, then interstar ripples
    /// append to that file until the next settled primary turn begins.
    /// </summary>
    public sealed class ConversationDebugLogRecorder
    {
        private readonly object _gate = new object();
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly string _logDirectory;
        private string _latestLogPath = string.Empty;

        public ConversationDebugLogRecorder(string? logDirectory = null)
        {
            _logDirectory = ResolveLogDirectory(logDirectory);
            Directory.CreateDirectory(_logDirectory);
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            _jsonOptions.Converters.Add(new JsonStringEnumConverter());
        }

        public string LogDirectory => _logDirectory;
        public string LatestLogPath
        {
            get { lock (_gate) return _latestLogPath; }
        }

        public string ReadLatestLogText()
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(_latestLogPath) || !File.Exists(_latestLogPath)) return string.Empty;
                return File.ReadAllText(_latestLogPath, Encoding.UTF8);
            }
        }

        public string RecordPrimaryTurn(StarInteractionRequest request, StarInteractionResponse response, StarState stateBefore, StarState stateAfter)
        {
            if (request == null || response == null) return LatestLogPath;
            if (IsInternalMicroRequest(request)) return LatestLogPath;

            string sessionId = string.IsNullOrWhiteSpace(request.SessionId) ? "local-dev" : request.SessionId.Trim();
            string starId = string.IsNullOrWhiteSpace(request.StarId) ? "UnknownStar" : request.StarId.Trim();
            string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
            string safeName = SafeName(stamp + "_" + sessionId + "_" + starId + "_" + ShortSlug(request.PlayerText));
            string path = Path.Combine(_logDirectory, safeName + ".md");

            var payload = new
            {
                kind = "primary-player-turn",
                createdAt = DateTimeOffset.Now,
                sessionId,
                starId,
                requestType = request.RequestType.ToString(),
                playerText = request.PlayerText ?? string.Empty,
                starResponse = response.SpokenText ?? string.Empty,
                spokenParts = response.SpokenParts ?? new List<string>(),
                sourceStateBefore = SnapshotState(stateBefore),
                stateAfter = SnapshotState(stateAfter),
                emotionalEffectVector = response.EmotionalEffectVector ?? string.Empty,
                negativeEmotionVector = response.NegativeEmotionVector ?? string.Empty,
                topAffectedEmotions = response.TopAffectedEmotions ?? new List<TopAffectedEmotion>(),
                acceptedPositiveSignals = response.AcceptedPositiveEmotionSignals ?? new List<string>(),
                acceptedNegativeSignals = response.AcceptedNegativeEmotionSignals ?? new List<string>(),
                acceptedCombos = response.AcceptedRelationshipCombos ?? new List<string>(),
                suggestedDelta = response.SuggestedStateDeltas,
                actualDelta = response.StateDeltas,
                deterministicSummary = response.DeterministicEmotionUpdateSummary ?? string.Empty,
                conversationSummary = response.ConversationSummary ?? string.Empty,
                changesSummary = response.ChangesSummary ?? string.Empty,
                importantPlayerQuotes = response.ImportantPlayerQuotes ?? new List<string>(),
                relationshipEvents = response.RelationshipEvents ?? new List<string>(),
                debug = response.Debug
            };

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Six Stars Conversation Emotion Trace");
            sb.AppendLine();
            sb.AppendLine("- Created: " + DateTimeOffset.Now.ToString("o"));
            sb.AppendLine("- Session: " + sessionId);
            sb.AppendLine("- Primary Star: " + starId);
            sb.AppendLine("- RequestType: " + request.RequestType);
            sb.AppendLine();
            sb.AppendLine("## Latest statement");
            sb.AppendLine();
            sb.AppendLine("**To " + starId + ":** " + (request.PlayerText ?? string.Empty));
            sb.AppendLine();
            sb.AppendLine("**" + starId + " replied:** " + (response.SpokenText ?? string.Empty));
            sb.AppendLine();
            sb.AppendLine("## Primary emotional vectors and state");
            sb.AppendLine();
            AppendJsonBlock(sb, payload);
            sb.AppendLine();
            sb.AppendLine("## Interstar ripple log");
            sb.AppendLine();
            sb.AppendLine("_No interstar ripple entries have been appended yet._");

            lock (_gate)
            {
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                _latestLogPath = path;
                DeleteOldLogsUnlocked(80);
                return _latestLogPath;
            }
        }

        public void AppendInterstarRelay(InterstarRelayMessage relay, StarInteractionRequest originalRequest, StarInteractionResponse originResponse, StarState originState, StarState targetState)
        {
            if (relay == null) return;
            var payload = new
            {
                kind = "interstar-relay",
                createdAt = DateTimeOffset.Now,
                sourceStarId = relay.OriginStarId,
                destinationStarId = relay.TargetStarId,
                shouldTell = relay.ShouldTell,
                priority = relay.Priority,
                summary = relay.Summary,
                whatSourceWouldTellDestination = relay.WhatOriginWouldTellTarget,
                reason = relay.Reason,
                cooldownKey = relay.CooldownKey,
                originalPlayerText = originalRequest?.PlayerText ?? string.Empty,
                originResponse = originResponse?.SpokenText ?? string.Empty,
                sourceStateAtRelay = SnapshotState(originState),
                destinationStateBeforeReaction = SnapshotState(targetState)
            };
            AppendSection("Interstar relay " + relay.OriginStarId + " → " + relay.TargetStarId, payload);
        }

        public void AppendInterstarReaction(string sourceStarId, string destinationStarId, InterstarRelayMessage relay, StarInteractionResponse reaction, StarState stateBefore, StarState stateAfter)
        {
            if (reaction == null) return;
            var payload = new
            {
                kind = "interstar-destination-reaction",
                createdAt = DateTimeOffset.Now,
                sourceStarId = sourceStarId ?? string.Empty,
                destinationStarId = destinationStarId ?? reaction.StarId ?? string.Empty,
                relaySummary = relay?.Summary ?? string.Empty,
                relayText = relay?.WhatOriginWouldTellTarget ?? string.Empty,
                replyToSourceStar = reaction.InterstarReplyToSourceStar ?? string.Empty,
                destinationStateBefore = SnapshotState(stateBefore),
                destinationStateAfter = SnapshotState(stateAfter),
                emotionalEffectVector = reaction.EmotionalEffectVector ?? string.Empty,
                negativeEmotionVector = reaction.NegativeEmotionVector ?? string.Empty,
                sourceStarEmotionalEffectVector = reaction.SourceStarEmotionalEffectVector ?? string.Empty,
                sourceStarNegativeEmotionVector = reaction.SourceStarNegativeEmotionVector ?? string.Empty,
                interstarEmotionVectors = reaction.InterstarEmotionVectors ?? new List<InterstarEmotionVectorResult>(),
                interstarRelationshipDeltas = reaction.InterstarRelationshipDeltas ?? new List<InterstarRelationshipDeltaResult>(),
                topAffectedEmotions = reaction.TopAffectedEmotions ?? new List<TopAffectedEmotion>(),
                acceptedPositiveSignals = reaction.AcceptedPositiveEmotionSignals ?? new List<string>(),
                acceptedNegativeSignals = reaction.AcceptedNegativeEmotionSignals ?? new List<string>(),
                acceptedCombos = reaction.AcceptedRelationshipCombos ?? new List<string>(),
                suggestedDelta = reaction.SuggestedStateDeltas,
                actualDelta = reaction.StateDeltas,
                deterministicSummary = reaction.DeterministicEmotionUpdateSummary ?? string.Empty,
                conversationSummary = reaction.ConversationSummary ?? string.Empty,
                changesSummary = reaction.ChangesSummary ?? string.Empty
            };
            AppendSection("Interstar reaction " + sourceStarId + " → " + destinationStarId, payload);
        }

        public void AppendInterstarFeedback(string originStarId, IEnumerable<InterstarReactionSummary> reactions, StarInteractionResponse feedback, StarState stateBefore, StarState stateAfter)
        {
            if (feedback == null) return;
            var payload = new
            {
                kind = "interstar-origin-feedback",
                createdAt = DateTimeOffset.Now,
                originStarId = originStarId ?? feedback.StarId ?? string.Empty,
                reactionSummaries = reactions?.ToList() ?? new List<InterstarReactionSummary>(),
                originStateBefore = SnapshotState(stateBefore),
                originStateAfter = SnapshotState(stateAfter),
                emotionalEffectVector = feedback.EmotionalEffectVector ?? string.Empty,
                negativeEmotionVector = feedback.NegativeEmotionVector ?? string.Empty,
                sourceStarEmotionalEffectVector = feedback.SourceStarEmotionalEffectVector ?? string.Empty,
                sourceStarNegativeEmotionVector = feedback.SourceStarNegativeEmotionVector ?? string.Empty,
                interstarEmotionVectors = feedback.InterstarEmotionVectors ?? new List<InterstarEmotionVectorResult>(),
                interstarRelationshipDeltas = feedback.InterstarRelationshipDeltas ?? new List<InterstarRelationshipDeltaResult>(),
                topAffectedEmotions = feedback.TopAffectedEmotions ?? new List<TopAffectedEmotion>(),
                acceptedPositiveSignals = feedback.AcceptedPositiveEmotionSignals ?? new List<string>(),
                acceptedNegativeSignals = feedback.AcceptedNegativeEmotionSignals ?? new List<string>(),
                acceptedCombos = feedback.AcceptedRelationshipCombos ?? new List<string>(),
                suggestedDelta = feedback.SuggestedStateDeltas,
                actualDelta = feedback.StateDeltas,
                deterministicSummary = feedback.DeterministicEmotionUpdateSummary ?? string.Empty,
                conversationSummary = feedback.ConversationSummary ?? string.Empty,
                changesSummary = feedback.ChangesSummary ?? string.Empty
            };
            AppendSection("Interstar feedback back to " + originStarId, payload);
        }

        private void AppendSection(string heading, object payload)
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(_latestLogPath) || !File.Exists(_latestLogPath))
                {
                    string path = Path.Combine(_logDirectory, SafeName(DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff") + "_interstar_orphan") + ".md");
                    File.WriteAllText(path, "# Six Stars Conversation Emotion Trace\n\n_Interstar entry arrived before a primary turn log was available._\n\n", Encoding.UTF8);
                    _latestLogPath = path;
                }

                string text = File.ReadAllText(_latestLogPath, Encoding.UTF8);
                text = text.Replace("_No interstar ripple entries have been appended yet._\r\n", string.Empty)
                           .Replace("_No interstar ripple entries have been appended yet._\n", string.Empty);
                StringBuilder sb = new StringBuilder(text.TrimEnd());
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("### " + heading);
                sb.AppendLine();
                AppendJsonBlock(sb, payload);
                File.WriteAllText(_latestLogPath, sb.ToString(), Encoding.UTF8);
            }
        }

        private static object SnapshotState(StarState? state)
        {
            if (state == null) return new { missing = true };
            return new
            {
                starId = state.StarId,
                isMet = state.IsMet,
                isAlly = state.IsAlly,
                isGirlfriend = state.IsGirlfriend,
                isGirlfriendReady = state.IsGirlfriendReady,
                girlfriendReadinessSummary = state.GirlfriendReadinessSummary,
                heart = state.Heart,
                trust = state.Trust,
                romance = state.Romance,
                benaveilInfluence = state.BenaveilInfluence,
                emotionalConnectionPercent = Math.Round(state.EmotionalConnectionPercent, 3),
                lastEmotionalConnectionPercentDelta = Math.Round(state.LastEmotionalConnectionPercentDelta, 3),
                interactionCount = state.InteractionCount,
                lastInteractionAt = state.LastInteractionAt,
                lastEmotionalEffectVector = state.LastEmotionalEffectVector,
                lastNegativeEmotionVector = state.LastNegativeEmotionVector,
                lastDeterministicEmotionSummary = state.LastDeterministicEmotionSummary,
                lastTopAffectedEmotions = state.LastTopAffectedEmotions ?? new List<TopAffectedEmotion>(),
                otherStars = (state.OtherStars ?? new List<InterstarRelationshipState>()).Select(x => new
                {
                    x.StarId,
                    x.StarName,
                    x.IsMet,
                    x.IsAlly,
                    x.IsGirlfriend,
                    x.TrustScore,
                    x.HeartScore,
                    x.LastPositiveEmotionVector,
                    x.LastNegativeEmotionVector,
                    x.LastUpdatedAt,
                    recentHistory = (x.History ?? new List<InterstarRelationshipHistoryEntry>()).Take(5).ToList()
                }).ToList(),
                relationshipScoreAccumulators = state.RelationshipScoreAccumulators ?? new List<StarRelationshipScoreAccumulator>(),
                positiveEmotionTotals = state.PositiveEmotionTotals ?? new List<StarEmotionAccumulatedScore>(),
                negativeEmotionTotals = state.NegativeEmotionTotals ?? new List<StarEmotionAccumulatedScore>(),
                relationshipEvents = (state.RelationshipEvents ?? new List<string>()).Take(30).ToList()
            };
        }

        private static bool IsInternalMicroRequest(StarInteractionRequest request)
        {
            if (request?.GameContext?.Flags == null) return false;
            return request.GameContext.Flags.TryGetValue("interstarMicroUpdate", out string? micro) && string.Equals(micro, "true", StringComparison.OrdinalIgnoreCase);
        }

        private void DeleteOldLogsUnlocked(int keep)
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(_logDirectory);
                foreach (FileInfo file in dir.GetFiles("*.md").OrderByDescending(x => x.LastWriteTimeUtc).Skip(Math.Max(10, keep)))
                {
                    try { file.Delete(); } catch { }
                }
            }
            catch { }
        }

        private void AppendJsonBlock(StringBuilder sb, object payload)
        {
            sb.AppendLine("```json");
            sb.AppendLine(JsonSerializer.Serialize(payload, _jsonOptions));
            sb.AppendLine("```");
        }

        private static string ResolveLogDirectory(string? configured)
        {
            string root = string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "logs", "conversation-emotion") : configured.Trim();
            return Path.GetFullPath(root);
        }

        private static string ShortSlug(string? text)
        {
            string t = string.IsNullOrWhiteSpace(text) ? "turn" : text.Trim().ToLowerInvariant();
            t = new string(t.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
            while (t.Contains("--")) t = t.Replace("--", "-");
            t = t.Trim('-');
            if (t.Length == 0) t = "turn";
            if (t.Length > 50) t = t.Substring(0, 50).Trim('-');
            return t;
        }

        private static string SafeName(string value)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "conversation" : value.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
            return text.Replace(' ', '_');
        }
    }
}
