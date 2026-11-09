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
    public List<TrackDefinition> Tracks { get; set; } = new();

    /// <summary>How the sheet is laid out: sections of widgets bound to keys. Empty: the host's generic layout (every value,
    /// track, list and keyed list in turn).</summary>
    public List<LayoutSection> Layout { get; set; } = new();
}

/// <summary>A section of a sheet's layout: a tab of its own, or with <see cref="Aside"/> a column shown beside every tab.</summary>
public sealed class LayoutSection
{
    /// <summary>The section's name, also its key (<c>Actions</c>, <c>attributes</c>).</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>What the tab says; none is the host's word for a section it knows, else <see cref="Section"/>.</summary>
    public string? Label { get; set; }

    /// <summary>A Lucide icon name for the tab.</summary>
    public string? Icon { get; set; }

    /// <summary>Shown beside every tab rather than as a tab.</summary>
    public bool Aside { get; set; }

    public List<LayoutWidget> Widgets { get; set; } = new();
}

/// <summary>
/// One widget in a section. Generic widgets: <c>values</c> (<see cref="Keys"/>), <c>track</c> (<see cref="Key"/>, which may end
/// in <c>*</c> for every generated track it starts), <c>list</c> and <c>table</c> (<see cref="Key"/>, <see cref="Columns"/>,
/// <see cref="GroupBy"/>), <c>roll</c> (<see cref="Roll"/>), <c>text</c> (<see cref="Key"/>) and <c>modifiers</c>
/// (<see cref="Target"/>). Any other name asks for a widget the host builds in (5e's <c>armor-class</c>, <c>hit-points</c>...).
/// </summary>
public sealed class LayoutWidget
{
    public string Widget { get; set; } = string.Empty;
    public string? Key { get; set; }
    public List<string> Keys { get; set; } = new();
    public List<string> Columns { get; set; } = new();
    public string? GroupBy { get; set; }
    public string? Roll { get; set; }
    public string? Target { get; set; }
    public string? Label { get; set; }
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

/// <summary>
/// A resource with a current value (overhaul section 3): hit points, stress, armor per location, spell slots. Its current value
/// is a number on the sheet under <see cref="Key"/>, or for a slot track the list of what its slots hold. Formulas read the
/// current value by the key and the maximum as <c>{key}_Max</c>. <see cref="Overflow"/>, <see cref="WhenFull"/> and
/// <see cref="Beyond"/> act when the track changes (Codex.Core.Rules.SheetIntents).
/// </summary>
public sealed class TrackDefinition
{
    /// <summary>The key, or for <see cref="Each"/> a template: <c>"slots_{item.id}_{level}"</c>.</summary>
    public string Key { get; set; } = string.Empty;
    public string? Label { get; set; }

    /// <summary>A number or a formula; none for an open-ended track (improvement points).</summary>
    public string? Max { get; set; }

    public string? Min { get; set; }

    /// <summary>Where a new sheet starts: a number, or <c>max</c>.</summary>
    public string? Start { get; set; }

    /// <summary><c>number</c> (default), <c>boxes</c> or <c>slots</c>.</summary>
    public string Display { get; set; } = "number";

    /// <summary>Each adds its condition while it holds; several can hold at once.</summary>
    public List<TrackThresholdDefinition> Thresholds { get; set; } = new();

    /// <summary>The first that matches adds its condition; exactly one holds at a time.</summary>
    public List<TrackThresholdDefinition> Bands { get; set; } = new();

    /// <summary>For a slot track, what a slot may hold.</summary>
    public TrackHoldsDefinition? Holds { get; set; }

    /// <summary>With <see cref="Each"/>, the levels to make a track for: <c>"0..9"</c>. On a slot track, its levels of slots.</summary>
    public object? Levels { get; set; }

    /// <summary>A list formula: one track per item (and per level), its formulas reading <c>item</c> and <c>level</c>.</summary>
    public string? Each { get; set; }

    /// <summary>What a rise past the maximum does: stress into trauma, heat into wanted level. Null: the track stops at its
    /// maximum.</summary>
    public TrackOverflowDefinition? Overflow { get; set; }

    /// <summary>For slots in levels: <c>pushUp</c>, a full level puts what it is given in the next level up. Null: a full level
    /// takes nothing more.</summary>
    public string? WhenFull { get; set; }

    /// <summary>For slots in levels: what pushing past the last level brings (Blades' fatal harm).</summary>
    public TrackBeyondDefinition? Beyond { get; set; }
}

/// <summary><c>{ into, by, then, ask }</c>: a rise past the maximum adds <see cref="By"/> to <see cref="Into"/>, and the track
/// is then <c>reset</c> to its minimum (stress), <c>carry</c>-s what went past (heat), or <c>keep</c>-s its maximum.</summary>
public sealed class TrackOverflowDefinition
{
    public string Into { get; set; } = string.Empty;

    /// <summary>A number or a formula (reading <c>excess</c>, how far past the maximum); 1 when unset.</summary>
    public string? By { get; set; }

    /// <summary><c>reset</c>, <c>carry</c> or <c>keep</c>.</summary>
    public string Then { get; set; } = "keep";

    /// <summary>A choice the host asks for once it overflowed (which trauma).</summary>
    public TrackAskDefinition? Ask { get; set; }
}

public sealed class TrackAskDefinition
{
    /// <summary><c>player</c> or <c>gm</c>.</summary>
    public string Who { get; set; } = "player";

    public List<string> Choices { get; set; } = new();
}

public sealed class TrackBeyondDefinition
{
    public string? Condition { get; set; }
}

/// <summary>A comparison of the track's current value with a number or formula, and the condition it brings. No comparison
/// matches always (a band's last, catch-all line).</summary>
public sealed class TrackThresholdDefinition
{
    public string? AtOrBelow { get; set; }
    public string? Below { get; set; }
    public string? AtOrAbove { get; set; }
    public string? Above { get; set; }

    /// <summary>Equal to (YAML <c>equals</c>; named so as not to hide <see cref="object.Equals(object)"/> for callers).</summary>
    public new string? Equals { get; set; }

    public string? Condition { get; set; }
}

/// <summary>What a slot holds: entries of <see cref="Type"/> (a spell id) that pass <see cref="Where"/>, or <c>text</c>.</summary>
public sealed class TrackHoldsDefinition
{
    public string Type { get; set; } = "text";
    public string? Where { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// What a system lets a campaign keep beside its sheets (overhaul section 3, "Campaign objects"): clocks, and tables of named
/// rows with fields (Blades' faction status). The campaign holds the state; this says what is allowed.
/// </summary>
public sealed class CampaignObjectsDefinition
{
    /// <summary>Clocks: null when the system has none.</summary>
    public ClockRulesDefinition? Clocks { get; set; }

    public List<CampaignTableDefinition> Tables { get; set; } = new();
}

/// <summary>The clocks a campaign may keep: the sizes allowed (empty: any size from 2), and who may see one.</summary>
public sealed class ClockRulesDefinition
{
    public List<int> Segments { get; set; } = new();

    /// <summary><c>gm</c> (the GM alone) and <c>players</c> (everyone at the table); the first is a new clock's.</summary>
    public List<string> Visibility { get; set; } = new() { "gm", "players" };
}

/// <summary>A table of named rows, each with the <see cref="ItemFields"/> (status from -3 to 3). Who may see it is the table's:
/// <c>players</c> (the default) or <c>gm</c>.</summary>
public sealed class CampaignTableDefinition
{
    public string Key { get; set; } = string.Empty;
    public string? Label { get; set; }
    public List<ItemFieldDefinition> ItemFields { get; set; } = new();
    public string Visibility { get; set; } = "players";
}
