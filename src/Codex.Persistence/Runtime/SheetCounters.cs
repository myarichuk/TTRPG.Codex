using Codex.Core;
using Codex.Plugin.Abstractions;

namespace Codex.Persistence.Runtime;

/// <summary>
/// The sheet's counters: values both the sheet and the table move (current hit points, a resource pool, spell slots left).
/// A sheet saved while the table is live merges these by how much the editor moved them, not by what the editor last saw
/// (review D3): opened at 30/30, dropped to 12 at the table, Con raised for +5 maximum on the sheet, the save leaves 17/35,
/// not 35/35; a slot used on the sheet and another at the table leave two used, not one.
/// </summary>
internal static class SheetCounters
{
    /// <summary>Each counter's key, with the key of its maximum (null when it has none).</summary>
    public static IEnumerable<(string Key, string? MaxKey)> Of(SystemDefinition? system, IEnumerable<string> sheetKeys)
    {
        if (system == null)
        {
            // The table's own default pool (TableRules.PrimaryKey).
            yield return ("HP", "HP_Max");
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in system.Resources)
        {
            if (seen.Add(resource.Key)) yield return (resource.Key, resource.MaxKey);
        }

        if (system.SheetRules?.HitPoints is { } hp && seen.Add(hp.CurrentKey))
        {
            yield return (hp.CurrentKey, hp.Key);
        }

        foreach (var pool in system.SheetRules?.SlotPools ?? new List<SlotPoolRule>())
        {
            foreach (var key in sheetKeys.Where(k => pool.Owns(k) && !k.EndsWith("_Max", StringComparison.Ordinal)))
            {
                if (seen.Add(key)) yield return (key, key + "_Max");
            }
        }

        // A feature's uses left, moved on the sheet and at the table alike.
        foreach (var key in sheetKeys.Where(Codex.Core.Rules.SheetRuleEngine.IsUsesKey))
        {
            if (seen.Add(key)) yield return (key, key + Codex.Core.Rules.SheetRuleEngine.UsesMaxSuffix);
        }
    }

    /// <summary>Sets each counter in <paramref name="merged"/> to its <paramref name="live"/> value moved by what the editor
    /// moved it (<paramref name="edited"/> less <paramref name="baseline"/>), within 0 and its maximum in <paramref name="merged"/>.
    /// A counter the editor left alone keeps its live value; one missing from any of the three is left as it is.</summary>
    public static void MergeByDelta(Dictionary<string, object> merged, IReadOnlyDictionary<string, object> edited,
        IReadOnlyDictionary<string, object> baseline, IReadOnlyDictionary<string, object> live, SystemDefinition? system)
    {
        foreach (var (key, maxKey) in Of(system, edited.Keys).ToList())
        {
            if (YamlValues.GetInt(edited, key) is not { } now || YamlValues.GetInt(baseline, key) is not { } was || YamlValues.GetInt(live, key) is not { } atTable)
            {
                continue;
            }

            var next = atTable + (now - was);
            var max = maxKey == null ? null : YamlValues.GetInt(merged, maxKey);

            // A feature's uses the editor moved to full or to none - a refill or an emptying by a changed pick, or the last use spent on
            // the sheet - say where the counter ends, not by how much it moved: the table's value is put aside, not added to.
            if (Codex.Core.Rules.SheetRuleEngine.IsUsesKey(key) && now != was && (now <= 0 || now == max))
            {
                merged[key] = (long)Math.Max(0, now);
                continue;
            }

            merged[key] = (long)Math.Clamp(next, 0, Math.Max(0, max ?? int.MaxValue));
        }
    }
}
