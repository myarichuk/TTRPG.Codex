using Codex.Core.Rules;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Systems;

/// <summary>The problems with <c>sheetRules.printed</c> (docs/CONDITION_IMPLIES_DESIGN.md, part b): an entry has a <c>from</c> and a <c>target</c>, or a <c>list</c> and
/// a <c>bonus</c>, never both; a target is the armor class key, a derived value, a Number field, <c>save.&lt;attribute&gt;</c> or <c>check.skill.&lt;key&gt;</c>; and not one a
/// sheet kind claims (a printed number belongs to the sheet-rules side).</summary>
internal static class PrintedChecks
{
    public static IEnumerable<string> Problems(SystemDefinition definition)
    {
        if (definition.SheetRules is not { Printed.Count: > 0 } rules) yield break;
        var kindTargets = definition.SheetKinds.Count > 0 ? SheetConditions.KindTargets(definition) : new HashSet<string>();
        for (var i = 0; i < rules.Printed.Count; i++)
        {
            var entry = rules.Printed[i];
            var where = $"sheetRules.printed[{i}]";
            var hasFrom = !string.IsNullOrWhiteSpace(entry.From);
            var hasList = !string.IsNullOrWhiteSpace(entry.List);
            if (hasFrom && hasList) { yield return $"{where}: has both 'from' and 'list'; a number is one or the other."; continue; }
            if (!hasFrom && !hasList) { yield return $"{where}: needs a 'from' and a 'target', or a 'list' of Strikes."; continue; }
            if (hasList)
            {
                if (string.IsNullOrWhiteSpace(entry.Bonus)) yield return $"{where}: a list of Strikes needs 'bonus', the key of each row's attack bonus.";
                continue;
            }

            var target = entry.Target?.Trim() ?? string.Empty;
            if (target.Length == 0) { yield return $"{where}: 'from' needs a 'target', the number the conditions' modifiers aim at."; continue; }
            if (kindTargets.Contains(target) && SheetRuleEngine.KindOf(definition, target) == SheetRuleEngine.TargetKind.Unknown) yield return $"{where}: the target '{target}' is a sheet kind's; a printed number belongs to the sheet-rules side.";
            else if (!IsKnown(definition, rules, target)) yield return $"{where}: the target '{target}' is not the armor class, a derived value, a Number field, save.<attribute> or check.skill.<key>.";
        }
    }

    private static bool IsKnown(SystemDefinition definition, SheetRules rules, string target)
    {
        // The armor class, a derived value (a ranked list's published score too: Perception, a class DC) or a Number field.
        if (SheetRuleEngine.KindOf(definition, target) is SheetRuleEngine.TargetKind.ArmorClass or SheetRuleEngine.TargetKind.Derived or SheetRuleEngine.TargetKind.Ranked or SheetRuleEngine.TargetKind.Field) return true;
        if (target.StartsWith("save.", StringComparison.Ordinal)) return definition.Attributes.Any(a => string.Equals(a.Key, target[5..], StringComparison.OrdinalIgnoreCase));
        const string skill = "check.skill.";
        return target.StartsWith(skill, StringComparison.Ordinal) && rules.Skills?.List.Any(s => string.Equals(s.Key, target[skill.Length..], StringComparison.OrdinalIgnoreCase)) == true;
    }
}
