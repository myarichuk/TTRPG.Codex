namespace Codex.Plugin.Abstractions.Dice;

/// <summary>
/// Resolves a roll expression into a <see cref="DiceRollResult"/> (3.6). A system plugin whose
/// dice work differently from "roll some d-somethings and add a modifier" - SWFFG's narrative
/// dice pool (Success/Advantage/Triumph/Despair symbols, not numbers) is the motivating case -
/// implements this itself and returns it from <see cref="ICodexSystemPlugin.GetDiceRoller"/>
/// instead of relying on <see cref="StandardDiceRoller"/>.
/// </summary>
public interface IDiceRoller
{
    /// <summary>
    /// Rolls <paramref name="expression"/>. Throws <see cref="FormatException"/> for an
    /// expression this roller doesn't understand - the caller (a runtime command) is expected to
    /// catch that and report it back to whoever typed the roll, not crash the campaign's
    /// single-writer loop over a typo.
    /// </summary>
    DiceRollResult Roll(string expression);
}
