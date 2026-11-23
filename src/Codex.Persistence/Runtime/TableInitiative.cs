using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;

namespace Codex.Persistence.Runtime;

/// <summary>How a creature's initiative roll is made, the one place the combat console and a prepared encounter's start both work it out, so a creature
/// rolls the same either way (ENCOUNTER_BUILDER_DESIGN.md 4.2). The die, the bonus field and the tie-break attribute are the system's (<see cref="DiceConventions"/>).</summary>
public static class TableInitiative
{
    /// <summary>The die the table rolls for initiative when nothing else is chosen: the system's, else <c>1d20</c>.</summary>
    public static string Die(SystemDefinition? system) => system?.Dice.Initiative ?? "1d20";

    /// <summary>The system's initiative bonus for one sheet: the derived field <see cref="DiceConventions.InitiativeBonus"/> names, worked out on
    /// <paramref name="liveSheet"/> by <see cref="SheetRuleEngine.EvaluateLive"/>. Zero when the system names none, there is no engine, the field is missing,
    /// the sheet cannot be built, or the rules fail on it. <paramref name="liveSheet"/> must make a copy for evaluating each time (EvaluateLive changes it); it and
    /// <paramref name="linked"/> are asked for only when there is something to work out.</summary>
    public static int Bonus(SystemDefinition? system, SheetRuleEngine? engine, Func<Dictionary<string, object>> liveSheet, Func<ILinkedSheets?>? linked = null)
    {
        if (engine == null || system?.Dice.InitiativeBonus is not { } key) return 0;
        try
        {
            // Building the sheet is inside the try, as it was in the console: a sheet that cannot be laid out gives no bonus, not a failed render or start.
            var evaluation = engine.EvaluateLive(liveSheet(), linked?.Invoke());
            return evaluation.Derived.TryGetValue(key, out var value) ? Convert.ToInt32(value) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>The tie-break score: the attribute <see cref="DiceConventions.InitiativeTieBreak"/> names, read off the stored sheet, else that attribute's
    /// default, else 0. Zero when the system names none.</summary>
    public static int TieBreak(SystemDefinition? system, IReadOnlyDictionary<string, object> sheet)
    {
        if (system?.Dice.InitiativeTieBreak is not { } key) return 0;
        return YamlValues.GetInt(sheet, key) ?? system.Attributes.FirstOrDefault(a => a.Key == key)?.Default ?? 0;
    }

    /// <summary>The expression rolled: the die (or <c>adv</c>/<c>dis</c>) with the bonus after it, <c>1d20+2</c>, <c>1d20-1</c>, <c>1d20</c>.</summary>
    public static string Expression(string die, int bonus) => die + (bonus == 0 ? "" : bonus > 0 ? $"+{bonus}" : bonus.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The bonus as the console's roll button shows it after the die: <c> + 2</c>, <c> - 1</c>, or nothing.</summary>
    public static string BonusText(int bonus) => bonus == 0 ? "" : bonus > 0 ? $" + {bonus}" : $" - {-bonus}";
}
