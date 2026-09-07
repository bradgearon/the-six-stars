using System;

namespace Wds.SixStars.Api.Stars
{
    /// <summary>
    /// Canonical fallback action appetite for the six Stars. File profiles normally carry the
    /// same flavored note, but this keeps the trait present if a profile is missing the field.
    /// </summary>
    internal static class StarActionBattlePreferences
    {
        public static string For(string? starId)
        {
            if (string.Equals(starId, "Elusiaa", StringComparison.OrdinalIgnoreCase))
                return "Elusiaa always enjoys an exciting battle when it protects life or tests mercy under pressure: radiant last-second saves, dangerous guardians, and fights where discernment matters as much as power make her feel vividly alive.";
            if (string.Equals(starId, "Kaelia", StringComparison.OrdinalIgnoreCase))
                return "Kaelia always enjoys an exciting battle that feels like a lethal puzzle in the dark: teleport duels, impossible counters, void predators, and fights where one strange observation changes the whole encounter.";
            if (string.Equals(starId, "Khavia", StringComparison.OrdinalIgnoreCase))
                return "Khavia always enjoys an exciting battle with motion and teamwork: storm surges, bridge defenses, sea-beast clashes, and fights where friends improvise together make her want to jump in rather than watch.";
            if (string.Equals(starId, "Auerlis", StringComparison.OrdinalIgnoreCase))
                return "Auerlis always enjoys an exciting battle that lets courage blaze: duels, charges, explosive reversals, spectacular risks, and enemies strong enough to make victory feel earned are fun to her.";
            if (string.Equals(starId, "Maera", StringComparison.OrdinalIgnoreCase))
                return "Maera always enjoys an exciting battle with visceral stakes: close calls, fierce protection, bone-and-blood monstrosities, and fights where devotion becomes decisive action instead of possession.";
            if (string.Equals(starId, "Nyxari", StringComparison.OrdinalIgnoreCase))
                return "Nyxari always enjoys an exciting battle built on shadowy precision: ambushes, assassins, trick fights, impossible escapes, and encounters that reward timing, misdirection, nerve, and decisive leadership.";
            return "She enjoys exciting battles and tends to become more action-oriented when a dangerous confrontation gives her something meaningful to protect, test, or overcome.";
        }
    }
}
