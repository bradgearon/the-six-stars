using System;
using System.Collections.Generic;
using System.Linq;

namespace Wds.SixStars.Api.Models
{
    // Durable sufficient statistics survive pruning of the detailed memory histories.
    public sealed class BoyfriendGradeLedger
    {
        public DateTimeOffset? HistoryFrom { get; set; }
        public List<BoyfriendGradeBucket> Totals { get; set; } = new List<BoyfriendGradeBucket>();
        public List<BoyfriendGradeBucket> Days { get; set; } = new List<BoyfriendGradeBucket>();
        public List<string> RecentEvidenceIds { get; set; } = new List<string>();
    }
    public sealed class BoyfriendGradeBucket
    {
        public int Category { get; set; }
        public string Day { get; set; } = "";
        public double Earned { get; set; }
        public double Weight { get; set; }
        public int EvidenceCount { get; set; }
        public string Memory { get; set; } = "";
        public DateTimeOffset LastAt { get; set; }
    }
    public sealed class BoyfriendCategoryGrade
    {
        public string Category { get; set; } = "";
        public string Grade { get; set; } = "Not yet rated";
        public string WeekGrade { get; set; } = "Not yet rated";
        public double? Score { get; set; }
        public double? WeekScore { get; set; }
        public int EvidenceCount { get; set; }
        public string Memory { get; set; } = "";
        public DateTimeOffset? MemoryAt { get; set; }
        public string CurrentPriority { get; set; } = "";
    }
    public sealed class BoyfriendGradeCard
    {
        public DateTimeOffset WeekStartsAt { get; set; }
        public DateTimeOffset? HistoryFrom { get; set; }
        public string Note { get; set; } = "Based on remembered interactions. This week starts Monday (UTC). Unspoken needs and time away do not lose points.";
        public List<BoyfriendCategoryGrade> Categories { get; set; } = new List<BoyfriendCategoryGrade>();
    }
    public static class BoyfriendGrades
    {
        public static readonly string[] Categories = { "Affection", "Comfort & reassurance", "Listening & attention", "Reliability", "Romance", "Play & shared time", "Respect & space", "Care & support" };
        private static readonly string[][] Keywords = {
            new[] { "holdinghands", "hug", "cuddl", "touch", "pett", "strok", "purring", "lapholding", "affection" },
            new[] { "comfort", "reassur", "safe", "sooth", "lonel", "anxious" },
            new[] { "listen", "attention", "understand", "heard", "conversation", "checkin" },
            new[] { "reliab", "promise", "followthrough", "consisten", "depend", "trust" },
            new[] { "kiss", "romanc", "devotion", "date", "sext", "passion", "desire" },
            new[] { "boop", "play", "fun", "laugh", "together", "shared", "qualitytime", "adventure" },
            new[] { "respect", "space", "boundar", "consent", "autonomy", "independen" },
            new[] { "care", "support", "help", "encourag", "rest", "practical" }
        };
        private static string Key(string value) => new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        public static int CategoryFor(GirlfriendVectorItem item)
        {
            string[] ids = { "affection", "comfort", "attention", "reliability", "romance", "play", "respect", "support" };
            int exact = Array.IndexOf(ids, Key(item.ItemId));
            if (exact >= 0) return exact;
            string text = Key(item.ItemId) + Key(item.DisplayName);
            // Specific needs win over incidental activity words; never infer meaning from a prose reason.
            foreach (int i in new[] { 6, 3, 1, 2, 7, 5, 4, 0 })
                if (Keywords[i].Any(text.Contains)) return i;
            return -1; // Unknown free-form needs remain visible in existing state; do not invent a grade.
        }
        public static string Grade(double score) => score >= 95 ? "S" : score >= 85 ? "A" : score >= 75 ? "B" : score >= 65 ? "C" : score >= 50 ? "D" : "F";
        private static double Clamp(double value, double max) => double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Max(0, Math.Min(max, value));

        public static BoyfriendGradeLedger ImportHistory(StarState state)
        {
            var ledger = new BoyfriendGradeLedger();
            foreach (var e in state.GirlfriendNeedsMetHistory.OrderBy(x => x.CreatedAt))
                Record(ledger, state, "need:" + e.Id, e.CreatedAt, e.MetNeedVector, e.MetWantVector, e.Summary);
            return ledger;
        }
        public static void Record(BoyfriendGradeLedger ledger, StarState state, string id, DateTimeOffset at,
            IEnumerable<GirlfriendVectorItem> needs, IEnumerable<GirlfriendVectorItem> wants,
            string summary, IEnumerable<GirlfriendVectorItem>? assessment = null)
        {
            if (!state.IsGirlfriend || (state.GirlfriendSince.HasValue && at < state.GirlfriendSince.Value) || ledger.RecentEvidenceIds.Contains(id)) return;
            var desired = state.GirlfriendDesiredStateHistory.Where(x => x.CreatedAt <= at).OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            var evidence = new List<(GirlfriendVectorItem Item, double Weight)>();
            void Add(IEnumerable<GirlfriendVectorItem> items, IEnumerable<GirlfriendVectorItem> priorities, double factor)
            {
                foreach (var item in items)
                {
                    double confidence = Clamp(item.Confidence, 1);
                    if (confidence < 0.5 || CategoryFor(item) < 0) continue;
                    var priority = priorities.FirstOrDefault(x => Key(x.ItemId) == Key(item.ItemId));
                    double importance = priority == null ? 0 : Clamp(priority.Score, 100) / 100 * Clamp(priority.Confidence, 1);
                    evidence.Add((item, confidence * factor * (1 + importance)));
                }
            }
            var assessed = (assessment ?? Array.Empty<GirlfriendVectorItem>()).Where(x => CategoryFor(x) >= 0 && x.Confidence >= .5 && !string.IsNullOrWhiteSpace(x.Reason)).ToList();
            if (assessed.Count > 0)
            {
                foreach (var item in assessed)
                {
                    int category = CategoryFor(item);
                    double needPriority = (desired?.NeedVector ?? new List<GirlfriendVectorItem>()).Where(x => CategoryFor(x) == category)
                        .Select(x => Clamp(x.Score, 100) / 100 * Clamp(x.Confidence, 1)).DefaultIfEmpty(0).Max();
                    double wantPriority = (desired?.WantVector ?? new List<GirlfriendVectorItem>()).Where(x => CategoryFor(x) == category)
                        .Select(x => Clamp(x.Score, 100) / 100 * Clamp(x.Confidence, 1)).DefaultIfEmpty(0).Max();
                    evidence.Add((item, Clamp(item.Confidence, 1) * (1 + 1.5 * needPriority + wantPriority)));
                }
            }
            else
            {
                // Legacy history records positive fulfillment strength, not a performance verdict.
                // Do not turn a small welcome gesture into a failing grade.
                GirlfriendVectorItem Positive(GirlfriendVectorItem x) => new GirlfriendVectorItem {
                    ItemId = x.ItemId, DisplayName = x.DisplayName, Confidence = x.Confidence,
                    Score = 70 + .30 * Clamp(x.Score, 100), Reason = x.Reason };
                Add(needs.Select(Positive), desired?.NeedVector ?? new List<GirlfriendVectorItem>(), 1.5);
                Add(wants.Select(Positive), desired?.WantVector ?? new List<GirlfriendVectorItem>(), 1.0);
                // Activities alone can describe HER actions. Only fulfilled needs/wants prove his contribution.
            }
            if (evidence.Count == 0) return;
            ledger.RecentEvidenceIds.Add(id);
            if (ledger.RecentEvidenceIds.Count > 2048) ledger.RecentEvidenceIds.RemoveAt(0);
            foreach (var group in evidence.GroupBy(x => CategoryFor(x.Item)))
            {
                // One observation per category per turn; listing several synonyms cannot multiply credit.
                double weight = group.Max(x => x.Weight);
                double score = group.Sum(x => Clamp(x.Item.Score, 100) * x.Weight) / group.Sum(x => x.Weight);
                foreach (var list in new[] { ledger.Totals, ledger.Days })
                {
                    string day = ReferenceEquals(list, ledger.Days) ? at.UtcDateTime.ToString("yyyy-MM-dd") : "";
                    var b = list.FirstOrDefault(x => x.Category == group.Key && x.Day == day);
                    if (b == null) { b = new BoyfriendGradeBucket { Category = group.Key, Day = day }; list.Add(b); }
                    b.Earned += score * weight;
                    b.Weight += weight;
                    b.EvidenceCount++;
                    if (at >= b.LastAt)
                    {
                        b.LastAt = at;
                        b.Memory = assessed.Count > 0 ? group.First().Item.Reason : string.IsNullOrWhiteSpace(summary) ? group.First().Item.DisplayName : summary;
                        if (b.Memory.Length > 240) b.Memory = b.Memory.Substring(0, 240);
                    }
                }
                if (!ledger.HistoryFrom.HasValue || at < ledger.HistoryFrom.Value) ledger.HistoryFrom = at;
            }
            // Retain daily buckets for 90 days, without losing cumulative evidence.
            string cutoff = ledger.Totals.Select(x => x.LastAt).DefaultIfEmpty(at).Max().AddDays(-90).UtcDateTime.ToString("yyyy-MM-dd");
            ledger.Days.RemoveAll(x => string.CompareOrdinal(x.Day, cutoff) < 0);
        }
        public static BoyfriendGradeCard Build(StarState state, DateTimeOffset now)
        {
            var ledger = state.BoyfriendGradeLedger ?? ImportHistory(state);
            var monday = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-(((int)now.UtcDateTime.DayOfWeek + 6) % 7));
            var card = new BoyfriendGradeCard { WeekStartsAt = monday, HistoryFrom = ledger.HistoryFrom };
            for (int i = 0; i < Categories.Length; i++)
            {
                var total = ledger.Totals.FirstOrDefault(x => x.Category == i);
                var week = ledger.Days.Where(x => x.Category == i && string.CompareOrdinal(x.Day, monday.ToString("yyyy-MM-dd")) >= 0 && x.LastAt <= now).ToList();
                double? score = total?.EvidenceCount >= 3 && total.Weight > 0 ? total.Earned / total.Weight : (double?)null;
                double? weekScore = week.Sum(x => x.EvidenceCount) >= 3 && week.Sum(x => x.Weight) > 0 ? week.Sum(x => x.Earned) / week.Sum(x => x.Weight) : (double?)null;
                var priority = (state.GirlfriendHourlyState?.NeedVector ?? new List<GirlfriendVectorItem>()).Concat(state.GirlfriendHourlyState?.WantVector ?? new List<GirlfriendVectorItem>())
                    .Where(x => CategoryFor(x) == i && x.Confidence >= .5 && x.Score >= 60).OrderByDescending(x => x.Score * x.Confidence).FirstOrDefault();
                card.Categories.Add(new BoyfriendCategoryGrade {
                    Category = Categories[i], Score = score.HasValue ? Math.Round(score.Value, 1) : (double?)null,
                    WeekScore = weekScore.HasValue ? Math.Round(weekScore.Value, 1) : (double?)null,
                    Grade = score.HasValue ? Grade(score.Value) : "Not yet rated", WeekGrade = weekScore.HasValue ? Grade(weekScore.Value) : "Not yet rated",
                    EvidenceCount = total?.EvidenceCount ?? 0, Memory = total?.Memory ?? "Three remembered observations unlock a grade.",
                    MemoryAt = total == null ? (DateTimeOffset?)null : total.LastAt,
                    CurrentPriority = priority?.DisplayName ?? "" });
            }
            return card;
        }
    }
}
