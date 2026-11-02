using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// Takes items off one sheet's inventory list and puts them on another's, in the stored form
/// (<c>"item-id|qty:2,equipped:1"</c>, see <see cref="InventoryRules"/>). Pure, so the repository can apply both halves to two
/// documents in one save and the rules are unit-tested without a database.
/// </summary>
public static class InventoryMoves
{
    /// <summary>How many of <paramref name="itemId"/> the list holds, over every line that names it.</summary>
    public static int Count(IReadOnlyList<string> raw, InventoryRules rules, string itemId) =>
        raw.Select(SheetRuleEngine.ParsePick).Where(p => SameItem(p.Id, itemId)).Sum(p => QuantityOf(p.Choices, rules));

    /// <summary>
    /// Takes <paramref name="quantity"/> of <paramref name="itemId"/> off <paramref name="raw"/>: loose ones first, so handing over
    /// a spare dagger leaves the one in hand. A line that runs out is removed. False, with the list unchanged, when it holds fewer.
    /// </summary>
    public static bool Take(List<string> raw, InventoryRules rules, string itemId, int quantity)
    {
        if (quantity < 1 || Count(raw, rules, itemId) < quantity) return false;

        var lines = raw.Select((line, index) => (Pick: SheetRuleEngine.ParsePick(line), Index: index))
            .Where(l => SameItem(l.Pick.Id, itemId))
            .OrderBy(l => l.Pick.Choices.GetValueOrDefault(rules.EquippedChoice) > 0)
            .ToList();
        var left = quantity;
        var emptied = new List<int>();
        foreach (var (pick, index) in lines)
        {
            if (left == 0) break;
            var have = QuantityOf(pick.Choices, rules);
            var taken = Math.Min(have, left);
            left -= taken;
            if (taken == have) emptied.Add(index);
            else raw[index] = SheetRuleEngine.FormatItem(rules, pick.Id, have - taken, pick.Choices.GetValueOrDefault(rules.EquippedChoice) > 0);
        }

        foreach (var index in emptied.OrderByDescending(i => i)) raw.RemoveAt(index);
        return true;
    }

    /// <summary>Puts <paramref name="quantity"/> of <paramref name="itemId"/> on <paramref name="raw"/>, onto a loose line of the same
    /// item when there is one (a given item is never worn until its new owner puts it on).</summary>
    public static void Add(List<string> raw, InventoryRules rules, string itemId, int quantity)
    {
        if (quantity < 1) return;
        for (var index = 0; index < raw.Count; index++)
        {
            var (id, choices) = SheetRuleEngine.ParsePick(raw[index]);
            if (!SameItem(id, itemId) || choices.GetValueOrDefault(rules.EquippedChoice) > 0) continue;
            raw[index] = SheetRuleEngine.FormatItem(rules, id, QuantityOf(choices, rules) + quantity, false);
            return;
        }

        raw.Add(SheetRuleEngine.FormatItem(rules, itemId, quantity, false));
    }

    // Item ids are matched as the inventory panel matches them.
    private static bool SameItem(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    // A line with no quantity holds one, as SheetRuleEngine reads it.
    private static int QuantityOf(Dictionary<string, int> choices, InventoryRules rules) =>
        choices.TryGetValue(rules.QuantityChoice, out var q) ? Math.Max(q, 1) : 1;
}
