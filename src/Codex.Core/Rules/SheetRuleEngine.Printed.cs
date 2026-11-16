using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>A number a printed creature carries (its armor class, a save, Perception, a DC, a Strike's bonus), with what the conditions that hold do to it:
/// <see cref="Printed"/> as the stat block prints it, <see cref="Total"/> with the condition modifiers added, who gave them (<see cref="Bonuses"/>) and the
/// dice that wait for a roll (<see cref="Dice"/>, never added to the total).</summary>
public sealed record PrintedNumber(string Key, string Label, string Target, int Printed, int Total, IReadOnlyList<RollBonus> Bonuses, IReadOnlyList<RollDice> Dice);

public sealed partial class SheetRuleEngine
{
    /// <summary>Whether the sheet is a printed creature's: its <c>attrMethod</c> is <see cref="PrintedMethod"/>.</summary>
    public bool IsPrinted(IReadOnlyDictionary<string, object> sheet) =>
        (YamlValues.GetString(sheet, MethodKey) ?? Rules?.Attributes?.Default)?.Equals(PrintedMethod, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The numbers a printed creature carries, each with the conditions that hold applied (docs/CONDITION_IMPLIES_DESIGN.md, part b). Empty unless the
    /// sheet's <c>attrMethod</c> is <see cref="PrintedMethod"/> and <c>sheetRules.printed</c> names numbers. A separate pure method: it is not part of
    /// <see cref="Evaluate"/> and never writes the sheet. Only <b>condition</b> modifiers count, never features: a printed number already holds the
    /// creature's own abilities. A missing or non-numeric base gives no entry, and the host shows what it showed before. A host uses a printed number
    /// <b>instead of</b> the worked-out site, never on top of it, or the same condition would count twice.
    /// </summary>
    public IReadOnlyList<PrintedNumber> Printed(IReadOnlyDictionary<string, object> live)
    {
        if (Rules is not { Printed.Count: > 0 } rules || !IsPrinted(live)) return Array.Empty<PrintedNumber>();
        var features = ConditionModifiers(live).Select(c => c.Feature).ToList();
        var derived = features.Count == 0 ? new Dictionary<string, double>() : DerivedNumbers(WorkedOutDerived(live));
        var found = new List<PrintedNumber>();
        foreach (var entry in rules.Printed)
        {
            if (!string.IsNullOrWhiteSpace(entry.List))
            {
                var rows = RawRows(live, entry.List);
                for (var i = 0; i < rows.Count; i++)
                {
                    if (YamlValues.ToNumber(rows[i].GetValueOrDefault(entry.Bonus ?? "bonus")) is not { } bonus) continue;
                    var name = entry.Name != null ? rows[i].GetValueOrDefault(entry.Name)?.ToString() : null;
                    var ranged = entry.Range != null && string.Equals(rows[i].GetValueOrDefault(entry.Range)?.ToString()?.Trim(), RollSiteTargets.Ranged, StringComparison.OrdinalIgnoreCase);
                    var site = StrikeSite(ranged ? RollSiteTargets.Ranged : RollSiteTargets.Melee);
                    found.Add(Number($"{entry.List}.{i}", string.IsNullOrWhiteSpace(name) ? $"{entry.List} {i + 1}" : name!, RollSiteTargets.Attack, (int)bonus, site, features, derived));
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.From) || string.IsNullOrWhiteSpace(entry.Target)) continue;
            if (YamlValues.ToNumber(live.GetValueOrDefault(entry.From)) is not { } printed) continue;
            found.Add(Number(entry.From, PrintedLabel(entry), entry.Target, (int)printed, SiteOfTarget(entry.Target), features, derived));
        }

        return found;
    }

    /// <summary>What the conditions that hold add to <paramref name="target"/> (a number the sheet keeps by hand, such as a manual armor class), as a bonus that is
    /// shown with the stored number and never written to the sheet. Only condition modifiers count.</summary>
    public RollSiteBonus ConditionBonus(IReadOnlyDictionary<string, object> live, string target)
    {
        var features = ConditionModifiers(live).Select(c => c.Feature).ToList();
        return features.Count == 0 ? RollSiteBonus.None : SiteBonus(features, new RollSite(RollKind.Check, new[] { target }), DerivedNumbers(WorkedOutDerived(live)), null);
    }

    private PrintedNumber Number(string key, string label, string target, int printed, RollSite site, IReadOnlyList<ActiveFeature> features, IReadOnlyDictionary<string, double> derived)
    {
        var bonus = features.Count == 0 ? RollSiteBonus.None : SiteBonus(features, site, derived, null);
        return new PrintedNumber(key, label, target, printed, printed + bonus.Number, bonus.Bonuses, bonus.Dice);
    }

    /// <summary>What the printed number aimed at <paramref name="target"/> reads: a save of an attribute, a skill's check, or the target alone (armor class,
    /// a derived value, a Number field).</summary>
    private RollSite SiteOfTarget(string target)
    {
        const string save = RollSiteTargets.Save + ".";
        const string skill = RollSiteTargets.Check + "." + RollSiteTargets.Skill + ".";
        if (target.StartsWith(save, StringComparison.Ordinal)) return SaveSite(target[save.Length..]);
        if (target.StartsWith(skill, StringComparison.Ordinal)) return SkillSite(target[skill.Length..]);
        return new RollSite(RollKind.Check, new[] { target });
    }

    /// <summary>The attack roll of a printed Strike: <c>attack</c>, <c>attack.weapon</c>, its range, and both together.</summary>
    private static RollSite StrikeSite(string range) => new(RollKind.WeaponAttack, new[]
    {
        RollSiteTargets.Attack, $"{RollSiteTargets.Attack}.{RollSiteTargets.Weapon}", $"{RollSiteTargets.Attack}.{range}", $"{RollSiteTargets.Attack}.{RollSiteTargets.Weapon}.{range}"
    });

    private string PrintedLabel(PrintedNumberDefinition entry)
    {
        var field = system.ActorFields.FirstOrDefault(f => f.Key == entry.From);
        if (field != null) return field.Label;
        return Rules?.Derived.FirstOrDefault(d => d.Key == entry.From)?.Label ?? entry.From ?? string.Empty;
    }

    /// <summary>The rows of a list on the sheet that are maps (a creature's printed Strikes), read as text-keyed maps.</summary>
    private static List<IReadOnlyDictionary<string, object>> RawRows(IReadOnlyDictionary<string, object> sheet, string key)
    {
        var rows = new List<IReadOnlyDictionary<string, object>>();
        if (YamlValues.Normalize(sheet.GetValueOrDefault(key)) is not IEnumerable<object> items) return rows;
        foreach (var item in items)
        {
            if (YamlValues.Normalize(item) is IDictionary<string, object> map) rows.Add(new Dictionary<string, object>(map, StringComparer.Ordinal));
        }

        return rows;
    }
}
