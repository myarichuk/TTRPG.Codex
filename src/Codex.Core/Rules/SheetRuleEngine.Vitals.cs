using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>One carried item: its place in the sheet's raw list, the entry it names and what the sheet says about it.</summary>
public sealed record InventoryItem(int Index, string Raw, ContentEntry Entry, int Quantity, bool Equipped);

/// <summary>What one character level adds to maximum hit points.</summary>
public sealed record HitPointLevel(int Level, string ClassName, int Die, int Base, bool IsRolled, int Modifier, int Gain, int Extra = 0);

/// <summary>Maximum hit points as the rules work them out. <see cref="Active"/> is false in manual mode
/// or while no class with a hit die is chosen: the sheet's own number stands.</summary>
public sealed record HitPointSummary(string Mode, bool Active, int Max, int Modifier, IReadOnlyList<HitPointLevel> Levels, int LevelExtra = 0, string? ExtraSource = null);

/// <summary>Armor class as the rules work it out, with a line per contribution for display.</summary>
public sealed record ArmorClassSummary(string Mode, bool Active, int Total, IReadOnlyList<string> Lines);

public sealed partial class SheetRuleEngine
{
    private static readonly Regex HitDie = new(@"^\s*[dD]?(?<sides>\d+)\s*$", RegexOptions.Compiled);

    // ---- Inventory ----

    private void EvaluateInventory(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        if (Rules?.Inventory is not { } rules) return;

        var raw = RawList(sheet, rules.Key);
        for (var index = 0; index < raw.Count; index++)
        {
            var (id, choices) = ParsePick(raw[index]);
            var entry = rules.Types.Select(type => resolver.Resolve(type, id)).FirstOrDefault(e => e != null);
            if (entry == null)
            {
                result.Violations.Add(new RuleViolation(rules.Key, $"{rules.Label}: \"{id}\" is not in the Library."));
                continue;
            }

            var quantity = choices.TryGetValue(rules.QuantityChoice, out var q) ? Math.Max(q, 1) : 1;
            var equipped = choices.TryGetValue(rules.EquippedChoice, out var e) && e > 0;
            result.Inventory.Add(new InventoryItem(index, raw[index], entry, quantity, equipped));
        }

        if (Rules.ArmorClass is { } armor)
        {
            var worn = result.Inventory.Where(i => i.Equipped).ToList();
            if (worn.Count(i => IsBodyArmor(i.Entry, armor)) > 1)
            {
                result.Violations.Add(new RuleViolation(rules.Key, $"{rules.Label}: only one suit of armor can be worn at a time."));
            }

            if (worn.Count(i => IsShield(i.Entry, armor)) > 1)
            {
                result.Violations.Add(new RuleViolation(rules.Key, $"{rules.Label}: only one shield can be carried in hand."));
            }
        }
    }

    /// <summary>The sheet value for an item with its quantity and equipped state, ready to store in the list.</summary>
    public static string FormatItem(InventoryRules rules, string id, int quantity, bool equipped)
    {
        var choices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (quantity > 1) choices[rules.QuantityChoice] = quantity;
        if (equipped) choices[rules.EquippedChoice] = 1;
        return FormatPick(id, choices);
    }

    // ---- Hit points ----

    /// <summary>The hit die's sides from text like "d10" (null when it isn't one).</summary>
    public static int? ParseHitDie(object? value) =>
        value != null && HitDie.Match(value.ToString() ?? string.Empty) is { Success: true } m
        && int.TryParse(m.Groups["sides"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sides) && sides > 0
            ? sides
            : null;

    /// <summary>The sheet's classes with their levels: the multiclass views when the system has them, else the one class.</summary>
    private List<ClassLevel> SheetClasses(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result, string classField, string levelField)
    {
        if (Rules?.Multiclass != null) return result.Classes;

        var id = YamlValues.GetString(sheet, classField);
        if (string.IsNullOrEmpty(id)) return new List<ClassLevel>();
        var type = system.ActorFields.FirstOrDefault(f => f.Key == classField)?.TargetEntityType;
        var field = system.ActorFields.FirstOrDefault(f => f.Key == levelField);
        var level = YamlValues.GetInt(sheet, levelField) ?? (int)(ToDouble(field?.DefaultValue) ?? 1);
        return new List<ClassLevel> { new(id, type == null ? null : resolver.Resolve(type, id), Math.Max(level, 1), true) };
    }

    private int ModifierOf(IReadOnlyDictionary<string, object> sheet, string attributeKey)
    {
        var attribute = system.Attributes.FirstOrDefault(a => a.Key.Equals(attributeKey, StringComparison.OrdinalIgnoreCase));
        if (attribute == null || attribute.Modifier != "half-minus-five") return 0;
        var score = YamlValues.GetInt(sheet, attribute.Key) ?? attribute.Default;
        return (int)Math.Floor((score - 10) / 2.0);
    }

    private void EvaluateHitPoints(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        if (Rules?.HitPoints is not { } rules) return;

        var requested = (YamlValues.GetString(sheet, rules.ModeKey) ?? "manual").ToLowerInvariant();
        var mode = requested == "manual" ? "manual" : rules.Modes.Contains(requested) ? requested : rules.Default;
        var modifier = ModifierOf(sheet, rules.Attribute);
        var rolls = RawList(sheet, rules.RollsKey).Select(r => int.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0).ToList();

        // A species can add to every level (Dwarven Toughness).
        var extraEntry = rules.PerLevelBonus == null ? null : ReferencedEntry(rules.PerLevelBonus.Field, sheet);
        var extra = extraEntry == null ? 0 : (int)(ToDouble(extraEntry.Properties.GetValueOrDefault(rules.PerLevelBonus!.Property)) ?? 0);
        var levels = new List<HitPointLevel>();
        var number = 0;
        foreach (var cls in SheetClasses(sheet, result, rules.ClassField, rules.LevelField))
        {
            var die = cls.Entry != null && cls.Entry.Properties.TryGetValue(rules.DieProperty, out var raw) ? ParseHitDie(raw) : null;
            for (var i = 0; i < cls.Level; i++)
            {
                number++;
                if (die == null) continue;
                var rolled = false;
                int baseValue;
                if (number == 1)
                {
                    baseValue = die.Value;
                }
                else if (mode == "rolled" && number - 2 < rolls.Count && rolls[number - 2] > 0)
                {
                    baseValue = Math.Clamp(rolls[number - 2], 1, die.Value);
                    rolled = true;
                }
                else
                {
                    baseValue = die.Value / 2 + 1;
                }

                levels.Add(new HitPointLevel(number, cls.Entry!.Name, die.Value, baseValue, rolled, modifier, Math.Max(baseValue + modifier + extra, rules.MinimumPerLevel), extra));
            }
        }

        result.HitPoints = new HitPointSummary(mode, mode != "manual" && levels.Count > 0, levels.Sum(l => l.Gain), modifier, levels, extra, extra != 0 ? extraEntry!.Name : null);
    }

    /// <summary>
    /// Writes the computed maximum hit points to the sheet and keeps current hit points in step: a
    /// character whose maximum grows gains the difference (a level up arrives healthy), one whose
    /// maximum shrinks loses the same amount (never below 0), and a fresh character starts at full health.
    /// </summary>
    public void ApplyHitPoints(Dictionary<string, object> sheet, SheetEvaluation evaluation)
    {
        if (Rules?.HitPoints is not { } rules || evaluation.HitPoints is not { Active: true } summary) return;

        var previousMax = YamlValues.GetInt(sheet, rules.Key);
        var current = YamlValues.GetInt(sheet, rules.CurrentKey);
        sheet[rules.Key] = (long)summary.Max;
        var next = previousMax == null || current == null ? summary.Max : current.Value + (summary.Max - previousMax.Value);
        sheet[rules.CurrentKey] = (long)Math.Clamp(next, 0, summary.Max);
    }

    // ---- Armor class ----

    public static bool IsBodyArmor(ContentEntry entry, ArmorClassRules rules) => ToDouble(Property(entry, rules.BaseProperty)).HasValue;

    public static bool IsShield(ContentEntry entry, ArmorClassRules rules) =>
        string.Equals(Property(entry, rules.TypeProperty)?.ToString(), rules.ShieldType, StringComparison.OrdinalIgnoreCase);

    private static object? Property(ContentEntry entry, string key) => entry.Properties.TryGetValue(key, out var value) ? value : null;

    private void EvaluateArmorClass(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        if (Rules?.ArmorClass is not { } rules) return;

        var requested = (YamlValues.GetString(sheet, rules.ModeKey) ?? "manual").ToLowerInvariant();
        var mode = requested == "auto" ? "auto" : "manual";
        var lines = new List<string>();
        var (interpreter, identifiers) = FormulaContext(sheet);

        var worn = result.Inventory.Where(i => i.Equipped).Select(i => i.Entry).ToList();
        var body = worn.Where(e => IsBodyArmor(e, rules)).OrderByDescending(e => ToDouble(Property(e, rules.BaseProperty))).FirstOrDefault();
        var shielded = worn.Any(e => IsShield(e, rules));
        var dex = ModifierOf(sheet, rules.DexAttribute);
        long total;

        if (body != null)
        {
            var baseValue = (int)ToDouble(Property(body, rules.BaseProperty))!.Value;
            var cap = ToDouble(Property(body, rules.DexCapProperty));
            var dexPart = cap.HasValue ? (cap.Value <= 0 ? 0 : Math.Min(dex, (int)cap.Value)) : dex;
            total = baseValue + dexPart;
            lines.Add($"{body.Name}: {baseValue}");
            if (dexPart != 0 || !cap.HasValue) lines.Add($"Dexterity: {Signed(dexPart)}{(cap is > 0 ? $" (limited to +{(int)cap.Value})" : string.Empty)}");
        }
        else
        {
            var options = new List<(string Label, long Value)>();
            try
            {
                options.Add(("Unarmored", EvaluateFormula(interpreter, identifiers, rules.Unarmored)));
            }
            catch (Exception ex)
            {
                result.Violations.Add(new RuleViolation(rules.Key, $"Armor Class: formula error ({ex.Message})."));
            }

            foreach (var cls in SheetClasses(sheet, result, Rules.HitPoints?.ClassField ?? "class", Rules.HitPoints?.LevelField ?? "level"))
            {
                if (cls.Entry == null || !cls.Entry.Properties.TryGetValue(rules.ClassFormulaProperty, out var formula) || string.IsNullOrWhiteSpace(formula?.ToString())) continue;
                var shieldAllowed = !cls.Entry.Properties.TryGetValue(rules.ClassShieldProperty, out var allows) || allows is not (false or "false" or "False");
                if (shielded && !shieldAllowed) continue;
                try
                {
                    options.Add(($"{cls.Entry.Name} defense", EvaluateFormula(interpreter, identifiers, formula!.ToString()!)));
                }
                catch (Exception ex)
                {
                    result.Violations.Add(new RuleViolation(rules.Key, $"Armor Class: {cls.Entry.Name}'s formula error ({ex.Message})."));
                }
            }

            if (options.Count == 0) options.Add(("Unarmored", 10 + dex));
            var best = options.OrderByDescending(o => o.Value).First();
            total = best.Value;
            lines.Add($"{best.Label}: {best.Value}");
        }

        foreach (var entry in worn)
        {
            var bonus = ToDouble(Property(entry, rules.BonusProperty));
            if (!bonus.HasValue || bonus.Value == 0) continue;
            total += (long)bonus.Value;
            lines.Add($"{entry.Name}: {Signed((int)bonus.Value)}");
        }

        foreach (var listKey in rules.BonusLists)
        {
            foreach (var pick in result.ListPicks.GetValueOrDefault(listKey) ?? Array.Empty<SheetPick>())
            {
                var bonus = ToDouble(Property(pick.Entry, rules.BonusProperty));
                if (!bonus.HasValue || bonus.Value == 0) continue;
                if ((Property(pick.Entry, rules.RequiresArmorProperty) is true or "true" or "True") && body == null) continue;
                total += (long)bonus.Value;
                lines.Add($"{pick.Entry.Name}: {Signed((int)bonus.Value)}");
            }
        }

        var misc = YamlValues.GetInt(sheet, rules.MiscField) ?? 0;
        if (misc != 0)
        {
            total += misc;
            lines.Add($"Other bonuses: {Signed(misc)}");
        }

        result.ArmorClass = new ArmorClassSummary(mode, mode == "auto", (int)total, lines);
    }

    /// <summary>Writes the computed armor class to the sheet while the sheet follows the rules.</summary>
    public void ApplyArmorClass(Dictionary<string, object> sheet, SheetEvaluation evaluation)
    {
        if (Rules?.ArmorClass is not { } rules || evaluation.ArmorClass is not { Active: true } summary) return;
        sheet[rules.Key] = (long)summary.Total;
    }

    private static string Signed(int value) => value >= 0 ? "+" + value : value.ToString(CultureInfo.InvariantCulture);
}
