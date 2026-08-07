using System;
using System.Collections.Generic;

namespace Wds.SixStars.Api.Models
{
    public sealed class InterstarRelayMessage
    {
        public string OriginStarId { get; set; } = string.Empty;
        public string TargetStarId { get; set; } = string.Empty;
        public bool ShouldTell { get; set; }
        public double Priority { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string WhatOriginWouldTellTarget { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string CooldownKey { get; set; } = string.Empty;
    }

    public sealed class InterstarReactionSummary
    {
        public string StarId { get; set; } = string.Empty;
        public string SourceStarId { get; set; } = string.Empty;
        public string RelaySummary { get; set; } = string.Empty;
        /// <summary>How the relay changed this Star's hidden feelings toward the Last Cat.</summary>
        public string EmotionalEffectVector { get; set; } = string.Empty;
        public string NegativeEmotionVector { get; set; } = string.Empty;

        /// <summary>How the relay changed this Star's hidden feelings toward the source Star.</summary>
        public string SourceStarEmotionalEffectVector { get; set; } = string.Empty;
        public string SourceStarNegativeEmotionVector { get; set; } = string.Empty;
        public List<InterstarEmotionVectorResult> InterstarEmotionVectors { get; set; } = new List<InterstarEmotionVectorResult>();
        public List<InterstarRelationshipDeltaResult> InterstarRelationshipDeltas { get; set; } = new List<InterstarRelationshipDeltaResult>();

        public RelationshipDelta AppliedDelta { get; set; } = RelationshipDelta.Zero();
        public string TopAffectedEmotionsText { get; set; } = string.Empty;
        /// <summary>One concise in-character sentence the destination Star would send back to the source Star.</summary>
        public string ReplyToSourceStar { get; set; } = string.Empty;
        public string DeterministicSummary { get; set; } = string.Empty;
    }

    public sealed class InterstarUpdateStatusEventArgs : EventArgs
    {
        public string SessionId { get; set; } = string.Empty;
        public string OriginStarId { get; set; } = string.Empty;
        public string TargetStarId { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int QueuedTargets { get; set; }
        public int CompletedTargets { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public UnityEventEnvelope ToUnityEvent()
        {
            return new UnityEventEnvelope
            {
                Type = "InterstarUpdate" + (string.IsNullOrWhiteSpace(Phase) ? "Status" : Phase),
                Target = string.IsNullOrWhiteSpace(TargetStarId) ? OriginStarId : TargetStarId,
                Data =
                {
                    { "sessionId", SessionId ?? string.Empty },
                    { "originStarId", OriginStarId ?? string.Empty },
                    { "targetStarId", TargetStarId ?? string.Empty },
                    { "phase", Phase ?? string.Empty },
                    { "message", Message ?? string.Empty },
                    { "queuedTargets", QueuedTargets.ToString() },
                    { "completedTargets", CompletedTargets.ToString() },
                    { "createdAt", CreatedAt.ToString("o") }
                }
            };
        }
    }

    public sealed class InterstarUpdateCycleResult : EventArgs
    {
        public string SessionId { get; set; } = string.Empty;
        public string OriginStarId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public List<InterstarReactionSummary> Reactions { get; set; } = new List<InterstarReactionSummary>();
        public bool OriginFeedbackApplied { get; set; }
    }
}
