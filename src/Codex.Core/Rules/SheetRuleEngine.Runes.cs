using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// Runes on inventory picks (docs/ITEM_RUNES_DESIGN.md). A rune is an entry of <see cref="InventoryRules.RuneType"/> attached to an item as the pick's
/// choice <c>rune.{id}</c>. While the item is equipped its <c>modifiers</c> (and any <c>modifiers</c> of the item itself) become features of the sheet,
/// so they stack by the system's modifier types; on a weapon the targets <c>attack</c> and <c>damage</c> reach that weapon alone.
/// </summary>
public sealed partial class SheetRuleEngine
{
    /// <summary>The start of a pick's choice that attaches a rune: <c>rune.{id}</c>.</summary>
    public const string RuneChoicePrefix = "rune.";

    /// <summary>The ids of the runes a pick carries, in the order the pick lists them.</summary>
    public static List<string> RuneIds(IReadOnlyDictionary<string, int> choices) =>
        choices.Where(c => c.Value > 0 && c.Key.StartsWith(RuneChoicePrefix, StringComparison.OrdinalIgnoreCase) && c.Key.Length > RuneChoicePrefix.Length)
            .Select(c => c.Key[RuneChoicePrefix.Length..]).ToList();

    /// <summary>The pick with the rune attached (a rune already on it stays once).</summary>
    public static string WithRune(string raw, string runeId)
    {
        var (id, choices) = ParsePick(raw);
        choices[RuneChoicePrefix + runeId] = 1;
        return FormatPick(id, choices);
    }

    /// <summary>The pick as a new quantity or equipped state leaves it: <paramref name="previous"/>'s runes kept.</summary>
    public static string FormatItem(InventoryRules rules, string id, int quantity, bool equipped, string previous)
    {
        var choices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (quantity > 1) choices[rules.QuantityChoice] = quantity;
        if (equipped) choices[rules.EquippedChoice] = 1;
        foreach (var (key, value) in ParsePick(previous).Choices)
        {
            if (key.StartsWith(RuneChoicePrefix, StringComparison.OrdinalIgnoreCase)) choices[key] = value;
        }

        return FormatPick(id, choices);
    }

    private IEnumerable<ContentEntry> RunesOf(InventoryRules rules, IReadOnlyDictionary<string, int> choices) =>
        rules.RuneType == null
            ? Enumerable.Empty<ContentEntry>()
            : RuneIds(choices).Select(id => resolver.Resolve(rules.RuneType, id)).OfType<ContentEntry>();

    /// <summary>Whether an entry is one a weapon action is made with (its damage and attack are its own).</summary>
    private bool IsWeaponEntry(ContentEntry entry) =>
        Rules!.Actions.Any(a => a.Inventory != null && Property(entry, a.Property) != null && (a.Tag == null || entry.Tags.Contains(a.Tag, StringComparer.OrdinalIgnoreCase)));

    /// <summary>The features the equipped items of the inventory give: their own <c>modifiers</c>, and those of each rune they carry.</summary>
    private IEnumerable<ActiveFeature> InventoryFeatures(IReadOnlyDictionary<string, object> sheet, IReadOnlyDictionary<string, object> view)
    {
        if (Rules?.Inventory is not { } rules) yield break;
        foreach (var raw in RawList(sheet, rules.Key))
        {
            var (id, choices) = ParsePick(raw);
            if (!(choices.TryGetValue(rules.EquippedChoice, out var worn) && worn > 0)) continue;
            var item = rules.Types.Select(type => resolver.Resolve(type, id)).FirstOrDefault(e => e != null);
            if (item == null) continue;
            var weapon = IsWeaponEntry(item);
            foreach (var source in new[] { item }.Concat(RunesOf(rules, choices)))
            {
                var modifiers = Maps(source.Properties.GetValueOrDefault("modifiers")).Select(m => RuneTarget(m, weapon ? item.Id : null)).ToList();
                if (modifiers.Count == 0) continue;
                yield return Feature(source.Name, source.Description, source, null, view, null, new Dictionary<string, object> { ["modifiers"] = modifiers });
            }
        }
    }

    /// <summary>A modifier with its <c>attack</c> and <c>damage</c> targets narrowed to the weapon <paramref name="weaponId"/>, when there is one.</summary>
    private static object RuneTarget(IDictionary<string, object> modifier, string? weaponId)
    {
        var copy = new Dictionary<string, object>(modifier);
        if (weaponId != null && Text(modifier, "target") is { } target && (target == RollSiteTargets.Attack || target == RollSiteTargets.Damage))
        {
            copy["target"] = $"{target}.{RollSiteTargets.Entry}.{weaponId}";
        }

        return copy;
    }

    /// <summary>The damage dice of a weapon with the extra copies its equipped runes add (<c>extraDice</c>, the largest among them).</summary>
    private string WeaponDice(SheetEvaluation result, ContentEntry entry, string dice)
    {
        if (Rules?.Inventory is not { } rules || rules.RuneType == null) return dice;
        var extra = result.Inventory.Where(i => i.Equipped && i.Entry.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) && i.Entry.Type == entry.Type)
            .SelectMany(i => RunesOf(rules, ParsePick(i.Raw).Choices)).Select(r => (int)(ToDouble(Property(r, "extraDice")) ?? 0)).DefaultIfEmpty(0).Max();
        if (extra <= 0 || LeadingDice.Match(dice) is not { Success: true } lead) return dice;
        return MoreDice(dice, $"{lead.Groups["count"].Value}d{lead.Groups["sides"].Value}", extra);
    }

    /// <summary>The rune checks of one pick: each rune is in the Library and fits the item, a group (potency, striking, resilience) is held once, and
    /// the runes with no group (the property runes) fit the room the item's runes give (<c>slots</c>, the most among them).</summary>
    private void CheckRunes(InventoryRules rules, ContentEntry item, IReadOnlyDictionary<string, int> choices, SheetEvaluation result)
    {
        if (rules.RuneType == null) return;
        var runes = new List<ContentEntry>();
        foreach (var runeId in RuneIds(choices))
        {
            if (resolver.Resolve(rules.RuneType, runeId) is not { } rune)
            {
                result.Violations.Add(new RuleViolation(rules.Key, $"{rules.Label}: {item.Name} has a rune \"{runeId}\" that is not in the Library.", "Actor.Rule.RuneNotFound", new object?[] { rules.Label, item.Name, runeId }));
                continue;
            }

            if (Property(rune, "appliesTo")?.ToString() is { Length: > 0 } fits && !fits.Equals(item.Type, StringComparison.OrdinalIgnoreCase))
            {
                result.Violations.Add(new RuleViolation(rules.Key, $"{rules.Label}: {rune.Name} can't be put on {item.Name}.", "Actor.Rule.RuneWrongItem", new object?[] { rules.Label, rune.Name, item.Name }));
                continue;
            }

            runes.Add(rune);
        }

        foreach (var group in runes.Where(r => Property(r, "group") != null).GroupBy(r => Property(r, "group")!.ToString()!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            result.Violations.Add(new RuleViolation(rules.Key, $"{rules.Label}: {item.Name} has more than one {group.Key} rune.", "Actor.Rule.RuneGroup", new object?[] { rules.Label, item.Name, group.Key }));
        }

        var properties = runes.Count(r => Property(r, "group") == null);
        var room = runes.Select(r => (int)(ToDouble(Property(r, "slots")) ?? 0)).DefaultIfEmpty(0).Max();
        if (properties > room)
        {
            result.Violations.Add(new RuleViolation(rules.Key, $"{rules.Label}: {item.Name} has {properties.ToString(CultureInfo.InvariantCulture)} property runes but room for {room.ToString(CultureInfo.InvariantCulture)}.", "Actor.Rule.RuneSlots",
                new object?[] { rules.Label, item.Name, properties, room }));
        }
    }
}
