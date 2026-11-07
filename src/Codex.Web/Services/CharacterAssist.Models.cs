using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Codex.Web.Services;

/// <summary>What a proposed change does to its field: set a value, add a paragraph to a long text, or add or remove one entry of a list.</summary>
public enum SheetChangeOp { Set, Append, Add, Remove }

/// <summary>One change the model proposes to a character sheet, checked against the system and the caller's Library, as the
/// person reviews it (docs/SERVER_ACCESS_AND_VAULT.md 6). Nothing is changed until they take it.</summary>
public sealed class SheetChange
{
    /// <summary>The sheet key: a field, <c>base_str</c> for an attribute, a list, the inventory or the skills.</summary>
    public required string Field { get; init; }
    public required string Label { get; init; }
    public SheetChangeOp Op { get; init; }

    /// <summary>The value as the sheet stores it: an entry id, a skill key, a number or text.</summary>
    public required string Value { get; init; }
    public int Quantity { get; init; } = 1;

    /// <summary>The value as a person reads it: the entry's name, the skill's label.</summary>
    public required string Shown { get; init; }

    /// <summary>What a <see cref="SheetChangeOp.Set"/> replaces, as the sheet held it when the change was proposed; taking the change
    /// after the field changed since is refused.</summary>
    public string? Before { get; init; }
    public string? BeforeShown { get; init; }
    public required string Reason { get; init; }

    /// <summary>The rules' warnings this change brings (armor the class is not trained in), which never stop it.</summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>Something on the sheet that fits the character's concept, with why; it changes nothing.</summary>
public sealed record SheetHighlight(string Field, string Label, string? Value, string? Shown, string Reason);

/// <summary>What one request to the model came back with: changes to review, or highlights, and its closing words.</summary>
public sealed class CharacterAssistResult
{
    public List<SheetChange> Changes { get; init; } = new();
    public List<SheetHighlight> Highlights { get; init; } = new();
    public string? Note { get; init; }

    /// <summary>The rules' warnings no one change brought.</summary>
    public List<string> Warnings { get; init; } = new();
}

/// <summary>One change as the model writes it in <c>propose_changes</c>.</summary>
public sealed class ChangeInput
{
    [Description("A key from sheet_fields.")]
    public string? Field { get; set; }

    [Description("\"set\" a field or an attribute; \"append\" a paragraph to a long text; \"add\" or \"remove\" one entry of a list, the inventory or the skills.")]
    public string? Op { get; set; }

    [Description("For an entry, its id as search_library lists it; for a skill, its key; otherwise the number or the text.")]
    public string? Value { get; set; }

    [Description("How many, for an item added to the inventory; 1 when left out.")]
    public int? Quantity { get; set; }

    [Description("One short line on why, for the person to read.")]
    public string? Reason { get; set; }
}

public sealed class ChangesInput
{
    [JsonPropertyName("changes")]
    public List<ChangeInput>? Changes { get; set; }
}

/// <summary>One highlight as the model writes it in <c>highlight</c>.</summary>
public sealed class HighlightInput
{
    [Description("A key from sheet_fields.")]
    public string? Field { get; set; }

    [Description("Optional: an entry id from search_library, a skill key, or nothing to point at the field itself.")]
    public string? Value { get; set; }

    [Description("One short line on why it fits the character.")]
    public string? Reason { get; set; }
}

public sealed class HighlightsInput
{
    [JsonPropertyName("highlights")]
    public List<HighlightInput>? Highlights { get; set; }
}
