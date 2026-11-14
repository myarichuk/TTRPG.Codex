using Codex.Core.Rules;

namespace Codex.Web.Components.Shared;

/// <summary>An option of an entry choice as the reader sees it: the entry's name in their language, a small line of what it is (its level), and
/// where its Library card is (null when the host has none).</summary>
public sealed record EntryOptionInfo(string Name, string? Meta, string? Href);

/// <summary>
/// What the choice picker needs to show choices over Library entries and values (docs/ENTRY_CHOICES_DESIGN.md), which only the host knows: which
/// list keys hold Library entries and of what type, the type's names, how the system words its own values, and each option's name, level and
/// Library card. A picker given none shows such a choice as a plain list of what it offers.
/// </summary>
public sealed class EntryChoiceContext
{
    /// <summary>The entity type a list holds (<c>spell</c> for <c>grantedSpells</c>), or null when the list is not one of Library entries.</summary>
    public Func<string, string?> EntityTypeOf { get; init; } = _ => null;

    /// <summary>The names of an entity type, singular and plural ("Spell", "Spells"), or null when it has none.</summary>
    public Func<string, (string Singular, string Plural)?> TypeNames { get; init; } = _ => null;

    /// <summary>A value as the system words it (a class's name in the reader's language); the value itself when it has no translation. What is
    /// stored stays the canonical value.</summary>
    public Func<string, string> DisplayValue { get; init; } = value => value;

    /// <summary>An option of an entity type, by its id: its name, its level and its card. Null when the entry cannot be found.</summary>
    public Func<string, ChoiceOption, EntryOptionInfo?> Describe { get; init; } = (_, _) => null;
}
