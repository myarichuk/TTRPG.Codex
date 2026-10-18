using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

/// <summary>
/// The rules a system applies to a character sheet, declared in <c>system.yaml</c> so a homebrew
/// system can have its own (or none). Everything here is evaluated by the core's sheet rule engine
/// against the sheet's field values and the content entries they reference; nothing is specific
/// to any one game.
/// </summary>
public class SheetRules
{
    /// <summary>"strict": players can't save a sheet that breaks a rule (the DM always can);
    /// "warn": violations are shown but saving is allowed; "off": rules are not evaluated.
    /// A campaign can override this (house rules).</summary>
    public string Enforcement { get; set; } = "strict";

    public AttributeRules? Attributes { get; set; }

    /// <summary>Read-only fields computed from the sheet (proficiency bonus from level, ...).</summary>
    public List<DerivedField> Derived { get; set; } = new();

    /// <summary>Conditions a single-pick reference field's chosen entry must satisfy.</summary>
    public List<FieldConstraint> Constraints { get; set; } = new();

    /// <summary>Multi-pick lists of entries on the sheet (spells, feats, knacks...).</summary>
    public List<ListRule> Lists { get; set; } = new();

    /// <summary>Levels split across more than one class. Null: one class only.</summary>
    public MulticlassRules? Multiclass { get; set; }

    /// <summary>Expendable slot pools (spell slots, pact slots...) computed from class tables and
    /// tracked during play as resources.</summary>
    public List<SlotPoolRule> SlotPools { get; set; } = new();
}

/// <summary>How ability scores are generated and adjusted.</summary>
public class AttributeRules
{
    /// <summary>Methods a player may choose from: "point-buy", "standard-array", "manual". The DM can always use manual.</summary>
    public List<string> Methods { get; set; } = new() { "manual" };

    public string Default { get; set; } = "manual";

    /// <summary>Upper bound on a final score (base plus bonuses).</summary>
    public int? Max { get; set; }

    public PointBuyRules? PointBuy { get; set; }

    /// <summary>The fixed set of scores to assign, one per attribute, for "standard-array".</summary>
    public List<int> StandardArray { get; set; } = new();

    /// <summary>Bonuses granted by a referenced entry (a background's "+2/+1 among these abilities").</summary>
    public BonusRules? Bonuses { get; set; }

    /// <summary>Score increases granted by entries picked in a list (an Ability Score Improvement feat).</summary>
    public IncreaseRules? Increases { get; set; }
}

/// <summary>
/// Score increases that come from picks in a <see cref="ListRule"/>: each picked entry whose
/// <see cref="Property"/> is a positive number grants that many points, spread by the player over
/// the attributes its <see cref="OptionsProperty"/> lists (any attribute when it lists none), and
/// can raise a score no higher than its <see cref="CapProperty"/> (or <see cref="AttributeRules.Max"/>).
/// The player's choice is stored on the pick itself: <c>"feat-id|str:1,dex:1"</c>.
/// </summary>
public class IncreaseRules
{
    public string Label { get; set; } = "Score increases";

    /// <summary>The <see cref="ListRule.Key"/> whose picks can grant increases.</summary>
    public string FromList { get; set; } = string.Empty;

    /// <summary>Entry property holding how many points the entry grants.</summary>
    public string Property { get; set; } = "abilityIncrease";

    /// <summary>Entry property listing the attributes (keys, abbreviations or labels) the points may go to.</summary>
    public string OptionsProperty { get; set; } = "abilityIncreaseOptions";

    /// <summary>Entry property holding the highest score the increase may reach.</summary>
    public string CapProperty { get; set; } = "abilityIncreaseCap";
}

/// <summary>
/// Multiclassing: the sheet's <see cref="ClassField"/> is the first class, <see cref="Key"/> holds
/// the others as <c>"class-id:levels"</c>, and <see cref="LevelField"/> stays the total character
/// level. Every class gets its own <see cref="ClassLevelKey"/> value, so table lookups written with
/// <c>byField: classLevel</c> read the right row for each class; list limits are summed across
/// classes and a list entry is allowed when any one class allows it.
/// </summary>
public class MulticlassRules
{
    public string Key { get; set; } = "multiclass";
    public string Label { get; set; } = "Additional classes";
    public string ClassField { get; set; } = "class";
    public string LevelField { get; set; } = "level";
    public string ClassLevelKey { get; set; } = "classLevel";

    /// <summary>Class entry property listing prerequisite options, each like <c>"STR 13+"</c> or
    /// <c>"DEX 13+, WIS 13+"</c> (all of these). Any one option is enough.</summary>
    public string PrerequisitesProperty { get; set; } = "multiclassPrerequisites";

    /// <summary>Whether the first class must also meet its own prerequisites once you multiclass.</summary>
    public bool RequireForFirstClass { get; set; } = true;
}

/// <summary>
/// One pool of expendable slots. For each of the sheet's classes, the class entry's
/// <see cref="Table"/> row at that class's level supplies <see cref="Column"/>: either
/// <c>"4/3/2"</c> (slots per slot level from 1 up) or, when <see cref="SlotLevelColumn"/> is set, a
/// count of slots that are all of the level in that column. Classes are filtered by their
/// <see cref="CasterTypeProperty"/>. When two or more classes contribute and <see cref="Combine"/> is
/// set, their weighted levels are added up and looked up in the combined table instead.
/// During play each slot level is a resource: <c>{Key}_{level}</c> (remaining) and
/// <c>{Key}_{level}_Max</c>.
/// </summary>
public class SlotPoolRule
{
    public string Key { get; set; } = "slots";
    public string Label { get; set; } = "Spell slots";

    /// <summary>The sheet field naming the class entry, and the sheet value whose row is read
    /// (with multiclass rules, use their <see cref="MulticlassRules.ClassLevelKey"/>).</summary>
    public string Field { get; set; } = "class";
    public string ByField { get; set; } = "level";

    public string Table { get; set; } = "spellcasting";
    public string Column { get; set; } = "slots";
    public string? SlotLevelColumn { get; set; }
    public string CasterTypeProperty { get; set; } = "casterType";

    /// <summary>Only classes with one of these caster types count (empty: any).</summary>
    public List<string> IncludeCasterTypes { get; set; } = new();
    public List<string> ExcludeCasterTypes { get; set; } = new();

    public SlotCombineRule? Combine { get; set; }

    /// <summary>Rest names that refill this pool ("Short Rest", "Long Rest"); the table offers one button per name.</summary>
    public List<string> RecoversOn { get; set; } = new();

    public string KeyFor(int slotLevel) => $"{Key}_{slotLevel}";
    public string MaxKeyFor(int slotLevel) => $"{Key}_{slotLevel}_Max";

    /// <summary>True for "{Key}_{n}" and "{Key}_{n}_Max".</summary>
    public bool Owns(string sheetKey)
    {
        if (!sheetKey.StartsWith(Key + "_", System.StringComparison.Ordinal)) return false;
        var rest = sheetKey.Substring(Key.Length + 1);
        if (rest.EndsWith("_Max", System.StringComparison.Ordinal)) rest = rest.Substring(0, rest.Length - 4);
        return rest.Length > 0 && int.TryParse(rest, out _);
    }
}

/// <summary>How several classes' slots combine: each class adds its level times its caster type's
/// weight (rounded per <see cref="Rounding"/>), and the sum picks a row of <see cref="Table"/>
/// (row 1 is caster level 1).</summary>
public class SlotCombineRule
{
    public Dictionary<string, double> Weights { get; set; } = new();

    /// <summary>"up", "down" or "nearest", applied per class.</summary>
    public string Rounding { get; set; } = "down";

    /// <summary>Slots per caster level, each like <c>"4/3/2"</c>.</summary>
    public List<string> Table { get; set; } = new();
}

public class PointBuyRules
{
    public int Budget { get; set; } = 27;
    public int Min { get; set; } = 8;
    public int Max { get; set; } = 15;

    /// <summary>Cost of each score from <see cref="Min"/> to <see cref="Max"/>.</summary>
    public Dictionary<int, int> Costs { get; set; } = new();
}

public class BonusRules
{
    public string Label { get; set; } = "Bonuses";

    /// <summary>The sheet field whose referenced entry lists the eligible attributes.</summary>
    public string FromField { get; set; } = string.Empty;

    /// <summary>The property on that entry holding the eligible attribute keys, abbreviations or labels.</summary>
    public string Property { get; set; } = string.Empty;

    /// <summary>Allowed distributions, e.g. [[2, 1], [1, 1, 1]]: one bonus per distinct attribute.</summary>
    public List<List<int>> Allocations { get; set; } = new();
}

public class DerivedField
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    /// <summary>A C#-style expression over the sheet's numeric fields and attribute scores, e.g.
    /// <c>2 + (level - 1) / 4</c>. Integer arithmetic; <c>floor</c>, <c>ceil</c>, <c>max</c>, <c>min</c>
    /// and <c>mod(key)</c> (an attribute's modifier) are available.</summary>
    public string Formula { get; set; } = string.Empty;

    public string? Category { get; set; }
}

public class FieldConstraint
{
    public string Field { get; set; } = string.Empty;
    public List<RuleCondition> Where { get; set; } = new();
}

public class ListRule
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string TargetEntityType { get; set; } = string.Empty;
    public List<RuleCondition> Where { get; set; } = new();
    public CountRule? MaxCount { get; set; }
    public string? Category { get; set; }

    /// <summary>Candidate property that, when true, lets the same entry be picked more than once.</summary>
    public string? RepeatableProperty { get; set; }
}

/// <summary>
/// One condition on a candidate entry. Exactly one comparison is set; <see cref="Property"/> names
/// the candidate's property ("level", "classes", "category"...), or "id"/"name"/"tags".
/// </summary>
public class RuleCondition
{
    public string Property { get; set; } = string.Empty;

    /// <summary>Candidate value equals this constant.</summary>
    public new object? Equals { get; set; }

    /// <summary>Candidate value is one of these.</summary>
    public List<string>? In { get; set; }

    /// <summary>Candidate numeric value &gt;= / &lt;= a constant.</summary>
    public double? Gte { get; set; }
    public double? Lte { get; set; }

    /// <summary>Candidate value equals the raw value of this sheet field (an entry id, say).</summary>
    public string? EqualsField { get; set; }

    /// <summary>Candidate value equals a property of the entry a sheet field references
    /// (e.g. the feat named by the chosen background).</summary>
    public FieldProperty? EqualsPropertyOfField { get; set; }

    /// <summary>Candidate list property contains the display name of the entry a sheet field references.</summary>
    public string? ContainsNameOfField { get; set; }

    /// <summary>Candidate numeric value &lt;= / &gt;= a numeric sheet field.</summary>
    public string? LteField { get; set; }
    public string? GteField { get; set; }

    /// <summary>Candidate numeric value &lt;= a value looked up in a referenced entry's table.</summary>
    public LookupRule? LteLookup { get; set; }

    /// <summary>Shown when the condition fails.</summary>
    public string? Message { get; set; }
}

public class FieldProperty
{
    public string Field { get; set; } = string.Empty;
    public string Property { get; set; } = string.Empty;
}

/// <summary>
/// Reads a value out of a row table on a referenced entry: the sheet's <see cref="Field"/> names an
/// entry; that entry's <see cref="Table"/> property is a list of rows; the row whose
/// <see cref="RowKey"/> equals the sheet's <see cref="ByField"/> value (or the highest row not above
/// it) supplies <see cref="Column"/>.
/// </summary>
public class LookupRule
{
    public string Field { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public string ByField { get; set; } = "level";
    public string RowKey { get; set; } = "level";
    public string Column { get; set; } = string.Empty;
}

public class CountRule
{
    public int? Value { get; set; }
    public LookupRule? Lookup { get; set; }
    public int Plus { get; set; }
}
