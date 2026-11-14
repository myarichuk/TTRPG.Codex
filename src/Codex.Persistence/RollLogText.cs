using Codex.Core.Rules;

namespace Codex.Persistence;

/// <summary>
/// What a roll's line in the table's log says, in the reader's language. The roll's name and the bonuses it counted are kept as parts beside the
/// English text (<see cref="RollLogEntry.Name"/>, <see cref="RollLogEntry.Bonuses"/>); each reader's terms say them, and anything they cannot
/// say stays English. An entry from before the parts existed has only its text, which is shown as it is.
/// </summary>
public static class RollLogText
{
    /// <param name="terms">Text for a game term in the reader's language, or null to show the stored English.</param>
    public static string? Label(RollLogEntry entry, EventTermText? terms)
    {
        if (terms == null || entry.Label == null || entry.Name == null) return entry.Label;
        var name = terms(EventTerm.SystemKind, entry.Name) ?? entry.Name;
        var bonuses = entry.Bonuses.Select(b =>
        {
            var key = EventTerm.Bonus(b.Label, b.Source).Key;
            return new RollBonus(null, b.Source, terms(EventTerm.BonusKind, key) ?? b.Label, b.Amount, true);
        });
        return RollBonusLabel.Of(name, bonuses, entry.Pool);
    }
}
