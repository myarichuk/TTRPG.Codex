using System.Text;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// What a writer of entries (a model homebrewing a subclass, a pack's author) is told about making a feature do something on
/// the sheet: where on an entry of a type the system reads features (<see cref="SheetRules.Features"/>), and the names this
/// system gives the things a feature may change. Every name comes from the system; nothing here is any game's word.
/// </summary>
public static class FeatureGrantGuide
{
    /// <summary>The guide for entries of <paramref name="entityType"/>, or null when the system reads no features from them.</summary>
    public static string? For(SystemDefinition system, string entityType)
    {
        if (system.SheetRules is not { Features.Count: > 0 } rules) return null;

        var rowProperties = new List<string>();
        var wholeEntry = false;
        foreach (var source in rules.Features)
        {
            var type = source.List != null
                ? rules.Lists.FirstOrDefault(l => l.Key == source.List)?.TargetEntityType
                : system.ActorFields.FirstOrDefault(f => f.Key == source.Field)?.TargetEntityType;
            if (!string.Equals(type, entityType, StringComparison.OrdinalIgnoreCase)) continue;
            if (source.Property == null) wholeEntry = true;
            else if (!rowProperties.Contains(source.Property)) rowProperties.Add(source.Property);
        }

        if (!wholeEntry && rowProperties.Count == 0) return null;

        var where = rowProperties.Count > 0
            ? $"Each row of {string.Join(" and ", rowProperties.Select(p => $"\"{p}\""))} may carry, beside its name and description,"
            : "The entry may carry, among its properties,";
        var sb = new StringBuilder();
        sb.AppendLine($"What a feature does on the sheet. {where} the three keys below; the sheet then applies them, so a player does not have to. " +
                      "Use them for everything the text states as a plain fact about the sheet, keep the text as it is, and leave out of them whatever needs a ruling at the table.");

        var targets = Targets(system, rules);
        if (targets.Count > 0)
        {
            var floors = system.ModifierTypes.Types.Where(t => string.Equals(t.Value, ModifierStacking.Best, StringComparison.OrdinalIgnoreCase)).Select(t => t.Key).ToList();
            sb.AppendLine($"- \"modifiers\": [{{\"target\": key, \"amount\": number or formula, \"type\": optional}}]. Targets: {string.Join(", ", targets)}. " +
                          $"A formula reads the sheet's numbers ({string.Join(", ", FormulaNames(system, rules))}) and mod(attribute). Without a type the amount is added." +
                          (floors.Count > 0 ? $" With \"type\": \"{floors[0]}\" the amount is a floor instead: the number becomes at least that much." : ""));
        }

        var lists = rules.Lists.Where(l => l.MaxCount == null).Select(l => $"{l.Key} ({l.Label}: {l.TargetEntityType} ids or names)").ToList();
        if (system.Vitals?.Damage is { } damage)
        {
            var types = damage.Types.Count > 0 ? ": " + string.Join(", ", damage.Types) : "";
            lists.Add($"{damage.ResistancesField}, {damage.ImmunitiesField}, {damage.VulnerabilitiesField} (damage types{types})");
        }

        if (lists.Count > 0)
        {
            sb.AppendLine($"- \"adds\": [{{\"to\": list key, \"entries\": [..], \"level\": optional}}]: entries the feature puts on a list, from that level on when one is given. Lists: {string.Join("; ", lists)}.");
        }

        var rests = (system.Vitals?.Rests.Select(r => r.Name) ?? Enumerable.Empty<string>()).Concat(rules.SlotPools.SelectMany(p => p.RecoversOn)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        sb.Append("- \"uses\": {\"max\": number or formula, \"recoversOn\": [rest names]}: a feature that can be used a limited number of times gets a counter.");
        if (rests.Count > 0) sb.Append($" Rests: {string.Join(", ", rests)}.");
        sb.AppendLine();
        return sb.ToString().TrimEnd();
    }

    private static List<string> Targets(SystemDefinition system, SheetRules rules)
    {
        var targets = new List<string>();
        void Add(string key, string? label)
        {
            if (!targets.Any(t => t.StartsWith(key + " ", StringComparison.Ordinal) || t == key)) targets.Add(string.IsNullOrWhiteSpace(label) || label == key ? key : $"{key} ({label})");
        }

        if (rules.ArmorClass is { } armor) Add(armor.Key, system.ActorFields.FirstOrDefault(f => f.Key == armor.Key)?.Label);
        if (rules.HitPoints is { } hitPoints) Add(hitPoints.Key, system.Resources.FirstOrDefault(r => r.MaxKey == hitPoints.Key)?.Label);
        foreach (var derived in rules.Derived) Add(derived.Key, derived.Label);
        foreach (var field in system.ActorFields.Where(f => f.Type == FieldType.Number && f.Key != system.FieldRoles.Level && f.Key != rules.ArmorClass?.MiscField)) Add(field.Key, field.Label);
        return targets;
    }

    private static IEnumerable<string> FormulaNames(SystemDefinition system, SheetRules rules)
    {
        if (system.FieldRoles.Level is { } level) yield return level;
        if (rules.Multiclass is { } multiclass) yield return multiclass.ClassLevelKey;
        foreach (var derived in rules.Derived) yield return derived.Key;
        foreach (var attribute in system.Attributes) yield return attribute.Key;
    }
}
