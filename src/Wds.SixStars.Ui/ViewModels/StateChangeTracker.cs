using System;
using System.Collections.Generic;

namespace Wds.SixStars.Ui.ViewModels
{
    public sealed class StateChangeTracker
    {
        private readonly Dictionary<string, StarStateTotals> _starTotals = new Dictionary<string, StarStateTotals>();

        public void RecordStateChange(string starId, int heartDelta, int trustDelta, int romanceDelta, int benaveilDelta)
        {
            if (!_starTotals.ContainsKey(starId))
            {
                _starTotals[starId] = new StarStateTotals { StarId = starId };
            }

            StarStateTotals totals = _starTotals[starId];
            totals.TotalHeartChanges += heartDelta;
            totals.TotalTrustChanges += trustDelta;
            totals.TotalRomanceChanges += romanceDelta;
            totals.TotalBenaveilChanges += benaveilDelta;
            totals.InteractionCount++;
            totals.LastInteractionTime = DateTimeOffset.Now;

            if (heartDelta > 0) totals.PositiveHeartCount++;
            else if (heartDelta < 0) totals.NegativeHeartCount++;

            if (trustDelta > 0) totals.PositiveTrustCount++;
            else if (trustDelta < 0) totals.NegativeTrustCount++;

            if (romanceDelta > 0) totals.PositiveRomanceCount++;
            else if (romanceDelta < 0) totals.NegativeRomanceCount++;
        }

        public void Reset(string starId)
        {
            _starTotals.Remove(starId);
        }

        public StarStateTotals GetTotals(string starId)
        {
            return _starTotals.TryGetValue(starId, out StarStateTotals? totals) 
                ? totals 
                : new StarStateTotals { StarId = starId };
        }

        public Dictionary<string, StarStateTotals> GetAllTotals()
        {
            return new Dictionary<string, StarStateTotals>(_starTotals);
        }

        public string GetSummary(string starId)
        {
            StarStateTotals totals = GetTotals(starId);
            if (totals.InteractionCount == 0)
            {
                return $"No interactions recorded for {starId}.";
            }

            return $"{starId} Session Totals:\n" +
                   $"  Interactions: {totals.InteractionCount}\n" +
                   $"  Heart: {FormatDelta(totals.TotalHeartChanges)} ({totals.PositiveHeartCount}+ / {totals.NegativeHeartCount}-)\n" +
                   $"  Trust: {FormatDelta(totals.TotalTrustChanges)} ({totals.PositiveTrustCount}+ / {totals.NegativeTrustCount}-)\n" +
                   $"  Romance: {FormatDelta(totals.TotalRomanceChanges)} ({totals.PositiveRomanceCount}+ / {totals.NegativeRomanceCount}-)\n" +
                   $"  Benaveil: {FormatDelta(totals.TotalBenaveilChanges)}\n" +
                   $"  Last: {totals.LastInteractionTime:HH:mm:ss}";
        }

        public string GetCombinedSummary()
        {
            if (_starTotals.Count == 0)
            {
                return "No interactions recorded.";
            }

            int totalInteractions = 0;
            int totalHeart = 0;
            int totalTrust = 0;
            int totalRomance = 0;
            int totalBenaveil = 0;

            foreach (var totals in _starTotals.Values)
            {
                totalInteractions += totals.InteractionCount;
                totalHeart += totals.TotalHeartChanges;
                totalTrust += totals.TotalTrustChanges;
                totalRomance += totals.TotalRomanceChanges;
                totalBenaveil += totals.TotalBenaveilChanges;
            }

            string summary = $"=== COMBINED SESSION TOTALS ===\n" +
                           $"Total Interactions: {totalInteractions}\n" +
                           $"Combined Heart: {FormatDelta(totalHeart)}\n" +
                           $"Combined Trust: {FormatDelta(totalTrust)}\n" +
                           $"Combined Romance: {FormatDelta(totalRomance)}\n" +
                           $"Combined Benaveil: {FormatDelta(totalBenaveil)}\n\n" +
                           $"=== PER-STAR BREAKDOWN ===\n";

            foreach (var kvp in _starTotals)
            {
                summary += GetSummary(kvp.Key) + "\n\n";
            }

            return summary.TrimEnd();
        }

        private static string FormatDelta(int value)
        {
            return value >= 0 ? $"+{value}" : value.ToString();
        }
    }

    public sealed class StarStateTotals
    {
        public string StarId { get; set; } = string.Empty;
        public int InteractionCount { get; set; }
        public int TotalHeartChanges { get; set; }
        public int TotalTrustChanges { get; set; }
        public int TotalRomanceChanges { get; set; }
        public int TotalBenaveilChanges { get; set; }
        public int PositiveHeartCount { get; set; }
        public int NegativeHeartCount { get; set; }
        public int PositiveTrustCount { get; set; }
        public int NegativeTrustCount { get; set; }
        public int PositiveRomanceCount { get; set; }
        public int NegativeRomanceCount { get; set; }
        public DateTimeOffset LastInteractionTime { get; set; }
    }
}
