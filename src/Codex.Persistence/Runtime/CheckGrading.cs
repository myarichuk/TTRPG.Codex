using Codex.Core.Rules;
using Codex.Plugin.Abstractions;

namespace Codex.Persistence.Runtime;

/// <summary>Puts a degree on a roll that was made against a DC: the system's <c>dice.checkRoll</c> grades it (<see cref="SheetRolls.Grade"/>), so a roll
/// made off the table and one made at it say the same thing.</summary>
public static class CheckGrading
{
    /// <summary>Sets <see cref="RollLogEntry.Outcome"/> and <see cref="RollLogEntry.Mark"/> from the entry's total and check die when
    /// <paramref name="dc"/> is given and the system names a check roll. Otherwise the entry is left as it is.</summary>
    public static void Apply(RollLogEntry entry, SystemDefinition? system, IEntryResolver resolver, IReadOnlyDictionary<string, object>? sheet, int? dc)
    {
        if (dc is not { } target || system?.Dice.CheckRoll == null) return;
        int? natural = entry.CheckDieIndex is { } at && at >= 0 && at < entry.Kept.Count ? entry.Kept[at] : null;
        try
        {
            if (SheetRolls.Grade(system, resolver, sheet ?? new Dictionary<string, object>(), entry.Total, natural, target) is not { } grade) return;
            entry.Outcome = grade.Outcome;
            entry.Mark = grade.Mark;
        }
        catch (Exception ex) when (ex is RollException or FormulaException or InvalidOperationException)
        {
            // A check roll that cannot be worked out is the system's to fix (the loader says so); the roll itself still stands.
        }
    }
}
