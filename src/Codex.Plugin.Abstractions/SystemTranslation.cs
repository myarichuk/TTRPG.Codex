using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

/// <summary>
/// Display text for a system in one language (<c>system.&lt;locale&gt;.yaml</c> next to <c>system.yaml</c>). Everything is
/// keyed by the stable key the system already uses, and only display text appears here: keys, ids, dropdown values and
/// condition names are what the rules compare against, so they stay in the original language and are translated only
/// when shown (<see cref="SystemDefinition.Localize"/>, <see cref="SystemDefinition.DisplayOption"/>).
/// </summary>
public class SystemTranslation
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Compatibility { get; set; }

    public Dictionary<string, LabelTranslation> Attributes { get; set; } = new();
    public Dictionary<string, LabelTranslation> Resources { get; set; } = new();
    public Dictionary<string, LabelTranslation> ActorFields { get; set; } = new();
    public Dictionary<string, TypeTranslation> EntityTypes { get; set; } = new();

    /// <summary>Condition name as the rules know it -> name to show.</summary>
    public Dictionary<string, string> Conditions { get; set; } = new();

    /// <summary>Shared dropdown/tag values (schools, sizes, damage types...) -> text to show, for any field.</summary>
    public Dictionary<string, string> Values { get; set; } = new();
}

public class LabelTranslation
{
    public string? Label { get; set; }
    public string? Abbreviation { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }

    /// <summary>Dropdown value -> text to show, for this field only (wins over <see cref="SystemTranslation.Values"/>).</summary>
    public Dictionary<string, string> Options { get; set; } = new();
}

public class TypeTranslation
{
    public string? Label { get; set; }
    public string? PluralLabel { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, LabelTranslation> Fields { get; set; } = new();
}
