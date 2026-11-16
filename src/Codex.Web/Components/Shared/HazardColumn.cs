namespace Codex.Web.Components.Shared;

/// <summary>What a column of a hazard's row editor holds (<see cref="HazardRows"/>).</summary>
public enum HazardColumnKind
{
    /// <summary>One line of text.</summary>
    Text,

    /// <summary>A whole number; a box that holds anything else keeps the old value.</summary>
    Whole,

    /// <summary>Several lines of text.</summary>
    Long,

    /// <summary>One of <see cref="HazardColumn.Options"/>, or none.</summary>
    Choice,

    /// <summary>A box: ticked is <c>true</c>, unticked takes the key out.</summary>
    Flag,

    /// <summary>A box that is ticked when the key is missing or true; unticked stores <c>false</c> (<c>passive: false</c> on a route).</summary>
    FlagOnByDefault,

    /// <summary>A box for one word of a list of words (<c>triggersOn: [failure]</c>); the other words in the list stay.</summary>
    Word,

    /// <summary>A list of rows of its own (a band's damage parts).</summary>
    Rows
}

/// <summary>One column of a row of a hazard's list: the key it writes, what it is called, and what kind of control it is.</summary>
public sealed record HazardColumn(string Key, string Label, HazardColumnKind Kind = HazardColumnKind.Text)
{
    /// <summary>For <see cref="HazardColumnKind.Choice"/>: the stored value and what to show.</summary>
    public IReadOnlyList<(string Value, string Label)> Options { get; init; } = [];

    /// <summary>Words a text box suggests (a datalist).</summary>
    public IReadOnlyList<string> Suggestions { get; init; } = [];

    /// <summary>For <see cref="HazardColumnKind.Choice"/>: names the option a stored value stands for (a skill's canonical label for its key), so a value written in another form still shows selected. Null: the value as stored.</summary>
    public Func<string, string>? Match { get; init; }

    /// <summary>For <see cref="HazardColumnKind.Word"/>: the word the box stands for.</summary>
    public string? Word { get; init; }

    /// <summary>For <see cref="HazardColumnKind.Rows"/>: the columns of each row, what one row is called, and what the add button says.</summary>
    public IReadOnlyList<HazardColumn> Columns { get; init; } = [];

    public string? RowLabel { get; init; }
    public string? AddLabel { get; init; }

    /// <summary>The control takes the whole width of the row.</summary>
    public bool Wide { get; init; }

    public string? Hint { get; init; }

    /// <summary>For <see cref="HazardColumnKind.Choice"/>: adds an "Other..." choice that shows a text box with this label, so a word the list does not hold is stored as written.</summary>
    public string? OtherLabel { get; init; }

    /// <summary>For <see cref="HazardColumnKind.Choice"/>: the key of the same row whose value is not offered (a route cannot wait for its own id).</summary>
    public string? NotOwn { get; init; }

    /// <summary>The box holds dice, numbers or ranges: always drawn left to right, so "2d8" and "5-10" keep their order in a right to left page.</summary>
    public bool Ltr { get; init; }
}
