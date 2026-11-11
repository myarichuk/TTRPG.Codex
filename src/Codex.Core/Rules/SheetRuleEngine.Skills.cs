using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

public sealed partial class SheetRuleEngine
{
    // ---- Saving throws, skills, passive scores ----

    private int BonusValue(SheetEvaluation result, string key) =>
        result.Derived.TryGetValue(key, out var value) ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;

    /// <summary>An attribute's modifier from the final score the attribute rules worked out (or the sheet's own).</summary>
    private int ModifierOf(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result, string attributeKey)
    {
        var attribute = system.Attributes.FirstOrDefault(a => a.Key.Equals(attributeKey, StringComparison.OrdinalIgnoreCase));
        if (attribute == null) return 0;
        var score = result.Attributes?.Totals.GetValueOrDefault(attribute.Key) ?? YamlValues.GetInt(sheet, attribute.Key) ?? attribute.Default;
        return attribute.ModifierFor(score) ?? 0;
    }

    private ContentEntry? FirstClass(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result, string classField) =>
        result.Classes.FirstOrDefault(c => c.IsFirst)?.Entry ?? ReferencedEntry(classField, sheet);

    private static HashSet<string> Names(object? listed) =>
        (listed is not string and IEnumerable<object> list ? list.Select(o => o?.ToString() ?? "") : new[] { listed?.ToString() ?? "" })
        .Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // How many skills a class's choices allow: a number, or the first number in text such as "Choose 2: Acrobatics, …". ASCII digits only:
    // \d also matches other scripts' digits, which int.Parse refuses.
    private static readonly System.Text.RegularExpressions.Regex FirstNumber = new(@"[0-9]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static int? ChoiceCount(object? value) =>
        ToDouble(value) is { } number ? (int)number
        : value is string text && FirstNumber.Match(text) is { Success: true } m ? int.Parse(m.Value, CultureInfo.InvariantCulture)
        : null;

    private void EvaluateSkills(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        if (Rules?.Skills is not { } rules) return;

        var bonus = BonusValue(result, rules.BonusKey);
        var granted = Names(ReferencedEntry(rules.BackgroundField, sheet)?.Properties.GetValueOrDefault(rules.BackgroundProperty));
        var picked = RawList(sheet, rules.ProficienciesKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expertise = RawList(sheet, rules.ExpertiseKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool Has(HashSet<string> names, SkillDefinition skill) => names.Contains(skill.Key) || names.Contains(skill.Label) || (skill.CanonicalLabel != null && names.Contains(skill.CanonicalLabel));

        foreach (var skill in rules.List)
        {
            var proficient = Has(picked, skill) || Has(granted, skill);
            var expert = proficient && Has(expertise, skill);
            if (Has(expertise, skill) && !proficient)
            {
                result.Warnings.Add(new RuleViolation(rules.ExpertiseKey, $"{rules.Label}: {skill.Label} has expertise but no proficiency."));
            }

            var modifier = ModifierOf(sheet, result, skill.Attribute);
            result.Skills.Add(new SkillScore(skill.Key, skill.Label, skill.Attribute, proficient, expert, modifier + (expert ? bonus * 2 : proficient ? bonus : 0)));
        }

        var known = rules.List.SelectMany(s => new[] { s.Key, s.Label, s.CanonicalLabel ?? s.Label }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unknown in picked.Concat(expertise).Where(n => !known.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            result.Warnings.Add(new RuleViolation(rules.ProficienciesKey, $"{rules.Label}: \"{unknown}\" is not a skill this system has."));
        }

        // The first class's skill choices (F24): more chosen than it gives, beyond the background's, is worth a look, not a
        // refusal, since a species or a feat may give more. With a second class it is left alone: a class taken later gives skills
        // of its own (a rogue, one), which its entry does not say.
        if (result.Classes.Count <= 1 && FirstClass(sheet, result, rules.ClassField) is { } firstClass && ChoiceCount(firstClass.Properties.GetValueOrDefault(rules.ClassChoicesProperty)) is { } allowed)
        {
            var chosen = rules.List.Count(s => Has(picked, s) && !Has(granted, s));
            if (chosen > allowed)
            {
                result.Warnings.Add(new RuleViolation(rules.ProficienciesKey, $"{rules.Label}: {chosen} chosen beyond your background, but {firstClass.Name} gives {allowed}. A species or feat may give more.",
                    "Actor.Rule.SkillChoices", new object?[] { rules.Label, chosen, firstClass, allowed }));
            }
        }

        var trained = AttributesNamedIn(FirstClass(sheet, result, rules.ClassField)?.Properties.GetValueOrDefault(rules.SavingThrowsProperty));
        foreach (var attribute in system.Attributes)
        {
            var proficient = trained.Contains(attribute.Key);
            result.SavingThrows.Add(new SkillScore(attribute.Key, attribute.Label, attribute.Key, proficient, false, ModifierOf(sheet, result, attribute.Key) + (proficient ? bonus : 0)));
        }

        foreach (var passive in rules.Passives)
        {
            var skill = result.Skills.FirstOrDefault(s => s.Key.Equals(passive.Skill, StringComparison.OrdinalIgnoreCase));
            if (skill != null) result.Passives.Add(new PassiveScore(passive.Key, passive.Label, passive.Base + skill.Bonus));
        }
    }

    // ---- Spell save DC and attack ----

    private void EvaluateSpellcasting(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        if (Rules?.Spellcasting is not { } rules) return;

        var bonus = BonusValue(result, rules.BonusKey);
        var classes = result.Classes.Count > 0
            ? result.Classes.Where(c => c.Entry != null).Select(c => c.Entry!).ToList()
            : (Rules.Multiclass?.ClassField ?? system.FieldRoles.Class) is { } classField
                ? new[] { ReferencedEntry(classField, sheet) }.OfType<ContentEntry>().ToList()
                : new List<ContentEntry>();
        foreach (var cls in classes)
        {
            // A homebrew caster missing half of what casting needs used to get no DC, or no slots and spells, without a word (F24).
            var hasTable = Rules.SlotPools.Any(p => cls.Properties.ContainsKey(p.Table));
            var ability = AttributesNamedIn(cls.Properties.GetValueOrDefault(rules.AbilityProperty)).FirstOrDefault();
            if (hasTable && ability == null)
            {
                result.Warnings.Add(new RuleViolation(rules.AbilityProperty, $"{cls.Name} has spell slots but names no spellcasting ability, so it has no spell save DC or attack.", "Actor.Rule.NoSpellAbility", new object?[] { cls }));
            }
            else if (!hasTable && ability != null && Rules.SlotPools.Count > 0)
            {
                result.Warnings.Add(new RuleViolation(rules.AbilityProperty, $"{cls.Name} names a spellcasting ability but has no spellcasting table, so it gets no spell slots or prepared spells.", "Actor.Rule.NoSpellTable", new object?[] { cls }));
            }

            if (ability is not { } key) continue;
            var modifier = ModifierOf(sheet, result, key);
            result.Spellcasting.Add(new SpellcastingScore(cls.Name, key, rules.SaveBase + bonus + modifier, bonus + modifier));
        }
    }

    // ---- Armor and weapon training ----

    // A weapon's name and a training's, as words in any order, each without the system's plural ending: "Longswords" is a Longsword.
    private static string StemWords(string text, string? pluralSuffix) =>
        string.Join(' ', text.Replace(',', ' ').ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => !string.IsNullOrEmpty(pluralSuffix) && w.EndsWith(pluralSuffix, StringComparison.OrdinalIgnoreCase) ? w[..^pluralSuffix.Length] : w)
            .Order(StringComparer.Ordinal));

    private void EvaluateProficiencies(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        if (Rules?.Proficiencies is not { } rules || result.Inventory.Count == 0) return;
        var cls = FirstClass(sheet, result, rules.ClassField);
        if (cls == null) return;

        var armorTraining = Names(cls.Properties.GetValueOrDefault(rules.ArmorProperty));
        var allArmor = rules.AllArmor is { Length: > 0 } every && armorTraining.Any(t => t.StartsWith(every, StringComparison.OrdinalIgnoreCase));
        var typeProperty = Rules.ArmorClass?.TypeProperty ?? "armorType";
        var strength = rules.StrengthAttribute;

        foreach (var item in result.Inventory.Where(i => i.Equipped))
        {
            var entry = item.Entry;
            var type = Property(entry, typeProperty)?.ToString();
            if (type != null && rules.ArmorTraining.TryGetValue(type, out var training))
            {
                var isShield = Rules.ArmorClass != null && type.Equals(Rules.ArmorClass.ShieldType, StringComparison.OrdinalIgnoreCase);
                if (!armorTraining.Contains(training) && !(allArmor && !isShield))
                {
                    result.Warnings.Add(new RuleViolation(Rules.Inventory!.Key, $"{entry.Name}: {cls.Name} isn't trained with {training.ToLowerInvariant()}. Without training you have disadvantage on checks, attacks and saves that use Strength or Dexterity, and can't cast spells.", "Actor.Rule.ArmorUntrained", new object?[] { entry, cls, training.ToLowerInvariant() }));
                }

                var needed = strength == null ? null : ToDouble(Property(entry, rules.StrengthProperty));
                var score = strength == null ? 0 : result.Attributes?.Totals.GetValueOrDefault(strength) ?? YamlValues.GetInt(sheet, strength) ?? 10;
                if (needed is > 0 && score < needed.Value)
                {
                    result.Warnings.Add(new RuleViolation(Rules.Inventory!.Key, $"{entry.Name}: needs Strength {(int)needed.Value}, or your Speed drops by 10 feet.", "Actor.Rule.ArmorStrength", new object?[] { entry, (int)needed.Value }));
                }

                if (Property(entry, rules.StealthProperty) is true or "true" or "True")
                {
                    result.Warnings.Add(new RuleViolation(Rules.Inventory!.Key, $"{entry.Name}: gives disadvantage on Stealth checks.", "Actor.Rule.ArmorStealth", new object?[] { entry }));
                }

                continue;
            }

            if (!entry.Tags.Contains(rules.WeaponTag, StringComparer.OrdinalIgnoreCase)) continue;
            if (!WeaponTrained(entry, cls, rules))
            {
                result.Warnings.Add(new RuleViolation(Rules.Inventory!.Key, $"{entry.Name}: {cls.Name} isn't trained with it, so you don't add your proficiency bonus to its attacks.", "Actor.Rule.WeaponUntrained", new object?[] { entry, cls }));
            }
        }
    }

    // ---- Values copied from a chosen entry ----

    /// <summary>Copies the picked entry's properties (a species' speed and size) onto the sheet when the pick
    /// changed since last time; later hand edits are left alone until the entry changes again.</summary>
    public void ApplyInherited(Dictionary<string, object> sheet)
    {
        if (Rules == null) return;
        foreach (var rule in Rules.Inherits)
        {
            var id = YamlValues.GetString(sheet, rule.Field);
            if (string.IsNullOrEmpty(id) || string.Equals(YamlValues.GetString(sheet, rule.AppliedKey), id, StringComparison.OrdinalIgnoreCase)) continue;
            var entry = ReferencedEntry(rule.Field, sheet);
            if (entry == null) continue;
            foreach (var (property, key) in rule.Copy)
            {
                if (entry.Properties.TryGetValue(property, out var value) && value != null) sheet[key] = value;
            }

            sheet[rule.AppliedKey] = id;
        }
    }
}
