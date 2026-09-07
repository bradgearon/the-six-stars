using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wds.SixStars.Api.Forge.Contracts;
using Wds.SixStars.Api.Forge.State;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Forge.Context
{
    /// <summary>
    /// Compatibility projection from the current Six Stars profile/state models into Forge context cards.
    /// This is intentionally side-effect free. State persistence remains in StarMemoryApi during the first migration pass.
    /// </summary>
    public sealed class SixStarsForgeContextCardFactory : IForgeContextCardFactory
    {
        public ForgeContextBundle CreateInteractionReviewBundle(
            StarProfile profile,
            StarInteractionRequest request,
            StarState state,
            StarInteractionResponse foregroundResponse,
            int recentConversationCount = 6)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (request == null) throw new ArgumentNullException(nameof(request));
            state = state ?? new StarState { StarId = profile.StarId };
            foregroundResponse = foregroundResponse ?? new StarInteractionResponse { StarId = profile.StarId };

            ForgeActorStateSnapshot snapshot = ForgeActorStateProjector.FromStarState(profile, state, Math.Max(1, recentConversationCount));
            ForgeActorReference actor = snapshot.Actor;
            ForgeContextBundle bundle = new ForgeContextBundle { Actor = actor };
            bundle.Character = BuildCharacterCard(profile, actor);
            bundle.Relationship = BuildRelationshipCard(state, snapshot, actor);
            bundle.Emotion = BuildEmotionCard(snapshot, actor);
            bundle.Memory = BuildMemoryCard(snapshot, actor);
            bundle.Conversation = BuildConversationCard(profile, snapshot, actor);
            bundle.Setting = BuildSettingCard(request, foregroundResponse, actor);
            return bundle;
        }

        private static ForgeContextCard BuildCharacterCard(StarProfile profile, ForgeActorReference actor)
        {
            ForgeContextCard card = NewCard("character", "CHARACTER CARD", actor);
            Add(card, "Name", profile.DisplayName);
            Add(card, "ActorType", string.IsNullOrWhiteSpace(profile.ActorType) ? "star" : profile.ActorType);
            Add(card, "Element", profile.Element);
            Add(card, "Domain", profile.Domain);
            Add(card, "CoreQuote", profile.CoreQuote);
            Add(card, "Personality", profile.PersonalityShort);
            Add(card, "SpeakingStyle", profile.SpeakingStyle);
            AddList(card, "Behaviors", (profile.Behaviors ?? new List<string>()).Take(8));
            AddList(card, "Spoken response rules", (profile.SpokenResponseRules ?? new List<string>()).Take(8));
            return card;
        }

        private static ForgeContextCard BuildRelationshipCard(StarState state, ForgeActorStateSnapshot snapshot, ForgeActorReference actor)
        {
            ForgeContextCard card = NewCard("relationship", string.Empty, actor);
            Add(card, "Relationship", BuildRelationshipStatus(state));
            Add(card, "Flags", "Met=" + state.IsMet + "; Ally=" + state.IsAlly + "; IsGirlfriend=" + state.IsGirlfriend
                + "; GirlfriendSince=" + (state.GirlfriendSince.HasValue ? state.GirlfriendSince.Value.ToString("o") : "none")
                + "; IsGirlfriendReady=" + state.IsGirlfriendReady + ".");
            Add(card, "Current EmotionalConnectionPercent", state.EmotionalConnectionPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%");
            Add(card, "Girlfriend readiness", (state.IsGirlfriendReady ? "ready" : "not ready") + "; " + (state.GirlfriendReadinessSummary ?? string.Empty));

            StarGirlfriendHourlyStateContext hourly = state.GirlfriendHourlyState ?? new StarGirlfriendHourlyStateContext();
            if (state.IsGirlfriend && hourly.IsAvailable)
            {
                Add(card, "Girlfriend hourly state guidance", "This is her current girlfriend emotional and behavioral state. needVector and wantVector may describe what she wants from him, what she wants to say, or what she wants/needs to do herself or together. Use strong fitting items to score this turn; never read scores or vector syntax aloud.");
                Add(card, "Girlfriend hourly state", "GeneratedAt=" + (hourly.GeneratedAt.HasValue ? hourly.GeneratedAt.Value.ToString("o") : "unknown")
                    + "; slot=" + (hourly.SlotId ?? string.Empty)
                    + "; summary=" + Truncate(hourly.Summary, 280));
                Add(card, "Girlfriend posture", Truncate(hourly.RelationshipPosture, 240));
                Add(card, "Need theme", Truncate(hourly.NeedSummary, 180));
                Add(card, "Want theme", Truncate(hourly.WantSummary, 180));
                Add(card, "Girlfriend cute mode", "enabled=" + state.GirlfriendCuteModeEnabled.ToString().ToLowerInvariant()
                    + "; active=" + (state.GirlfriendCuteModeEnabled && hourly.CuteModeActive).ToString().ToLowerInvariant()
                    + "; probability=" + (state.GirlfriendCuteModeEnabled ? (hourly.CuteModeProbability <= 0 ? 0.35 : hourly.CuteModeProbability) : 0).ToString("0.00", CultureInfo.InvariantCulture)
                    + "; signature=" + Truncate(hourly.CuteModeSignature, 140));
                Add(card, "Need vector", FormatGirlfriendVector(hourly.NeedVector, 6));
                Add(card, "Want vector", FormatGirlfriendVector(hourly.WantVector, 6));
            }

            StarDialogueProgressState dialogue = snapshot.Memory.CurrentDialogue ?? new StarDialogueProgressState();
            if (!string.IsNullOrWhiteSpace(dialogue.DialogId) || !string.IsNullOrWhiteSpace(dialogue.SummarySoFar)
                || !string.IsNullOrWhiteSpace(dialogue.FullScriptText) || !string.IsNullOrWhiteSpace(dialogue.FullScriptJson))
            {
                Add(card, "Current authored dialogue", "DialogId=" + dialogue.DialogId + "; title=" + dialogue.Title + "; scene=" + dialogue.SceneId
                    + "; area=" + dialogue.Area + "; quest=" + dialogue.QuestId + "; node=" + dialogue.CurrentNodeId
                    + "; lineIndex=" + dialogue.CurrentLineIndex + "; stage=" + dialogue.CurrentStage + "; active=" + dialogue.IsActive + "; complete=" + dialogue.IsComplete + ".");
                Add(card, "Dialogue summary", Truncate(dialogue.SummarySoFar, 1000));
                Add(card, "Last authored line", Truncate(dialogue.LastAuthoredLine, 500));
                Add(card, "Next expected beat", Truncate(dialogue.NextExpectedBeat, 500));
                // Background turn review only needs the current authored beat. The foreground speech
                // contract receives the richer dialogue context; repeating full script text/JSON here
                // lengthened a background model call that can otherwise block the next foreground turn.
            }
            AddList(card, "Recent authored dialogue", snapshot.Memory.RecentDialogueHistory.Select(x =>
                "id=" + x.Id + " | dialog=" + x.DialogId + " | node=" + x.NodeId + " | title=" + Truncate(x.Title, 120)
                + " | speaker=" + x.Speaker + " | complete=" + x.IsComplete
                + (string.IsNullOrWhiteSpace(x.Text) ? string.Empty : " | line=" + Truncate(x.Text, 400))
                + (string.IsNullOrWhiteSpace(x.Summary) ? string.Empty : " | summary=" + Truncate(x.Summary, 500))
                + (string.IsNullOrWhiteSpace(x.Area) && string.IsNullOrWhiteSpace(x.QuestId) ? string.Empty : " | context=area:" + x.Area + "; quest:" + x.QuestId + "; scene:" + x.SceneId)));
            return card;
        }

        private static ForgeContextCard BuildEmotionCard(ForgeActorStateSnapshot snapshot, ForgeActorReference actor)
        {
            ForgeContextCard card = NewCard("emotion", "EMOTION STATE", actor);
            Add(card, "Last positive vector", snapshot.Emotion.PositiveVector);
            Add(card, "Last negative vector", snapshot.Emotion.NegativeVector);
            Add(card, "Last deterministic summary", snapshot.Emotion.DeterministicSummary);
            AddList(card, "Top affected emotions", snapshot.Emotion.TopEffects);
            return card;
        }

        private static ForgeContextCard BuildMemoryCard(ForgeActorStateSnapshot snapshot, ForgeActorReference actor)
        {
            ForgeContextCard card = NewCard("memory", string.Empty, actor);
            Add(card, "Existing conversation summary", string.IsNullOrWhiteSpace(snapshot.Memory.ConversationSummary) ? "none yet" : Truncate(snapshot.Memory.ConversationSummary, 900));
            AddList(card, "Things learned or suggested by the Last Cat", snapshot.Memory.LearnedItems.Take(12).Select(x => Truncate(x, 500)));
            AddList(card, "Important remembered player quotes", snapshot.Quotes.ImportantPlayerQuotes.Take(6));
            AddList(card, "Memory notes", snapshot.Memory.MemoryNotes.Take(6).Select(x => Truncate(x, 500)));
            AddList(card, "Remembered markers", snapshot.Memory.RememberedMarkers.Take(8));
            AddList(card, "Relationship events", snapshot.Memory.RelationshipEvents.Take(6).Select(x => Truncate(x, 500)));
            AddList(card, "Stories already told by this Star", snapshot.Memory.StoryHistory.Take(10).Select(x => Truncate(x, 260)));
            return card;
        }

        private static ForgeContextCard BuildConversationCard(StarProfile profile, ForgeActorStateSnapshot snapshot, ForgeActorReference actor)
        {
            ForgeContextCard card = NewCard(
                "conversation",
                "RECENT CONVERSATION HISTORY FROM MEMORY LOG / SAVED STATE — newest first. Use this for continuity, follow-ups, pronouns, tone, and relationship pacing.",
                actor);
            int index = 1;
            foreach (ConversationLogEntry entry in snapshot.Memory.RecentConversation)
            {
                string line = "Turn " + index + ": requestType=" + entry.RequestType
                    + "; player=\"" + EscapeInline(Truncate(entry.PlayerText, 220)) + "\"; "
                    + profile.DisplayName + "=\"" + EscapeInline(Truncate(entry.StarResponse, 240)) + "\"";
                AddList(card, "Turns", new[] { line });
                index++;
            }
            if (index == 1)
            {
                Add(card, "History", "No usable prior spoken turns are recorded for this Star yet. Technical parser-fallback turns are intentionally omitted from roleplay memory.");
                if (!string.IsNullOrWhiteSpace(snapshot.Memory.ConversationSummary))
                {
                    Add(card, "Fallback saved conversation summary", Truncate(snapshot.Memory.ConversationSummary, 500));
                }
            }
            return card;
        }

        private static ForgeContextCard BuildSettingCard(StarInteractionRequest request, StarInteractionResponse foregroundResponse, ForgeActorReference actor)
        {
            ForgeContextCard card = NewCard("setting", string.Empty, actor);
            if (!string.IsNullOrWhiteSpace(foregroundResponse.StoryId) || request.RequestType == RequestType.Story)
            {
                card.Title = "Foreground story fields to preserve/update:";
                Add(card, "StoryId", foregroundResponse.StoryId ?? string.Empty);
                Add(card, "StoryTitle", foregroundResponse.StoryTitle ?? string.Empty);
                Add(card, "StorySummary", Truncate(foregroundResponse.StorySummary, 700));
                Add(card, "StoryIsFinished", foregroundResponse.StoryIsFinished.ToString());
                Add(card, "StoryPartIndex", foregroundResponse.StoryPartIndex.ToString(CultureInfo.InvariantCulture));
            }
            return card;
        }

        private static ForgeContextCard NewCard(string type, string title, ForgeActorReference actor)
        {
            return new ForgeContextCard { CardType = type, Title = title, Actor = actor };
        }

        private static void Add(ForgeContextCard card, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            card.Fields.Add(new ForgeContextField { Name = name, Value = value! });
        }

        private static void AddList(ForgeContextCard card, string name, IEnumerable<string>? values)
        {
            List<string> materialized = (values ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (materialized.Count == 0) return;
            if (!card.Lists.TryGetValue(name, out List<string>? existing))
            {
                existing = new List<string>();
                card.Lists[name] = existing;
            }
            existing.AddRange(materialized);
        }

        private static string BuildRelationshipStatus(StarState state)
        {
            string stage;
            if (state.IsGirlfriend) stage = "girlfriend / committed romantic bond";
            else if (state.IsGirlfriendReady) stage = "girlfriend-ready connection gate reached";
            else if (state.EmotionalConnectionPercent >= 50) stage = "part-connected; serious emotional connection forming";
            else if (state.Romance >= 70) stage = "strong romantic tension and deep affection";
            else if (state.Romance >= 35) stage = "warm flirtation and growing romantic possibility";
            else if (state.Trust >= 70 || state.Heart >= 70) stage = "trusted close companion";
            else if (state.Trust >= 35 || state.Heart >= 35) stage = "growing trust and friendship";
            else if (state.HurtSuccessionCount > 0) stage = "recently hurt or cautious";
            else stage = "early bond / still learning each other";
            return stage + " (Heart " + state.Heart + ", Trust " + state.Trust + ", Romance " + state.Romance
                + ", EmotionalConnection " + state.EmotionalConnectionPercent.ToString("0.0", CultureInfo.InvariantCulture)
                + "%, GirlfriendReady " + state.IsGirlfriendReady + ", IsAlly " + state.IsAlly + ").";
        }

        private static string FormatGirlfriendVector(IEnumerable<GirlfriendVectorItem>? items, int maxItems)
        {
            List<string> parts = (items ?? Array.Empty<GirlfriendVectorItem>())
                .Where(x => x != null && (!string.IsNullOrWhiteSpace(x.ItemId) || !string.IsNullOrWhiteSpace(x.DisplayName)))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Confidence)
                .Take(Math.Max(1, maxItems))
                .Select(x => CleanId(!string.IsNullOrWhiteSpace(x.ItemId) ? x.ItemId : x.DisplayName)
                    + ":" + Math.Max(0, Math.Min(100, x.Score)).ToString("0.0", CultureInfo.InvariantCulture)
                    + ":" + Math.Max(0, Math.Min(1, x.Confidence)).ToString("0.00", CultureInfo.InvariantCulture))
                .ToList();
            return parts.Count == 0 ? "none" : string.Join(";", parts);
        }

        private static string CleanId(string? value)
        {
            string raw = value ?? string.Empty;
            string cleaned = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrWhiteSpace(cleaned)) return "unknown";
            return char.ToLowerInvariant(cleaned[0]) + (cleaned.Length > 1 ? cleaned.Substring(1) : string.Empty);
        }

        private static string EscapeInline(string? value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        private static bool IsTechnicalMemoryNote(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.IndexOf("structured JSON parsing failed", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Real LLM raw response was salvaged", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("spokenText was missing or empty", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("The JSON value could not be converted", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Truncate(string? value, int max)
        {
            string text = value ?? string.Empty;
            if (text.Length <= max) return text;
            return text.Substring(0, Math.Max(0, max - 1)) + "…";
        }
    }
}
