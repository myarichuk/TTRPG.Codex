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

    /// <summary>Words a text box suggests (a datalist): the system's skills and attributes for a check.</summary>
    public IReadOnlyList<string> Suggestions { get; init; } = [];

    /// <summary>For <see cref="HazardColumnKind.Word"/>: the word the box stands for.</summary>
    public string? Word { get; init; }

    /// <summary>For <see cref="HazardColumnKind.Rows"/>: the columns of each row, what one row is called, and what the add button says.</summary>
    public IReadOnlyList<HazardColumn> Columns { get; init; } = [];

    public string? RowLabel { get; init; }
    public string? AddLabel { get; init; }

    /// <summary>The control takes the whole width of the row.</summary>
    public bool Wide { get; init; }

    public string? Hint { get; init; }
}
