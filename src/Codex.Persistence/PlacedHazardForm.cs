namespace Codex.Persistence;

/// <summary>
/// What the place form does with a placed hazard. The form never writes back state it did not change: the table may have moved the hazard on
/// (a disable success, noticed by someone) while the form was open, so an untouched hazard keeps what is stored when the form saves, and one the
/// game master changed starts over (state hidden, counts at zero) unless it is the same entry.
/// </summary>
public sealed class PlacedHazardForm
{
    private const string HomebrewPrefix = "homebrew:";

    private readonly string openedKey;
    private readonly HazardState openedState;
    private readonly string? openedLevels;

    public PlacedHazardForm(PlacedHazard? opened)
    {
        openedKey = KeyOf(opened);
        openedState = opened?.State ?? HazardState.Hidden;
        openedLevels = Clean(opened?.Levels);
        key = openedKey;
        State = openedState;
        Levels = openedLevels;
    }

    /// <summary>The picker's value: the entry's reference, with <c>homebrew:</c> before it for the campaign's own entry. Empty: no hazard.</summary>
    public static string KeyOf(PlacedHazard? hazard) => hazard == null || string.IsNullOrEmpty(hazard.Entry) ? string.Empty : KeyOf(hazard.IsHomebrew, hazard.Entry);

    public static string KeyOf(bool isHomebrew, string entry) => isHomebrew ? HomebrewPrefix + entry : entry;

    private string key = string.Empty;

    /// <summary>The picker's current value. Choosing another hazard puts the state back to hidden and clears the scaled version.</summary>
    public string Key
    {
        get => key;
        set
        {
            var changed = key != value;
            key = value ?? string.Empty;
            if (changed && key != openedKey)
            {
                State = HazardState.Hidden;
                Levels = null;
            }
            else if (changed)
            {
                State = openedState;
                Levels = openedLevels;
            }
        }
    }

    public HazardState State { get; set; }

    /// <summary>The scaled version (<c>5-10</c>), or null for the base numbers.</summary>
    public string? Levels { get; set; }

    /// <summary>True when the game master changed the hazard, its state or its version since the form opened.</summary>
    public bool Changed => key != openedKey || State != openedState || Clean(Levels) != openedLevels;

    /// <summary>What to save. <paramref name="stored"/> is the placement as stored now, read just before saving.</summary>
    public PlacedHazard? Result(PlacedHazard? stored)
    {
        if (!Changed) return stored;
        if (key.Length == 0) return null;
        var homebrew = key.StartsWith(HomebrewPrefix, StringComparison.Ordinal);
        var entry = homebrew ? key[HomebrewPrefix.Length..] : key;
        var same = stored != null && stored.Entry == entry && stored.IsHomebrew == homebrew;
        return new PlacedHazard
        {
            Entry = entry,
            IsHomebrew = homebrew,
            State = State,
            Levels = Clean(Levels),
            Successes = same ? stored!.Successes : 0,
            NoticedBy = same ? stored!.NoticedBy.ToList() : new List<string>()
        };
    }

    private static string? Clean(string? levels) => string.IsNullOrWhiteSpace(levels) ? null : levels.Trim();
}
