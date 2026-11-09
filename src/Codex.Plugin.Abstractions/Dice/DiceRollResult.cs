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
    /// <summary>Where in <see cref="Kept"/> the roll's one d20 check die is (<c>1d20+5</c>, <c>adv</c>), so a natural 20 or 1 is
    /// read from that die only; null when the roll has none (<c>1d200</c>, <c>2d6</c>) or more than one (R8).</summary>
    public int? CheckDieIndex { get; init; }

    /// <summary>For a roll that counts successes (<c>5d10&gt;=8</c>), how many; <see cref="Total"/> is then the successes plus
    /// the modifier. Null for a roll that adds its dice.</summary>
    public int? Successes { get; init; }

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
        var counted = Successes is { } n ? $" ({n} successes)" : string.Empty;
        return $"[{kept}]{suffix}{counted}{modifierText} = {Total}";
    }
}
