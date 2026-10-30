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

    /// <summary>Labels of the sheet rules (derived stats, pick lists, skills, slot pools...).</summary>
    public SheetRulesTranslation SheetRules { get; set; } = new();

    /// <summary>The word in "Hit Points (max)": what a resource's maximum field is called after its label. Default "max".</summary>
    public string? MaxSuffix { get; set; }

    /// <summary>Heading of a group of sheet fields (General, Combat, Attributes, Resources, Spellcasting...) -> heading to show. A field's own
    /// <c>category</c> translation wins; this one covers every field and rule that merely names the group.</summary>
    public Dictionary<string, string> Categories { get; set; } = new();

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

/// <summary>
/// Display labels for <see cref="Codex.Plugin.Abstractions.SheetRules"/>, each keyed by the rule's own <c>key</c>.
/// Only labels: formulas, conditions and messages are rules, not text for the reader.
/// </summary>
public class SheetRulesTranslation
{
    /// <summary>Derived stat key (proficiencyBonus, initiative) -> label.</summary>
    public Dictionary<string, string> Derived { get; set; } = new();

    /// <summary>Pick list key (spells, feats) -> label.</summary>
    public Dictionary<string, string> Lists { get; set; } = new();

    /// <summary>Skill key -> label.</summary>
    public Dictionary<string, string> Skills { get; set; } = new();

    /// <summary>Passive score key -> label.</summary>
    public Dictionary<string, string> Passives { get; set; } = new();

    /// <summary>Slot pool key -> label.</summary>
    public Dictionary<string, string> SlotPools { get; set; } = new();

    /// <summary>Pick list key -> the sentence under its label.</summary>
    public Dictionary<string, string> ListDescriptions { get; set; } = new();

    /// <summary>The <c>message</c> of a rule condition as system.yaml words it ("that is not a cantrip.") -> text to show.</summary>
    public Dictionary<string, string> Messages { get; set; } = new();

    /// <summary>Headings of whole rule groups.</summary>
    public string? BonusesLabel { get; set; }
    public string? IncreasesLabel { get; set; }
    public string? SkillsLabel { get; set; }
    public string? InventoryLabel { get; set; }
    public string? MulticlassLabel { get; set; }
}
