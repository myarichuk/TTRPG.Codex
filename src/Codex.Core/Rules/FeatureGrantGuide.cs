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
        if (FeatureGrantVocabulary.For(system) is not { } vocabulary || vocabulary.CarrierFor(entityType) is not { } carrier) return null;
        var rowProperties = carrier.RowProperties;

        var where = rowProperties.Count > 0
            ? $"Each row of {string.Join(" and ", rowProperties.Select(p => $"\"{p}\""))} may carry, beside its name and description,"
            : "The entry may carry, among its properties,";
        var sb = new StringBuilder();
        sb.AppendLine($"What a feature does on the sheet. {where} the three keys below; the sheet then applies them, so a player does not have to. " +
                      "Use them for everything the text states as a plain fact about the sheet, keep the text as it is, and leave out of them whatever needs a ruling at the table.");

        var targets = vocabulary.Targets.Select(t => t.Described).ToList();
        if (targets.Count > 0)
        {
            var floors = vocabulary.FloorTypes;
            sb.AppendLine($"- \"modifiers\": [{{\"target\": key, \"amount\": number or formula, \"type\": optional}}]. Targets: {string.Join(", ", targets)}. " +
                          $"A formula reads the sheet's numbers ({string.Join(", ", vocabulary.FormulaNames)}) and mod(attribute). Without a type the amount is added." +
                          (floors.Count > 0 ? $" With \"type\": \"{floors[0]}\" the amount is a floor instead: the number becomes at least that much." : ""));
        }

        var lists = vocabulary.Lists.Where(l => !vocabulary.DamageListKeys.Contains(l.Key)).Select(l => $"{l.Key} ({l.Label}: {l.EntityType} ids or names)").ToList();
        if (vocabulary.DamageListKeys.Count > 0)
        {
            var types = vocabulary.DamageTypes.Count > 0 ? ": " + string.Join(", ", vocabulary.DamageTypes) : "";
            lists.Add($"{string.Join(", ", vocabulary.DamageListKeys)} (damage types{types})");
        }

        if (lists.Count > 0)
        {
            sb.AppendLine($"- \"adds\": [{{\"to\": list key, \"entries\": [..], \"level\": optional}}]: entries the feature puts on a list, from that level on when one is given. Lists: {string.Join("; ", lists)}.");
        }

        var rests = vocabulary.Rests;
        sb.Append("- \"uses\": {\"max\": number or formula, \"recoversOn\": [rest names]}: a feature that can be used a limited number of times gets a counter.");
        if (rests.Count > 0) sb.Append($" Rests: {string.Join(", ", rests)}.");
        sb.AppendLine();
        return sb.ToString().TrimEnd();
    }
}
