using System.Text;
using Codex.Core.AI;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// What a writer of entries (a model homebrewing a spell or a weapon) is told about making an entry roll something on the sheet:
/// the <c>use</c> property (<see cref="ActionSource.Property"/>) the system's <see cref="SheetRules.Actions"/> read, with the
/// system's own attribute and damage type names. Every name comes from the system; nothing here is any game's word.
/// </summary>
public static class ActionUseGuide
{
    /// <summary>The action sources that read entries of <paramref name="entityType"/>: a pick list of that type, or the inventory
    /// when items may be of it.</summary>
    public static IReadOnlyList<ActionSource> SourcesFor(SystemDefinition system, string entityType)
    {
        if (system.SheetRules is not { Actions.Count: > 0 } rules) return Array.Empty<ActionSource>();
        return rules.Actions.Where(source =>
                source.Lists.Any(key => string.Equals(rules.Lists.FirstOrDefault(l => l.Key == key)?.TargetEntityType, entityType, StringComparison.OrdinalIgnoreCase))
                || (source.Inventory != null && rules.Inventory?.Types.Contains(entityType, StringComparer.OrdinalIgnoreCase) == true))
            .ToList();
    }

    /// <summary>The guide for entries of <paramref name="entityType"/>, or null when no action of the system reads them.</summary>
    public static string? For(SystemDefinition system, string entityType)
    {
        var sources = SourcesFor(system, entityType);
        if (sources.Count == 0) return null;

        var property = sources[0].Property;
        var attributes = system.Attributes.Select(a => string.IsNullOrWhiteSpace(a.Label) || a.Label == a.Key ? a.Key : $"{a.Key} ({a.Label})").ToList();
        var damageTypes = system.Vitals?.Damage?.Types ?? new List<string>();
        var attacks = sources.Any(s => s.AttackBonus != null);
        var saves = sources.Any(s => s.SaveDc != null);
        var abilities = sources.Any(s => new[] { s.AttackBonus, s.DamageBonus, s.AbilityBonus }.Any(f => f != null && f.Contains("abilityMod", StringComparison.Ordinal)));
        var addAbility = sources.Any(s => s.AbilityBonus != null);
        var spends = sources.Any(s => s.Spends.Count > 0 && s.LevelProperty != null);
        var scaleBy = sources.Select(s => s.ScaleBy).FirstOrDefault(k => k != null) ?? system.FieldRoles.Level;

        var sb = new StringBuilder();
        sb.AppendLine($"What it rolls on the sheet. Put it under the property \"{property}\", a map of the keys below; the sheet adds the character's own numbers " +
                      "when it is used, so state only the entry's own dice and kinds. " +
                      $"Leave \"{property}\" out when the entry rolls nothing, and write only dice that its text states.");
        if (attacks) sb.AppendLine("- \"attack\": \"melee\" or \"ranged\" when it starts with an attack roll.");
        if (abilities && attributes.Count > 0) sb.AppendLine($"- \"abilities\": [attribute keys] the attack or the damage may use; the best one counts. Attributes: {string.Join(", ", attributes)}.");
        if (saves && attributes.Count > 0) sb.AppendLine($"- \"save\": the attribute of the saving throw it asks for. Attributes: {string.Join(", ", attributes)}.");
        sb.AppendLine("- \"damage\": its dice, \"8d6\" or \"1d4+1\" (a plain number for a fixed amount)." +
                      (damageTypes.Count > 0 ? $" \"damageType\": {string.Join(", ", damageTypes)}." : " \"damageType\": its kind.") +
                      " \"heals\": true when the dice heal instead.");
        if (addAbility) sb.AppendLine("- \"addAbility\": true when the dice add the casting ability's modifier.");
        if (spends) sb.AppendLine("- \"perSlot\": dice added for each slot level above the entry's own, \"1d6\".");
        if (scaleBy != null) sb.AppendLine($"- \"atLevels\": {{\"5\": \"2d10\"}}: the dice from that value of \"{scaleBy}\" on, in place of \"damage\".");
        sb.AppendLine("- \"alternatives\": {\"two-handed\": \"1d10\"}: other dice it may roll, by name.");

        foreach (var source in sources.Where(s => s.LevelProperty != null))
        {
            sb.AppendLine($"The entry's own \"{source.LevelProperty}\" property, not \"{property}\", holds its level{(source.Spends.Count > 0 ? ", which decides the slot it spends" : "")}.");
            break;
        }

        foreach (var source in sources.Where(s => s.Tag != null))
        {
            sb.AppendLine($"An item is offered for {source.Label.ToLowerInvariant()} only when its tags include \"{source.Tag}\" and it has \"{property}\".");
            break;
        }

        sb.Append("Example: ").Append(Example(sources, system.Attributes.Select(a => a.Key).ToList(), damageTypes));
        return sb.ToString().TrimEnd();
    }

    /// <summary>The properties a model's entry of <paramref name="entityType"/> may carry beyond the type's fields so the sheet
    /// can use it: what the actions read (<c>use</c>), and where the whole entry is a feature, its grants.</summary>
    public static IReadOnlyList<FieldSpec> Extras(SystemDefinition system, string entityType)
    {
        var specs = new List<FieldSpec>();
        var declared = system.FindType(entityType)?.Fields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
        void Add(string key, string label, string hint)
        {
            if (declared.Contains(key) || specs.Any(s => s.Key == key)) return;
            specs.Add(new FieldSpec(key, label, "Structured", null, null, null, hint));
        }

        foreach (var source in SourcesFor(system, entityType)) Add(source.Property, "Rolls", "what it rolls; see the guide below");
        if (FeatureGrantVocabulary.For(system)?.CarrierFor(entityType) is { WholeEntry: true })
        {
            Add("modifiers", "Modifiers", "what it changes on the sheet; see the guide below");
            Add("adds", "Adds", "entries it adds to a list; see the guide below");
            Add("uses", "Uses", "its counter of uses; see the guide below");
        }

        return specs;
    }

    private static string Example(IReadOnlyList<ActionSource> sources, List<string> attributes, IReadOnlyList<string> damageTypes)
    {
        var property = sources[0].Property;
        string Kind(int index) => damageTypes.Count == 0 ? "" : $", \"damageType\": \"{damageTypes[Math.Min(index, damageTypes.Count - 1)]}\"";
        string Attr(int index) => attributes.Count == 0 ? "" : attributes[Math.Min(index, attributes.Count - 1)];

        var spell = sources.FirstOrDefault(s => s.Spends.Count > 0 && s.LevelProperty != null) ?? sources.FirstOrDefault(s => s.Lists.Count > 0);
        if (spell != null && attributes.Count > 0 && spell.SaveDc != null)
        {
            var perSlot = spell.Spends.Count > 0 ? ", \"perSlot\": \"1d6\"" : "";
            return $"\"{property}\": {{\"save\": \"{Attr(1)}\", \"damage\": \"8d6\"{Kind(0)}{perSlot}}}, a spell that deals 8d6 to those who fail a {Attr(1)} save.";
        }

        if (attributes.Count > 0 && sources.Any(s => s.AttackBonus != null))
            return $"\"{property}\": {{\"attack\": \"melee\", \"abilities\": [\"{Attr(0)}\"], \"damage\": \"1d8\"{Kind(1)}}}, a melee weapon that rolls 1d8.";
        return $"\"{property}\": {{\"damage\": \"1d8\"{Kind(0)}}}.";
    }
}
