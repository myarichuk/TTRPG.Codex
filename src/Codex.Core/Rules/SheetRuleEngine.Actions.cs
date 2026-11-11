using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>One way to pay for an action: a slot of <see cref="Level"/> in <see cref="Pool"/> (sheet key <see cref="Key"/>),
/// and the dice the action rolls when paid that way (more for a higher slot).</summary>
public sealed record ActionCost(SlotPoolRule Pool, int Level, string Key, string? Damage);

/// <summary>
/// Something the sheet can do with a roll (<see cref="SheetRules.Actions"/>): cast a picked spell, attack with a held weapon.
/// <see cref="Attack"/> is the kind of attack roll it starts with (null: none), <see cref="SaveAttribute"/> the saving throw it
/// asks for, <see cref="Damage"/> its dice with every bonus already in (<c>1d8+3</c>), or a plain number for an entry
/// without dice. An entry of level 1 or more needs one of <see cref="Costs"/>; with none the sheet has no slot for it.
/// </summary>
public sealed record SheetAction(
    ActionSource Source,
    ContentEntry Entry,
    int Level,
    string? Attack,
    int? AttackBonus,
    string? SaveAttribute,
    int? SaveDc,
    string? Damage,
    string? DamageType,
    bool Heals,
    IReadOnlyList<ActionCost> Costs,
    IReadOnlyDictionary<string, string> Alternatives)
{
    /// <summary>Using it spends a slot.</summary>
    public bool Spends => Level > 0 && Source.Spends.Count > 0;

    /// <summary>Whether an expression has dice to roll; a plain number is shown, not rolled.</summary>
    public static bool HasDice(string? expression) => expression != null && expression.Contains('d', StringComparison.OrdinalIgnoreCase);
}

public sealed partial class SheetRuleEngine
{
    private static readonly Regex LeadingDice = new(@"^(?<count>[0-9]+)d(?<sides>[0-9]+)(?<rest>.*)$", RegexOptions.Compiled);

    private void EvaluateActions(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        if (Rules!.Actions.Count == 0) return;

        var values = FormulaContext(sheet);
        foreach (var (key, value) in result.Derived)
        {
            if (IsIdentifier(key) && ToDouble(value) is { } number) values[key] = number;
        }

        var caster = result.Spellcasting.OrderByDescending(s => s.AttackBonus).FirstOrDefault();
        values["castAttack"] = caster?.AttackBonus ?? 0;
        values["castSave"] = caster?.SaveDc ?? 0;
        values["castMod"] = caster == null ? 0 : ModifierOf(sheet, result, caster.Attribute);

        var trainedClass = Rules.Proficiencies is { } training ? FirstClass(sheet, result, training.ClassField) : null;

        foreach (var source in Rules.Actions)
        {
            var failed = false;
            int? Number(string? formula, IReadOnlyDictionary<string, double> names)
            {
                if (string.IsNullOrWhiteSpace(formula)) return null;
                try
                {
                    return (int)EvaluateFormula(sheet, names, formula);
                }
                catch (Exception)
                {
                    if (!failed) result.Warnings.Add(FormulaError(source.Key, source.Label));
                    failed = true;
                    return null;
                }
            }

            foreach (var entry in ActionEntries(source, result))
            {
                var use = YamlValues.Normalize(Property(entry, source.Property)) as Dictionary<string, object> ?? new Dictionary<string, object>();
                var names = new Dictionary<string, double>(values, StringComparer.Ordinal);
                var abilities = AttributesNamedIn(use.GetValueOrDefault("abilities"));
                names["abilityMod"] = abilities.Count == 0 ? 0 : abilities.Max(a => ModifierOf(sheet, result, a));
                names["trained"] = Rules.Proficiencies == null || (trainedClass != null && WeaponTrained(entry, trainedClass, Rules.Proficiencies)) ? 1 : 0;

                var level = source.LevelProperty == null ? 0 : (int)(ToDouble(Property(entry, source.LevelProperty)) ?? 0);
                var attack = use.GetValueOrDefault("attack")?.ToString();
                var saveAttribute = AttributesNamedIn(use.GetValueOrDefault("save")).FirstOrDefault();

                var dice = use.GetValueOrDefault("damage")?.ToString()?.Replace(" ", "");
                if (dice != null && use.GetValueOrDefault("atLevels") is Dictionary<string, object> steps && (source.ScaleBy ?? system.FieldRoles.Level) is { } scaleBy && names.TryGetValue(scaleBy, out var scale))
                {
                    var reached = steps.Select(s => (At: ToDouble(s.Key), Dice: s.Value?.ToString())).Where(s => s.At <= scale && s.Dice != null).OrderByDescending(s => s.At).FirstOrDefault();
                    if (reached.Dice != null) dice = reached.Dice.Replace(" ", "");
                }

                var bonus = (Number(source.DamageBonus, names) ?? 0) + (use.GetValueOrDefault("addAbility") is true ? Number(source.AbilityBonus, names) ?? 0 : 0);
                var perSlot = use.GetValueOrDefault("perSlot")?.ToString();

                var costs = new List<ActionCost>();
                if (level > 0)
                {
                    foreach (var pool in source.Spends.Select(k => result.SlotPools.FirstOrDefault(p => p.Rule.Key == k)).OfType<SlotPool>())
                    {
                        foreach (var (slotLevel, max) in pool.Max.OrderBy(m => m.Key))
                        {
                            if (slotLevel < level || max <= 0) continue;
                            costs.Add(new ActionCost(pool.Rule, slotLevel, pool.Rule.KeyFor(slotLevel), dice == null ? null : WithBonus(MoreDice(dice, perSlot, slotLevel - level), bonus)));
                        }
                    }
                }

                var alternatives = use.GetValueOrDefault("alternatives") is Dictionary<string, object> others
                    ? others.Where(o => o.Value != null).ToDictionary(o => o.Key, o => WithBonus(o.Value.ToString()!.Replace(" ", ""), bonus))
                    : new Dictionary<string, string>();

                result.Actions.Add(new SheetAction(source, entry, level,
                    attack, attack == null ? null : Number(source.AttackBonus, names),
                    saveAttribute, saveAttribute == null ? null : Number(source.SaveDc, names),
                    dice == null ? null : WithBonus(dice, bonus),
                    use.GetValueOrDefault("damageType")?.ToString(),
                    use.GetValueOrDefault("heals") is true,
                    costs, alternatives));
            }
        }
    }

    /// <summary>The entries a source's actions come from, each once: a spell both prepared and granted is one action.</summary>
    private IEnumerable<ContentEntry> ActionEntries(ActionSource source, SheetEvaluation result)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in source.Lists)
        {
            foreach (var pick in result.ListPicks.GetValueOrDefault(list) ?? Array.Empty<SheetPick>())
            {
                if (seen.Add(pick.Entry.Type + "/" + pick.Entry.Id)) yield return pick.Entry;
            }
        }

        if (source.Inventory == null) yield break;
        var equippedOnly = !source.Inventory.Equals("carried", StringComparison.OrdinalIgnoreCase);
        foreach (var item in result.Inventory)
        {
            if (equippedOnly && !item.Equipped) continue;
            if (source.Tag != null && !item.Entry.Tags.Contains(source.Tag, StringComparer.OrdinalIgnoreCase)) continue;
            if (Property(item.Entry, source.Property) == null) continue;
            if (seen.Add(item.Entry.Type + "/" + item.Entry.Id)) yield return item.Entry;
        }
    }

    /// <summary>Whether a class's weapon training covers an item, by one of its tags or by its name.</summary>
    private static bool WeaponTrained(ContentEntry entry, ContentEntry cls, ProficiencyRules rules)
    {
        var training = Names(cls.Properties.GetValueOrDefault(rules.WeaponProperty));
        var name = StemWords(entry.Name, rules.PluralSuffix);
        return entry.Tags.Any(training.Contains) || training.Any(t => StemWords(t, rules.PluralSuffix) == name);
    }

    /// <summary><paramref name="dice"/> with <paramref name="per"/> added <paramref name="times"/> times: 8d6 and two more
    /// 1d6 is 10d6; dice of another size are added as their own term.</summary>
    public static string MoreDice(string dice, string? per, int times)
    {
        if (times <= 0 || per == null || LeadingDice.Match(per.Replace(" ", "")) is not { Success: true } step || step.Groups["rest"].Length > 0) return dice;
        var added = int.Parse(step.Groups["count"].Value, CultureInfo.InvariantCulture) * times;
        var sides = step.Groups["sides"].Value;
        return LeadingDice.Match(dice) is { Success: true } first && first.Groups["sides"].Value == sides
            ? $"{int.Parse(first.Groups["count"].Value, CultureInfo.InvariantCulture) + added}d{sides}{first.Groups["rest"].Value}"
            : $"{dice}+{added}d{sides}";
    }

    /// <summary>Dice with a flat number added (<c>1d8+3</c>, <c>1d8-1</c>); a plain number stays a plain number.</summary>
    public static string WithBonus(string dice, int bonus)
    {
        if (int.TryParse(dice, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var flat)) return Math.Max(flat + bonus, 0).ToString(CultureInfo.InvariantCulture);
        return bonus == 0 ? dice : bonus > 0 ? $"{dice}+{bonus}" : $"{dice}-{-bonus}";
    }
}
