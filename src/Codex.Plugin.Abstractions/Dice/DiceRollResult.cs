namespace Codex.Plugin.Abstractions.Dice;

/// <summary>
/// The outcome of one resolved <see cref="IDiceRoller"/> roll (3.6). <see cref="Kept"/> and
/// <see cref="Dropped"/> are split out (rather than one flat list) so a roll log can show
/// "4d6kh3: [5, 4, 3] drop [1]" instead of just the total - a DM and players both want to see
/// what actually came up, not just trust the number.
/// </summary>
public sealed record DiceRollResult(
    string Expression,
    IReadOnlyList<int> Kept,
    IReadOnlyList<int> Dropped,
    int Modifier,
    int Total)
{
    public override string ToString()
    {
        var kept = string.Join(", ", Kept);
        var suffix = Dropped.Count > 0 ? $" drop [{string.Join(", ", Dropped)}]" : string.Empty;
        var modifierText = Modifier switch
        {
            > 0 => $" + {Modifier}",
            < 0 => $" - {-Modifier}",
            _ => string.Empty
        };
        return $"[{kept}]{suffix}{modifierText} = {Total}";
    }
}
