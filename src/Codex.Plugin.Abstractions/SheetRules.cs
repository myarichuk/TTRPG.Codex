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
