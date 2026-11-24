using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

/// <summary>What the system says about encounters (<c>encounters:</c> in system.yaml): for now, how hard a lineup is for the party. Absent: the
/// encounter builder shows no difficulty at all.</summary>
public class EncounterRules
{
    /// <summary>How hard a lineup is for the party: a total of the creatures' values compared with budgets for the party, which names a band
    /// (Low, Moderate, High). Absent: no readout. Every number is the system's own; the app ships none.</summary>
    public DifficultyRules? Difficulty { get; set; }
}

/// <summary>
/// The difficulty readout of a prepared encounter. Two shapes. <c>sum</c>: each creature is worth a number from its entry (<c>creatureValue</c>), and the
/// party's budget for a band is the sum, over the characters, of that band's number in the <c>budget</c> row of the character's level. <c>byLevel</c>:
/// each creature is worth a number by how far its level is from the party's (<c>creatureLevel</c> and <c>values</c>), and each band has one budget
/// for a party of <c>partySize</c>, with a number added or taken for each character more or fewer.
/// A creature a line cannot value is named and left out; the band is given from the rest. Fields of the other shape are an error.
/// </summary>
public class DifficultyRules
{
    /// <summary>The shape constants <see cref="Shape"/> takes.</summary>
    public const string ShapeSum = "sum";
    public const string ShapeByLevel = "byLevel";

    /// <summary>The values <see cref="Pick"/> takes.</summary>
    public const string PickAtMost = "atMost";
    public const string PickAtLeast = "atLeast";

    /// <summary>The most characters a readout rates for.</summary>
    public const int MaxParty = 20;

    /// <summary>Required: <c>sum</c> or <c>byLevel</c>.</summary>
    public string Shape { get; set; } = string.Empty;

    /// <summary>The actor field (a Number) holding a player character's level. Default: the <c>fieldRoles.level</c> field. Neither: the system is refused.</summary>
    public string? PartyLevel { get; set; }

    /// <summary>The bands, easiest first: <c>{ key: low, label: Low }</c>. At least one. Keys start with a lower-case letter and carry letters and digits (under <c>sum</c> the key
    /// <c>level</c> is reserved, since a budget row has it too); labels are translated in <c>system.&lt;locale&gt;.yaml</c> (<c>encounters.difficulty.bands</c>), keys never are.
    /// Under <c>byLevel</c> each band also carries its budget, <c>{ key: low, label: Low, value: 40, perCharacter: 10 }</c>.</summary>
    public List<DifficultyBand> Bands { get; set; } = new();

    /// <summary>How a total names a band. <c>atMost</c> (the default): a band's budget is the most it allows, and the total is in the first band whose budget it does not
    /// pass; a total above the last budget is "above" the last band. <c>atLeast</c>: a band's budget is what it starts at, and the total is in the last band whose budget
    /// it reaches; a total below the first is "below" the first band.</summary>
    public string Pick { get; set; } = PickAtMost;

    /// <summary><c>sum</c> only: the Number field of a creature type (one marked <c>actorBlueprint</c>) that says what a creature is worth, such as <c>xp</c>. It must be a Number
    /// wherever a creature type has it.</summary>
    public string? CreatureValue { get; set; }

    /// <summary><c>sum</c> only: a short word shown after the total ("250 XP"). Translated. None: the bare number.</summary>
    public string? Unit { get; set; }

    /// <summary><c>byLevel</c> only: the Number field of a creature type that says the creature's level. Same rule as <see cref="CreatureValue"/>.</summary>
    public string? CreatureLevel { get; set; }

    /// <summary><c>byLevel</c> only: what a creature is worth by its level minus the party's level: <c>{ delta: 0, value: 10 }</c>. The deltas go up by exactly one, with no gap.
    /// A creature whose delta is not listed is named as one the table cannot value.</summary>
    public List<DifficultyValue> Values { get; set; } = new();

    /// <summary><c>byLevel</c> only: the number of characters the <see cref="Budget"/> values are written for (1 or more).</summary>
    public int? PartySize { get; set; }

    /// <summary><c>sum</c> only: the budgets, one row per character level in increasing level order, each <c>{ level: 3, low: 20, moderate: 40, high: 60 }</c>: a whole number per
    /// band key, per character, never smaller than the band before it. (Under <c>byLevel</c> each band carries its own budget.)</summary>
    public List<Dictionary<string, string>> Budget { get; set; } = new();
}

/// <summary>One band of the readout: a key the rules use and a label the page shows.</summary>
public class DifficultyBand
{
    /// <summary>The band's key: a lower-case letter, then letters and digits. Never translated.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The word shown for the band. Translated.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary><c>byLevel</c> only (required there): the band's budget for <c>partySize</c> characters, a whole number of zero or more.</summary>
    public int? Value { get; set; }

    /// <summary><c>byLevel</c> only (required there): what is added to <see cref="Value"/> for each character above <c>partySize</c>, and taken for each below it; a whole number of
    /// zero or more. A budget is never below zero, and bands may not cross for any party of 1 to 20.</summary>
    public int? PerCharacter { get; set; }
}

/// <summary>What a creature is worth at one distance from the party's level.</summary>
public class DifficultyValue
{
    /// <summary>Required: the creature's level minus the party's level.</summary>
    public int? Delta { get; set; }

    /// <summary>Required: what the creature is worth, a whole number of zero or more.</summary>
    public int? Value { get; set; }
}

/// <summary>The words of <see cref="EncounterRules"/> in one language (<c>encounters:</c> of <c>system.&lt;locale&gt;.yaml</c>).</summary>
public class EncounterTranslation
{
    /// <summary>The difficulty readout's words.</summary>
    public DifficultyTranslation Difficulty { get; set; } = new();
}

/// <summary>The readout's labels in one language.</summary>
public class DifficultyTranslation
{
    /// <summary>The word after the total. None: the system's own.</summary>
    public string? Unit { get; set; }

    /// <summary>Band key -> the label to show.</summary>
    public Dictionary<string, string> Bands { get; set; } = new();
}
