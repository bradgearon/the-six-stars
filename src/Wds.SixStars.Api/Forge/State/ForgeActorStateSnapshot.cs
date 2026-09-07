using System;
using System.Collections.Generic;
using System.Linq;
using Wds.SixStars.Api.Forge.Contracts;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Forge.State
{
    public sealed class ForgeEmotionStateFacet
    {
        public string PositiveVector { get; set; } = string.Empty;
        public string NegativeVector { get; set; } = string.Empty;
        public string DeterministicSummary { get; set; } = string.Empty;
        public List<string> TopEffects { get; set; } = new List<string>();
    }

    public sealed class ForgeRelationshipStateFacet
    {
        public int Heart { get; set; }
        public int Trust { get; set; }
        public int Romance { get; set; }
        public double EmotionalConnectionPercent { get; set; }
        public bool IsMet { get; set; }
        public bool IsAlly { get; set; }
        public bool IsGirlfriend { get; set; }
        public bool IsGirlfriendReady { get; set; }
        public DateTimeOffset? GirlfriendSince { get; set; }
        public string GirlfriendReadinessSummary { get; set; } = string.Empty;
        public List<string> OtherActorRelationships { get; set; } = new List<string>();
    }

    public sealed class ForgeQuoteStateFacet
    {
        public List<string> ImportantPlayerQuotes { get; set; } = new List<string>();
        public int ArchivedQuoteCount { get; set; }
    }

    public sealed class ForgeMemoryStateFacet
    {
        public string ConversationSummary { get; set; } = string.Empty;
        public List<string> MemoryNotes { get; set; } = new List<string>();
        public List<string> LearnedItems { get; set; } = new List<string>();
        public List<string> RelationshipEvents { get; set; } = new List<string>();
        public List<string> RememberedMarkers { get; set; } = new List<string>();
        public List<string> StoryHistory { get; set; } = new List<string>();
        public List<ConversationLogEntry> RecentConversation { get; set; } = new List<ConversationLogEntry>();
        public StarDialogueProgressState CurrentDialogue { get; set; } = new StarDialogueProgressState();
        public List<StarDialogueMemoryEntry> RecentDialogueHistory { get; set; } = new List<StarDialogueMemoryEntry>();
    }

    /// <summary>
    /// Projection of the legacy monolithic StarState into independently movable Forge state facets.
    /// Persistence is intentionally unchanged in this first pass; this snapshot is the compatibility seam.
    /// </summary>
    public sealed class ForgeActorStateSnapshot
    {
        public ForgeActorReference Actor { get; set; } = new ForgeActorReference();
        public long Revision { get; set; }
        public ForgeEmotionStateFacet Emotion { get; set; } = new ForgeEmotionStateFacet();
        public ForgeRelationshipStateFacet Relationship { get; set; } = new ForgeRelationshipStateFacet();
        public ForgeQuoteStateFacet Quotes { get; set; } = new ForgeQuoteStateFacet();
        public ForgeMemoryStateFacet Memory { get; set; } = new ForgeMemoryStateFacet();
    }

    public static class ForgeActorStateProjector
    {
        public static ForgeActorStateSnapshot FromStarState(StarProfile profile, StarState state, int recentConversationCount = 6)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            state = state ?? new StarState { StarId = profile.StarId };

            return new ForgeActorStateSnapshot
            {
                Actor = new ForgeActorReference
                {
                    ForgeId = "six-stars",
                    ActorId = string.IsNullOrWhiteSpace(profile.ActorId) ? profile.StarId : profile.ActorId,
                    ActorType = string.IsNullOrWhiteSpace(profile.ActorType) ? "star" : profile.ActorType
                },
                Revision = Math.Max(0, state.InteractionCount),
                Emotion = new ForgeEmotionStateFacet
                {
                    PositiveVector = state.LastEmotionalEffectVector ?? string.Empty,
                    NegativeVector = state.LastNegativeEmotionVector ?? string.Empty,
                    DeterministicSummary = state.LastDeterministicEmotionSummary ?? string.Empty,
                    TopEffects = (state.LastTopAffectedEmotions ?? new List<TopAffectedEmotion>())
                        .Where(x => x != null)
                        .Take(8)
                        .Select(x => x.ToString())
                        .ToList()
                },
                Relationship = new ForgeRelationshipStateFacet
                {
                    Heart = state.Heart,
                    Trust = state.Trust,
                    Romance = state.Romance,
                    EmotionalConnectionPercent = state.EmotionalConnectionPercent,
                    IsMet = state.IsMet,
                    IsAlly = state.IsAlly,
                    IsGirlfriend = state.IsGirlfriend,
                    IsGirlfriendReady = state.IsGirlfriendReady,
                    GirlfriendSince = state.GirlfriendSince,
                    GirlfriendReadinessSummary = state.GirlfriendReadinessSummary ?? string.Empty,
                    OtherActorRelationships = (state.OtherStars ?? new List<InterstarRelationshipState>())
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.StarId))
                        .Select(x => x.StarId + ":met=" + x.IsMet + ";ally=" + x.IsAlly + ";girlfriend=" + x.IsGirlfriend + ";trust=" + x.TrustScore + ";heart=" + x.HeartScore)
                        .ToList()
                },
                Quotes = new ForgeQuoteStateFacet
                {
                    ImportantPlayerQuotes = (state.ImportantPlayerQuotes ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
                    ArchivedQuoteCount = (state.AllImportantPlayerQuotes ?? new List<StarImportantQuoteRecord>()).Count
                },
                Memory = new ForgeMemoryStateFacet
                {
                    ConversationSummary = state.ConversationSummary ?? string.Empty,
                    MemoryNotes = (state.MemoryNotes ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x) && !IsTechnicalMemoryNote(x)).ToList(),
                    LearnedItems = (state.ThingsLearnedOrSuggestedByPlayer ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
                    RelationshipEvents = (state.RelationshipEvents ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
                    RememberedMarkers = (state.RememberedMarkers ?? new List<StarRememberedMarker>())
                        .Where(x => x != null && x.IsActive)
                        .OrderByDescending(x => x.UpdatedAt)
                        .Take(8)
                        .Select(FormatMarker)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList(),
                    StoryHistory = (state.StoryHistory ?? new List<StarStoryHistoryEntry>())
                        .Where(x => x != null)
                        .Select(x => x.Title + " | finished=" + x.Finished + " | " + (x.Summary ?? string.Empty))
                        .ToList(),
                    RecentConversation = GetUsableRecentConversation(state, recentConversationCount),
                    CurrentDialogue = state.CurrentDialogueState ?? new StarDialogueProgressState(),
                    RecentDialogueHistory = (state.GameDialogueHistory ?? new List<StarDialogueMemoryEntry>())
                        .Where(x => x != null)
                        .OrderByDescending(x => x.CompletedAt ?? x.CreatedAt)
                        .Take(4)
                        .ToList()
                }
            };
        }

        private static string FormatMarker(StarRememberedMarker marker)
        {
            string id = string.IsNullOrWhiteSpace(marker.Id) ? "unlabeled" : marker.Id;
            string name = string.IsNullOrWhiteSpace(marker.MarkerName) ? "marker" : marker.MarkerName;
            string coords = string.IsNullOrWhiteSpace(marker.Coordinates) ? "coordinates not provided" : marker.Coordinates;
            string location = string.IsNullOrWhiteSpace(marker.LocationText) ? marker.Area : marker.LocationText;
            string desc = string.IsNullOrWhiteSpace(marker.Description) ? marker.ReminderText : marker.Description;
            string line = "id=" + id + " | name=" + Truncate(name, 120) + " | coords=" + Truncate(coords, 80)
                + " | location=" + Truncate(location, 160) + " | description=" + Truncate(desc, 240);
            if (!string.IsNullOrWhiteSpace(marker.SourcePlayerQuote)) line += " | source=\"" + Truncate(marker.SourcePlayerQuote, 240).Replace("\"", "\\\"") + "\"";
            return line;
        }

        private static string Truncate(string? value, int max)
        {
            string text = value ?? string.Empty;
            if (text.Length <= max) return text;
            return text.Substring(0, Math.Max(0, max - 1)) + "…";
        }

        private static List<ConversationLogEntry> GetUsableRecentConversation(StarState state, int maxItems)
        {
            List<ConversationLogEntry> history = (state.MemoryLog ?? new List<ConversationLogEntry>())
                .Where(IsUsableConversationTurn)
                .ToList();
            if (history.Count == 0)
            {
                history.AddRange((state.OldConversationArchives ?? new List<StarConversationArchiveEntry>())
                    .Where(x => x != null)
                    .OrderByDescending(x => x.EndedAt)
                    .SelectMany(x => x.Entries ?? new List<ConversationLogEntry>())
                    .Where(IsUsableConversationTurn));
            }
            return history.OrderByDescending(x => x.CreatedAt).Take(Math.Max(1, maxItems)).ToList();
        }

        private static bool IsUsableConversationTurn(ConversationLogEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.PlayerText) || string.IsNullOrWhiteSpace(entry.StarResponse)) return false;
            string text = entry.StarResponse ?? string.Empty;
            return text.IndexOf("thoughts came back in a shape the system could not fully read", StringComparison.OrdinalIgnoreCase) < 0
                && text.IndexOf("answer came back in a strange shape", StringComparison.OrdinalIgnoreCase) < 0
                && !(text.IndexOf("received your words", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("try again", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsTechnicalMemoryNote(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.IndexOf("structured JSON parsing failed", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Real LLM raw response was salvaged", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("spokenText was missing or empty", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("The JSON value could not be converted", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
