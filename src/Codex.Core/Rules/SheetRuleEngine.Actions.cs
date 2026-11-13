using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// One way to pay for an action: spend <see cref="Amount"/> from the resource pool whose sheet key is <see cref="Key"/>, or
/// nothing at all (<see cref="Free"/>, no key). A pool is any spendable sheet counter with an optional maximum beside it
/// (<see cref="MaxKey"/>): one level of a slot pool (<c>slots_3</c>), a feature's counter of uses (<c>uses_{key}</c>), and
/// later a free casting's own counter. <see cref="Damage"/> is the dice the action rolls when paid this way (for a slot, more
/// for a higher level). <see cref="Label"/> names the pool in the system's language (a slot's unit, a feature's name) for the
/// owner to translate; a slot cost is also told apart by <see cref="Slot"/> and <see cref="Level"/>.
/// <para>New ways to pay are new sources of costs (built with <see cref="OfSlot"/>, <see cref="OfCounter"/>,
/// <see cref="FromPool"/>, <see cref="Nothing"/>), never a new kind the Actions panel has to learn.</para>
/// </summary>
/// <param name="Key">The pool's sheet key; null: the cost is free.</param>
/// <param name="Amount">How much using it spends (0 when free).</param>
/// <param name="MaxKey">The sheet key of the pool's maximum, or null when the pool has none.</param>
/// <param name="Label">The pool's name in the system's language, or null.</param>
/// <param name="Damage">The dice when paid this way, with every bonus in; null: none.</param>
public sealed record ActionCost(string? Key, int Amount, string? MaxKey, string? Label, string? Damage)
{
    /// <summary>The slot pool, when this cost is a slot of it; null for any other pool or none.</summary>
    public SlotPoolRule? Slot { get; init; }

    /// <summary>The slot's level when <see cref="Slot"/> is set; 0 otherwise.</summary>
    public int Level { get; init; }

    /// <summary>The maximum to assume when the sheet holds none under <see cref="MaxKey"/> (a feature counter's evaluated
    /// maximum); null: 0, so a pool with no maximum on the sheet has nothing left.</summary>
    public int? FallbackMax { get; init; }

    /// <summary>Nothing to spend: an at-will use or a ritual.</summary>
    public bool Free => Key == null;

    /// <summary>A stable name for picking this cost among an action's (the pool's key, or "free").</summary>
    public string Id => Key ?? FreeId;

    public const string FreeId = "free";

    /// <summary>One slot of <paramref name="pool"/> at <paramref name="level"/>.</summary>
    public static ActionCost OfSlot(SlotPoolRule pool, int level, string? damage) =>
        new(pool.KeyFor(level), 1, pool.MaxKeyFor(level), pool.UnitName, damage) { Slot = pool, Level = level };

    /// <summary><paramref name="amount"/> from a feature's counter of uses, read as the Table card's Use and Regain read it.</summary>
    public static ActionCost OfCounter(FeatureCounter counter, int amount, string? damage) =>
        new(counter.Key, amount, counter.MaxKey, counter.Label, damage) { FallbackMax = counter.Max };

    /// <summary><paramref name="amount"/> from any pool by its key.</summary>
    public static ActionCost FromPool(string key, int amount, string? maxKey, string? label, string? damage) => new(key, amount, maxKey, label, damage);

    /// <summary>A cost of nothing.</summary>
    public static ActionCost Nothing(string? label, string? damage) => new(null, 0, null, label, damage);

    /// <summary>What is left in the pool, read through <paramref name="value"/> (a sheet key's value, or null when the sheet has
    /// none): an unset pool is full, and it never reads below 0 or above its maximum. Null for a free cost.</summary>
    public int? Left(Func<string, int?> value)
    {
        if (Key == null) return null;
        if (MaxKey == null) return Math.Max(value(Key) ?? 0, 0);
        var max = value(MaxKey) ?? FallbackMax ?? 0;
        return Math.Clamp(value(Key) ?? max, 0, Math.Max(max, 0));
    }

    /// <summary>Whether the pool has <see cref="Amount"/> left now; a free cost always can.</summary>
    public bool CanPay(Func<string, int?> value) => Left(value) is not { } left || left >= Amount;
}

/// <summary>
/// Something the sheet can do with a roll (<see cref="SheetRules.Actions"/>): cast a picked spell, attack with a held weapon.
/// <see cref="Attack"/> is the kind of attack roll it starts with (null: none), <see cref="SaveAttribute"/> the saving throw it
/// asks for, <see cref="Damage"/> its dice with every bonus already in (<c>1d8+3</c>), or a plain number for an entry
/// without dice. An entry of level 1 or more needs one of <see cref="Costs"/>; with none the sheet has no slot for it. Each
/// cost spends from a resource pool (a slot level today) or nothing; see <see cref="ActionCost"/>.
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
    /// <summary>Using it goes through one of <see cref="Costs"/>: an entry of a level its source pays with slots (even when the
    /// sheet has no slot for it, so there is nothing to pick), or any action that has costs.</summary>
    public bool Spends => (Level > 0 && Source.Spends.Count > 0) || Costs.Count > 0;

    /// <summary>How many of this action a turn allows, from the source's <c>count</c>; null when it names none or fails.</summary>
    public int? Count { get; init; }

    /// <summary>The lowest die face that is a critical hit, from the source's <c>critical</c>; null when it names none or fails.</summary>
    public int? Critical { get; init; }

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
                            costs.Add(ActionCost.OfSlot(pool.Rule, slotLevel, dice == null ? null : WithBonus(MoreDice(dice, perSlot, slotLevel - level), bonus)));
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
                    costs, alternatives)
                {
                    Count = Number(source.Count, names),
                    Critical = Number(source.Critical, names)
                });
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
