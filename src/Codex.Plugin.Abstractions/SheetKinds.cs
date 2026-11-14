using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

// The primitives of docs/SYSTEM_IMPLEMENTATION_OVERHAUL.md section 3, as system.yaml declares them. The vocabulary is in
// tests/Codex.Tests/Fixtures/reference/README.md. Shapes that YAML lets an author write two ways (a target or a list of
// targets, a type or { type, positiveOnly }) are read as object and normalised by the engine (Codex.Core.Rules.SheetModel).

/// <summary>A kind of sheet (<c>character</c>, <c>crew</c>): its values, modifiers, lists and keyed lists.</summary>
public sealed class SheetKindDefinition
{
    /// <summary>Required: the kind's key (<c>character</c>, <c>crew</c>), which each sheet of it records.</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>The kind's name as the app shows it (where a new sheet's kind is chosen); <c>kind</c> when empty.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The host role: <c>player</c>, <c>npc</c>, <c>creature</c> or <c>shared</c>.</summary>
    public string? Role { get; set; }

    /// <summary>The sheet's numbers and choices, worked out or entered. Their order does not matter: each is worked out when
    /// first read.</summary>
    public List<ValueDefinition> Values { get; set; } = new();
    /// <summary>Modifiers every sheet of the kind has, beside those from picks and conditions.</summary>
    public List<ModifierDefinition> Modifiers { get; set; } = new();
    /// <summary>Lists of picks (classes, feats, items), each with fields of its own per pick.</summary>
    public List<PickListDefinition> Lists { get; set; } = new();
    /// <summary>Where the kind's sheets get features (special abilities, upgrades) from, as <c>sheetRules.features</c> does for a system
    /// with sheet rules; on a sheet kind a source names a <c>list</c> of the kind's <c>lists</c> and nothing else (no <c>field</c>, no
    /// <c>byField</c>). Each pick's own properties carry the grants: <c>modifiers</c> (targets are the kind's values, keyed entries or
    /// groups), <c>uses</c> and <c>spends</c> (counters), <c>options</c> with <c>choose</c>, and <c>adds</c> with a <c>choice</c> over a
    /// keyed list. A list that is a source may not also have <c>modifiersFrom</c>, or its modifiers would count twice.</summary>
    public List<FeatureSource> Features { get; set; } = new();

    /// <summary>Fixed sets of keys (skills, actions), each key with its own fields and worked-out values.</summary>
    public List<KeyedListDefinition> KeyedLists { get; set; } = new();
    /// <summary>Values that go up and down in play (hit points, stress, slots), with their bounds and what they bring.</summary>
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

    /// <summary>The section's widgets, top to bottom; a section needs at least one.</summary>
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
    /// <summary>Required: the widget's name: a generic widget (listed with this type), a widget the host builds in, or one of
    /// the system's own <c>widgets</c>.</summary>
    public string Widget { get; set; } = string.Empty;
    /// <summary>The key a <c>track</c>, <c>list</c>, <c>table</c> or <c>text</c> widget shows; for a <c>track</c>, a prefix
    /// ending in <c>*</c> shows every generated track it starts.</summary>
    public string? Key { get; set; }
    /// <summary>The keys a <c>values</c> widget shows: values, attributes, <c>{track}_Max</c> or a keyed-list entry
    /// (<c>list.id</c>).</summary>
    public List<string> Keys { get; set; } = new();
    /// <summary>For <c>list</c> and <c>table</c>: the columns after each row's name, as per-pick fields (editable in place),
    /// entry properties or derived values.</summary>
    public List<string> Columns { get; set; } = new();
    /// <summary>For <c>list</c> and <c>table</c>: the property rows are grouped under headings by (<c>group</c>). None: no
    /// headings.</summary>
    public string? GroupBy { get; set; }
    /// <summary>For <c>roll</c>: the key of the roll in <c>rolls</c> its button makes.</summary>
    public string? Roll { get; set; }
    /// <summary>For <c>modifiers</c>: the target whose modifiers it lists, each with whether it counted.</summary>
    public string? Target { get; set; }
    /// <summary>The text a <c>roll</c> button or a <c>text</c> field shows, or the label handed to one of the system's own
    /// widgets, in place of its default.</summary>
    public string? Label { get; set; }
}

/// <summary>A number (or a choice) on the sheet: worked out by <see cref="Formula"/>, or entered (<see cref="Input"/>).</summary>
public sealed class ValueDefinition
{
    /// <summary>Required: the name the sheet and formulas know the value by.</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>The value's name as the sheet shows it; <c>key</c> when unset.</summary>
    public string? Label { get; set; }
    /// <summary>How the value is worked out, over the sheet's values, attributes, lists and functions. None: 0 (or, with
    /// <c>input</c>, what was entered).</summary>
    public string? Formula { get; set; }

    /// <summary>A target or a list of targets (<c>AC</c>, <c>'@saves'</c>) whose modifiers are added on top of the formula.</summary>
    public object? Modifiers { get; set; }

    /// <summary>Modifier types left out: a type name, or <c>{ type, positiveOnly: true }</c> to leave out only its bonuses.</summary>
    public List<object>? Without { get; set; }

    /// <summary>Entered by the player rather than worked out.</summary>
    public bool Input { get; set; }

    /// <summary>For an <c>input</c> value: the choices the sheet offers in a dropdown. Not checked: a value set another way is
    /// kept.</summary>
    public List<string>? Options { get; set; }
    /// <summary>For an <c>input</c> value: its value until the player sets one.</summary>
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

    /// <summary>Where the modifier comes from, as the <c>modifiers</c> widget shows it. A condition's modifier without one is
    /// credited to the condition, a pick's to the entry.</summary>
    public string? Source { get; set; }

    /// <summary>A formula; the modifier counts only while it holds.</summary>
    public string? When { get; set; }

    /// <summary>Makes the modifier a tick box: it never counts on the sheet, and counts in a roll that reads its target only when the
    /// player ticked it for that roll (docs/ROLL_BONUSES_DESIGN.md).</summary>
    public OptInDefinition? OptIn { get; set; }
}

/// <summary>What makes a modifier a tick box: <c>optIn: { label, spends: { key, amount }, ends }</c>.</summary>
public sealed class OptInDefinition
{
    /// <summary>What the tick box says. None: the modifier's source.</summary>
    public string? Label { get; set; }

    /// <summary>A pool the roll spends from when this is ticked (a <c>uses_*</c> counter's key or a resource).</summary>
    public OptInSpendDefinition? Spends { get; set; }

    /// <summary>Only on a condition's effects: rolling with it ticked removes the condition.</summary>
    public bool Ends { get; set; }
}

/// <summary>What a ticked opt-in spends: <c>{ key, amount }</c>. The amount is a whole number (default 1).</summary>
public sealed class OptInSpendDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Amount { get; set; } = "1";
}

/// <summary>Picks of an entry type, each with its own fields.</summary>
public sealed class PickListDefinition
{
    /// <summary>Required: the sheet key the picks are stored under, and the name formulas and layouts use for the list.</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>The list's name as the sheet shows it.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The entry type the picks are of; none for a list of plain records (<c>hpRolls</c>).</summary>
    public string? Type { get; set; }

    /// <summary>A line about the list. Reserved; not read by this version.</summary>
    public string? Description { get; set; }
    /// <summary>The fields each pick carries besides its entry (<c>classLevel</c>, <c>equipped</c>).</summary>
    public List<ItemFieldDefinition> ItemFields { get; set; } = new();

    /// <summary>Each pick's entry property holding modifiers, and when a pick's count (<c>"i => i.equipped"</c>).</summary>
    public ModifiersFromDefinition? ModifiersFrom { get; set; }

    /// <summary>The entry property holding prerequisites, checked on the sheet without the pick.</summary>
    public string? Prerequisites { get; set; }

    /// <summary>Checks on the list; one with a lambda (<c>"c => ..."</c>) checks every pick.</summary>
    public List<SheetCheckDefinition> Rules { get; set; } = new();
}

/// <summary>Where a list's picks carry modifiers (<c>modifiersFrom</c>): each pick's entry property holding a list of <c>{
/// target, amount, type, source, when }</c>.</summary>
public sealed class ModifiersFromDefinition
{
    /// <summary>The entry property holding the modifiers. Default <c>modifiers</c>.</summary>
    public string Property { get; set; } = "modifiers";
    /// <summary>A lambda over the pick (<c>"i => i.equipped"</c>): only picks it holds for bring their modifiers. None: every
    /// pick does.</summary>
    public string? When { get; set; }
}

/// <summary>A field each pick or keyed entry carries (<c>classLevel</c>, <c>equipped</c>, <c>points</c>).</summary>
public sealed class ItemFieldDefinition
{
    /// <summary>Required: the field's name, as formulas read it on the pick or row (<c>item.classLevel</c>).</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>The column heading; the key when unset.</summary>
    public string? Label { get; set; }
    /// <summary><c>Number</c> (the default), <c>Boolean</c> (a checkbox), or anything else for text.</summary>
    public string Type { get; set; } = "Number";
    /// <summary>The value a new pick, keyed entry or row has (none: 0 for a number, false for a flag).</summary>
    public object? Default { get; set; }
    /// <summary>Lower bound for a number: an intent's change stops at it, and a campaign table reports a value below it.</summary>
    public double? Min { get; set; }
    /// <summary>Upper bound for a number: an intent's change stops at it, and a campaign table reports a value above it.</summary>
    public double? Max { get; set; }
    /// <summary>The values a text field may take. Not checked; this version uses them only to type the field in the generated
    /// <c>codex.d.ts</c>.</summary>
    public List<string>? Options { get; set; }
    /// <summary>How the field is drawn (<c>dots</c>). Reserved; not read by this version.</summary>
    public string? Display { get; set; }
}

/// <summary>A fixed set of keys (skills, actions), each with fields the player sets and values worked out per key.</summary>
public sealed class KeyedListDefinition
{
    /// <summary>Required: the sheet key the entries are stored under, and the list's name in formulas (<c>skills.balance</c>)
    /// and layouts.</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>The list's name as the sheet shows it.</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>The fields the player sets on each entry (<c>points</c>, <c>rating</c>).</summary>
    public List<ItemFieldDefinition> ItemFields { get; set; } = new();
    /// <summary>The fixed entries the list has.</summary>
    public List<KeyedEntryDefinition> Keys { get; set; } = new();

    /// <summary>Per-key values as lambdas over the entry (<c>ranks: "s => s.points"</c>). <c>value</c> is the entry's own value,
    /// with the modifiers that target it added.</summary>
    public Dictionary<string, string> Derive { get; set; } = new();

    /// <summary>Checks on the list; one with a lambda (<c>"s => ..."</c>) checks every entry.</summary>
    public List<SheetCheckDefinition> Rules { get; set; } = new();
}

/// <summary>One entry of a keyed list (a skill, an action): its key, label, attribute and groups.</summary>
public sealed class KeyedEntryDefinition
{
    /// <summary>Required: the entry's key (<c>balance</c>), as formulas reach it (<c>skills.balance</c>).</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>The entry's name as the sheet shows it.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The attribute the entry reads, if any (formulas see it as <c>s.attribute</c>).</summary>
    public string? Attribute { get; set; }

    /// <summary>A group the entry belongs to: formulas read it as <c>group</c>, a modifier on <c>'@group'</c> reaches the
    /// entry, and a table can group rows by it.</summary>
    public string? Group { get; set; }
    /// <summary>More groups, as <c>group</c> (formulas read all of them as <c>groups</c>).</summary>
    public List<string>? Groups { get; set; }

    /// <summary>One entry per subject (<c>knowledge.arcana</c>) instead of one for the key.</summary>
    public string? Parameter { get; set; }

    /// <summary>A flag formulas read as <c>trainedOnly</c> (3.5's trained-only skills); nothing else acts on it.</summary>
    public bool TrainedOnly { get; set; }
}

/// <summary>A problem with key <see cref="Key"/> when <see cref="Check"/> is false; a lambda checks every item.</summary>
public sealed class SheetCheckDefinition
{
    /// <summary>Required: a formula that must be true; a lambda (<c>"s => s.ranks &lt;= s.maxRanks"</c>) must be true for every
    /// item of the list.</summary>
    public string Check { get; set; } = string.Empty;
    /// <summary>Required: the problem's translation key (<c>Actor.Rule.RankMax</c>); also its text when there is no
    /// <c>message</c>.</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>The problem as shown, with <c>{name}</c> filled in from the item or the sheet's values (<c>{label}</c>,
    /// <c>{maxRanks}</c>).</summary>
    public string? Message { get; set; }
}

/// <summary>Stacking by modifier type: the modifiers of a type form a group that keeps one member or all. Bonus rules add what they
/// keep to the value: <c>sum</c> (every one), <c>highest</c> / <c>lowest</c> (the largest or smallest bonus; penalties apart).
/// Value rules compete with the value's starting amount and replace it when they win: <c>max</c> (the greatest, a floor; <c>best</c>
/// is its older name) and <c>min</c> (the least, a ceiling). Extra Attack 2, 3, 4 on 1 attack under <c>max</c> is 4; critical
/// 19 and 18 on a start of 20 under <c>min</c> is 18; +1 and +2 status under <c>highest</c> plus +1 untyped on 10 is 13.</summary>
public sealed class ModifierTypesDefinition
{
    /// <summary>The rule for a type <c>types</c> does not name: <c>sum</c> (the default), <c>highest</c>, <c>lowest</c>,
    /// <c>max</c>, <c>min</c> or <c>best</c>.</summary>
    public string Default { get; set; } = "sum";
    /// <summary>The rule for the penalties (negative amounts) of one type, whatever the type's own rule, except a <c>best</c>
    /// type. Default <c>sum</c>.</summary>
    public string Penalties { get; set; } = "sum";
    /// <summary>Modifier type -> its rule (<c>dodge: sum</c>, <c>armor: highest</c>, <c>base: max</c>, <c>critFrom: min</c>).</summary>
    public Dictionary<string, string> Types { get; set; } = new();
}

/// <summary>A named function: a one-line <see cref="Formula"/> (<c>"(a, b) => a + b"</c>), a <see cref="Script"/> export
/// (<c>rules.js#hitPoints</c>), or a compiled <see cref="Type"/> (<c>"Acme.Street.HitPoints, Acme.Street"</c>, an
/// <see cref="ICodexFunction"/>). One of them.</summary>
public sealed class FunctionDefinition
{
    /// <summary>A one-line lambda (<c>"(a, b) => a + b"</c>), up to four parameters.</summary>
    public string? Formula { get; set; }
    /// <summary>A script export, <c>file#export</c> (<c>rules.js#hitPoints</c>), read from the system's folder.</summary>
    public string? Script { get; set; }
    /// <summary>A compiled type, <c>"Namespace.Type, Assembly"</c>, from a DLL beside system.yaml.</summary>
    public string? Type { get; set; }
    /// <summary>For a script function, the parameters it takes; only their count is used, and only when the script's own
    /// parameters cannot be read.</summary>
    public List<string>? Reads { get; set; }
}

/// <summary>What a condition does while it holds (<c>conditionEffects.&lt;name&gt;</c>).</summary>
public sealed class ConditionEffectDefinition
{
    /// <summary>Modifiers that apply while the condition holds; one without a <c>source</c> is credited to the condition.</summary>
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
    /// <summary>The track's name as the sheet shows it; the key when unset.</summary>
    public string? Label { get; set; }

    /// <summary>A number or a formula; none for an open-ended track (improvement points).</summary>
    public string? Max { get; set; }

    /// <summary>The lowest the current value may be: a number or a formula. Default 0; it may be negative (3.5's -10).</summary>
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
    /// <summary>Required: the track the overflow adds to (stress into <c>trauma</c>).</summary>
    public string Into { get; set; } = string.Empty;

    /// <summary>A number or a formula (reading <c>excess</c>, how far past the maximum); 1 when unset.</summary>
    public string? By { get; set; }

    /// <summary><c>reset</c>, <c>carry</c> or <c>keep</c>.</summary>
    public string Then { get; set; } = "keep";

    /// <summary>A choice the host asks for once it overflowed (which trauma).</summary>
    public TrackAskDefinition? Ask { get; set; }
}

/// <summary>A choice the host asks for after an overflow (<c>{ who, choices }</c>): which trauma, say.</summary>
public sealed class TrackAskDefinition
{
    /// <summary><c>player</c> or <c>gm</c>.</summary>
    public string Who { get; set; } = "player";

    /// <summary>The options offered.</summary>
    public List<string> Choices { get; set; } = new();
}

/// <summary>What pushing a slot track past its last level brings (<c>beyond</c>).</summary>
public sealed class TrackBeyondDefinition
{
    /// <summary>The condition put on the sheet (Blades' fatal harm: <c>Dead</c>).</summary>
    public string? Condition { get; set; }
}

/// <summary>A comparison of the track's current value with a number or formula, and the condition it brings. No comparison
/// matches always (a band's last, catch-all line).</summary>
public sealed class TrackThresholdDefinition
{
    /// <summary>Holds when the current value is at or below this number or formula.</summary>
    public string? AtOrBelow { get; set; }
    /// <summary>Holds when the current value is below this number or formula.</summary>
    public string? Below { get; set; }
    /// <summary>Holds when the current value is at or above this number or formula.</summary>
    public string? AtOrAbove { get; set; }
    /// <summary>Holds when the current value is above this number or formula.</summary>
    public string? Above { get; set; }

    /// <summary>Equal to (YAML <c>equals</c>; named so as not to hide <see cref="object.Equals(object)"/> for callers).</summary>
    public new string? Equals { get; set; }

    /// <summary>The condition the line brings while it holds, with its <c>conditionEffects</c>.</summary>
    public string? Condition { get; set; }
}

/// <summary>What a slot holds: entries of <see cref="Type"/> (a spell id) that pass <see cref="Where"/>, or <c>text</c>.</summary>
public sealed class TrackHoldsDefinition
{
    /// <summary>The entity type a slot holds (<c>spell</c>), checked against the Library, or <c>text</c> (the default) for free
    /// text, which is not checked.</summary>
    public string Type { get; set; } = "text";
    /// <summary>A lambda over the held entry that must be true (<c>"s => levelFor(s, item.name) == level"</c>). None: any entry
    /// of the type.</summary>
    public string? Where { get; set; }
    /// <summary>The problem shown for something the slots cannot hold, with <c>{item.&lt;property&gt;}</c> and <c>{level}</c>
    /// filled in.</summary>
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

    /// <summary>Tables of named rows a campaign may keep (faction status).</summary>
    public List<CampaignTableDefinition> Tables { get; set; } = new();
}

/// <summary>The clocks a campaign may keep: the sizes allowed (empty: any size from 2), and who may see one.</summary>
public sealed class ClockRulesDefinition
{
    /// <summary>The clock sizes allowed (<c>[4, 6, 8]</c>), each at least 2. Empty: any size from 2.</summary>
    public List<int> Segments { get; set; } = new();

    /// <summary><c>gm</c> (the GM alone) and <c>players</c> (everyone at the table); the first is a new clock's.</summary>
    public List<string> Visibility { get; set; } = new() { "gm", "players" };
}

/// <summary>A table of named rows, each with the <see cref="ItemFields"/> (status from -3 to 3). Who may see it is the table's:
/// <c>players</c> (the default) or <c>gm</c>.</summary>
public sealed class CampaignTableDefinition
{
    /// <summary>Required, unique: the table's name, as intents (<c>table: factions</c>) and handler targets
    /// (<c>campaign.factions</c>) name it; not <c>clocks</c>.</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>The table's heading; the key when unset.</summary>
    public string? Label { get; set; }
    /// <summary>The fields each row has besides its name; a row's name is its own, so no field is called <c>name</c>.</summary>
    public List<ItemFieldDefinition> ItemFields { get; set; } = new();
    /// <summary>Who sees the table: <c>players</c> (everyone at the table, the default) or <c>gm</c>.</summary>
    public string Visibility { get; set; } = "players";
}

/// <summary>A widget a DLL supplies: <see cref="Type"/> names an <see cref="ICodexWidget"/> (<c>"Acme.Street.Cyberware,
/// Acme.Street"</c>).</summary>
public sealed class WidgetDefinition
{
    /// <summary>The compiled type, <c>"Namespace.Type, Assembly"</c>, an <c>ICodexWidget</c> in a DLL beside system.yaml.</summary>
    public string? Type { get; set; }
    /// <summary>The label handed to the widget when its layout entry gives none.</summary>
    public string? Label { get; set; }
}
