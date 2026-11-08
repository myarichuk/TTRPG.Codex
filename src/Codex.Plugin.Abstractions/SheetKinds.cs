using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

// The primitives of docs/SYSTEM_IMPLEMENTATION_OVERHAUL.md section 3, as system.yaml declares them. The vocabulary is in
// tests/Codex.Tests/Fixtures/reference/README.md. Shapes that YAML lets an author write two ways (a target or a list of
// targets, a type or { type, positiveOnly }) are read as object and normalised by the engine (Codex.Core.Rules.SheetModel).

/// <summary>A kind of sheet (<c>character</c>, <c>crew</c>): its values, modifiers, lists and keyed lists.</summary>
public sealed class SheetKindDefinition
{
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    /// <summary>The host role: <c>player</c>, <c>npc</c>, <c>creature</c> or <c>shared</c>.</summary>
    public string? Role { get; set; }

    public List<ValueDefinition> Values { get; set; } = new();
    public List<ModifierDefinition> Modifiers { get; set; } = new();
    public List<PickListDefinition> Lists { get; set; } = new();
    public List<KeyedListDefinition> KeyedLists { get; set; } = new();
}

/// <summary>A number (or a choice) on the sheet: worked out by <see cref="Formula"/>, or entered (<see cref="Input"/>).</summary>
public sealed class ValueDefinition
{
    public string Key { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Formula { get; set; }

    /// <summary>A target or a list of targets (<c>AC</c>, <c>'@saves'</c>) whose modifiers are added on top of the formula.</summary>
    public object? Modifiers { get; set; }

    /// <summary>Modifier types left out: a type name, or <c>{ type, positiveOnly: true }</c> to leave out only its bonuses.</summary>
    public List<object>? Without { get; set; }

    /// <summary>Entered by the player rather than worked out.</summary>
    public bool Input { get; set; }

    public List<string>? Options { get; set; }
    public object? Default { get; set; }
}

/// <summary>A typed contribution to a target: <c>{ target: AC, amount: 2, type: dodge, source: Dodge }</c>.</summary>
public sealed class ModifierDefinition
{
    /// <summary>A value key, a keyed-list entry (<c>skills.balance</c>) or a group (<c>'@armorPenalty'</c>).</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>A number or a formula.</summary>
    public string Amount { get; set; } = "0";

    /// <summary>The modifier type; none is <c>untyped</c>.</summary>
    public string? Type { get; set; }

    public string? Source { get; set; }

    /// <summary>A formula; the modifier counts only while it holds.</summary>
    public string? When { get; set; }
}

/// <summary>Picks of an entry type, each with its own fields.</summary>
public sealed class PickListDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    /// <summary>The entry type the picks are of; none for a list of plain records (<c>hpRolls</c>).</summary>
    public string? Type { get; set; }

    public string? Description { get; set; }
    public List<ItemFieldDefinition> ItemFields { get; set; } = new();

    /// <summary>Each pick's entry property holding modifiers, and when a pick's count (<c>"i => i.equipped"</c>).</summary>
    public ModifiersFromDefinition? ModifiersFrom { get; set; }

    /// <summary>The entry property holding prerequisites, checked on the sheet without the pick.</summary>
    public string? Prerequisites { get; set; }

    public List<SheetCheckDefinition> Rules { get; set; } = new();
}

public sealed class ModifiersFromDefinition
{
    public string Property { get; set; } = "modifiers";
    public string? When { get; set; }
}

/// <summary>A field each pick or keyed entry carries (<c>classLevel</c>, <c>equipped</c>, <c>points</c>).</summary>
public sealed class ItemFieldDefinition
{
    public string Key { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string Type { get; set; } = "Number";
    public object? Default { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public List<string>? Options { get; set; }
    public string? Display { get; set; }
}

/// <summary>A fixed set of keys (skills, actions), each with fields the player sets and values worked out per key.</summary>
public sealed class KeyedListDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public List<ItemFieldDefinition> ItemFields { get; set; } = new();
    public List<KeyedEntryDefinition> Keys { get; set; } = new();

    /// <summary>Per-key values as lambdas over the entry (<c>ranks: "s => s.points"</c>). <c>value</c> is the entry's own value,
    /// with the modifiers that target it added.</summary>
    public Dictionary<string, string> Derive { get; set; } = new();

    public List<SheetCheckDefinition> Rules { get; set; } = new();
}

public sealed class KeyedEntryDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    /// <summary>The attribute the entry reads, if any (formulas see it as <c>s.attribute</c>).</summary>
    public string? Attribute { get; set; }

    public string? Group { get; set; }
    public List<string>? Groups { get; set; }

    /// <summary>One entry per subject (<c>knowledge.arcana</c>) instead of one for the key.</summary>
    public string? Parameter { get; set; }

    public bool TrainedOnly { get; set; }
}

/// <summary>A problem with key <see cref="Key"/> when <see cref="Check"/> is false; a lambda checks every item.</summary>
public sealed class SheetCheckDefinition
{
    public string Check { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string? Message { get; set; }
}

/// <summary>Stacking: <c>sum</c>, <c>highest</c> or <c>lowest</c>, by modifier type; penalties (negative amounts) apart.</summary>
public sealed class ModifierTypesDefinition
{
    public string Default { get; set; } = "sum";
    public string Penalties { get; set; } = "sum";
    public Dictionary<string, string> Types { get; set; } = new();
}

/// <summary>A named function: a one-line <see cref="Formula"/> (<c>"(a, b) => a + b"</c>), or (later steps) a script or a
/// compiled type.</summary>
public sealed class FunctionDefinition
{
    public string? Formula { get; set; }
    public string? Script { get; set; }
    public string? Type { get; set; }
    public List<string>? Reads { get; set; }
}

public sealed class ConditionEffectDefinition
{
    public List<ModifierDefinition> Modifiers { get; set; } = new();
}
