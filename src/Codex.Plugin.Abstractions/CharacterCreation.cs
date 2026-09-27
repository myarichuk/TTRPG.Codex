using System.Collections.Generic;
using System.Linq;

namespace Codex.Plugin.Abstractions;

/// <summary>How a system's Equipment creation step collects gear.</summary>
public enum CreationEquipmentMode
{
    /// <summary>D&amp;D-5e-style: fixed grants plus choose-N option groups read from the
    /// picked class entry's <c>startingEquipment</c>/<c>startingEquipmentOptions</c>
    /// properties.</summary>
    ClassOptions,

    /// <summary>PF2e-style: free search over the <c>equipment</c> rules kind; the player
    /// adds whatever they want.</summary>
    SearchAdd,
}

/// <summary>
/// One step in a system's guided character-creation flow. The web wizard renders a small
/// library of step shapes keyed by <see cref="Key"/> (<c>Name</c>, <c>Class</c>,
/// <c>Subclass</c>, <c>Race</c>, <c>Background</c>, <c>Pf2eClass</c>, <c>Scores</c>,
/// <c>Equipment</c>, <c>Spells</c>, <c>Review</c>, or <c>Pick:&lt;kind&gt;</c> for a fully
/// generic card picker); the plugin composes those shapes with its own rules-entry kinds
/// rather than the wizard branching on the system id. A future system whose flow is a new
/// combination of existing shapes needs no wizard code change - only a definition here
/// (and YAML kinds to pick from). Only a genuinely new step shape needs new markup.
/// </summary>
public record CharacterCreationStep(
    string Key,
    string Title,
    string? Kind = null,
    string? SameKindChildLink = null,
    string? ChildKind = null,
    string? ChildLinkKey = null,
    string? ChildVisibleWhenPick = null);

/// <summary>
/// A system's guided character-creation flow: which steps run, which YAML rules-entry
/// kinds each picker step queries, how scores/equipment/spells resolve. Returned by
/// <see cref="ICodexSystemPlugin.GetCharacterCreation"/>; null means "no custom flow" and
/// the wizard falls back to <see cref="CharacterCreationFallback"/> built from whatever
/// rules kinds the system's packs actually contain.
/// </summary>
public record CharacterCreationDefinition(
    IReadOnlyList<CharacterCreationStep> Steps,
    IReadOnlyList<string>? AbilityScores = null,
    CreationEquipmentMode EquipmentMode = CreationEquipmentMode.SearchAdd,
    bool SpellsFromClassLists = false);

/// <summary>
/// Generic creation flow for a system with no custom definition: one card picker per
/// rules-entry kind found in YAML, plus name/scores/equipment/review. This is what makes
/// a YAML-only system creatable without touching the wizard.
/// </summary>
public static class CharacterCreationFallback
{
    public static readonly IReadOnlyList<string> DefaultAbilityScores =
        new[] { "Strength", "Dexterity", "Constitution", "Intelligence", "Wisdom", "Charisma" };

    /// <summary>Builds a generic flow from the distinct rules kinds present, or null when
    /// there is nothing to pick (creation stays unavailable for content-less systems).</summary>
    public static CharacterCreationDefinition? Build(IEnumerable<string> kinds)
    {
        var ordered = kinds
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        if (ordered.Count == 0)
        {
            return null;
        }

        var steps = new List<CharacterCreationStep> { new("Name", "Name") };
        steps.AddRange(ordered.Select(kind =>
            new CharacterCreationStep($"Pick:{kind}", Capitalize(kind), Kind: kind)));
        steps.Add(new CharacterCreationStep("Scores", "Scores"));
        steps.Add(new CharacterCreationStep("Equipment", "Equipment"));
        steps.Add(new CharacterCreationStep("Review", "Review"));

        return new CharacterCreationDefinition(steps, DefaultAbilityScores,
            CreationEquipmentMode.SearchAdd, SpellsFromClassLists: false);
    }

    private static string Capitalize(string kind) =>
        string.IsNullOrEmpty(kind) ? kind : char.ToUpperInvariant(kind[0]) + kind[1..];
}
