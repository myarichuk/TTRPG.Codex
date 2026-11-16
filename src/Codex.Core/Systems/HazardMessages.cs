namespace Codex.Core.Systems;

/// <summary>
/// The words of the hazard checks (<see cref="HazardNumbers.Problems"/>, <see cref="HazardNumbers.ScalingProblems"/>). The core has no localizer, so each message is a key
/// and its arguments: a caller that has one passes a function that says it in the reader's language (the wizard and the world builder's review card do), and a caller that has
/// none (the pack loader's log, a model reading a tool result) gets these English templates. The templates are the English resource strings, word for word, and a test holds
/// them together. <c>{{</c> and <c>}}</c> are literal braces, as in the resource files.
/// </summary>
public static class HazardMessages
{
    /// <summary>The prefix of every key, in <c>Strings.Wizard</c>.</summary>
    public const string Prefix = "Wizard.Hazard.Problem.";

    /// <summary>Every message and word by key (without the prefix), in English.</summary>
    public static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Band"] = "band {0}: ",
        ["LevelWord"] = "level",
        ["RoutineRow"] = "routine row",
        ["RowWord.detect"] = "detect row",
        ["RowWord.disarm"] = "disarm row",
        ["RowWord.other-way"] = "other-way row",
        ["RowNearKey"] = "scaled row {0} names '{1}'; the field is '{2}', so the row would be ignored.",
        ["RowUnknownKey"] = "scaled row {0} names '{1}', which is not a field of {2}.",
        ["RowNotList"] = "scaled row {0} gives '{1}' as one value; the field is a Collection, so it needs a list of rows.",
        ["NoLevel"] = "{0}it has scaled numbers ({1}) and no level to read them against; fill in {2} with a number such as 5 or a range such as 1-4.",
        ["UnknownSeverity"] = "{0}Severity '{1}' is not one the system's guide has ({2}); its numbers were read against the guide for any severity.",
        ["High"] = "{0}{1} {2} is high for a level {3} hazard; the system's guide gives {4} to {5}.",
        ["High.Severity"] = "{0}{1} {2} is high for a level {3} {6} hazard; the system's guide gives {4} to {5}.",
        ["High.Average"] = "{0}{1} averaging {2} is high for a level {3} hazard; the system's guide gives an average of {4} to {5}.",
        ["High.Average.Severity"] = "{0}{1} averaging {2} is high for a level {3} {6} hazard; the system's guide gives an average of {4} to {5}.",
        ["High.Dice"] = "{0}{1} averaging {2} is high for a level {3} hazard; the system's guide gives {4} to {5}.",
        ["High.Dice.Severity"] = "{0}{1} averaging {2} is high for a level {3} {6} hazard; the system's guide gives {4} to {5}.",
        ["Low"] = "{0}{1} {2} is low for a level {3} hazard; the system's guide gives {4} to {5}.",
        ["Low.Severity"] = "{0}{1} {2} is low for a level {3} {6} hazard; the system's guide gives {4} to {5}.",
        ["Low.Average"] = "{0}{1} averaging {2} is low for a level {3} hazard; the system's guide gives an average of {4} to {5}.",
        ["Low.Average.Severity"] = "{0}{1} averaging {2} is low for a level {3} {6} hazard; the system's guide gives an average of {4} to {5}.",
        ["Low.Dice"] = "{0}{1} averaging {2} is low for a level {3} hazard; the system's guide gives {4} to {5}.",
        ["Low.Dice.Severity"] = "{0}{1} averaging {2} is low for a level {3} {6} hazard; the system's guide gives {4} to {5}.",
        ["StrikesBad"] = "routine row '{0}' has strikes '{1}'; it must be true or false.",
        ["StrikesNoEffect"] = "routine row '{0}' strikes, and the hazard has no effect (no save, attack or damage) to apply.",
        ["StrikesMany"] = "more than one routine row has strikes: true; one row applies the effect.",
        ["SaveNoDc"] = "it has a save and no save DC.",
        ["DcNoSave"] = "it has a save DC and no save.",
        ["OnSaveBad"] = "onSave is '{0}'; it must be half or none.",
        ["OnSaveNoSave"] = "it has an onSave and no save.",
        ["ListNotRows.detect"] = "{0}the detect field '{1}' is one value; it needs a list of rows, each a map of keys.",
        ["ListNotRows.disarm"] = "{0}the disarm field '{1}' is one value; it needs a list of rows, each a map of keys.",
        ["ListNotRows.other-way"] = "{0}the other-way field '{1}' is one value; it needs a list of rows, each a map of keys.",
        ["ListNotRows.damage"] = "{0}the damage field '{1}' is one value; it needs a list of rows, each a map of keys.",
        ["ListNotRows.condition"] = "{0}the condition field '{1}' is one value; it needs a list of rows, each a map of keys.",
        ["ItemNotRow.detect"] = "{0}the detect list has '{1}', which is not a row of keys (write it as {{ name: ... }}).",
        ["ItemNotRow.disarm"] = "{0}the disarm list has '{1}', which is not a row of keys (write it as {{ name: ... }}).",
        ["ItemNotRow.other-way"] = "{0}the other-way list has '{1}', which is not a row of keys (write it as {{ name: ... }}).",
        ["ItemNotRow.damage"] = "{0}the damage list has '{1}', which is not a row of keys (write it as {{ name: ... }}).",
        ["ItemNotRow.condition"] = "{0}the condition list has '{1}', which is not a row of keys (write it as {{ name: ... }}).",
        ["OtherWayNoName"] = "{0}an other-way row has no name.",
        ["RepeatId.detect"] = "{0}detect route '{1}' repeats the id '{2}'.",
        ["RepeatId.disarm"] = "{0}disarm route '{1}' repeats the id '{2}'.",
        ["RepeatId.other-way"] = "{0}other-way route '{1}' repeats the id '{2}'.",
        ["DcNotWhole.detect"] = "{0}detect route '{1}' has a dc that is not a whole number.",
        ["DcNotWhole.disarm"] = "{0}disarm route '{1}' has a dc that is not a whole number.",
        ["DcNotWhole.other-way"] = "{0}other-way route '{1}' has a dc that is not a whole number.",
        ["DcNoCheck.detect"] = "{0}detect route '{1}' has a dc and no check.",
        ["DcNoCheck.disarm"] = "{0}disarm route '{1}' has a dc and no check.",
        ["DcNoCheck.other-way"] = "{0}other-way route '{1}' has a dc and no check.",
        ["NoCheck.detect"] = "{0}detect route '{1}' has no check; it needs 'needs' or 'text' to say what it takes.",
        ["NoCheck.disarm"] = "{0}disarm route '{1}' has no check; it needs 'needs' or 'text' to say what it takes.",
        ["NoCheck.other-way"] = "{0}other-way route '{1}' has no check; it needs 'needs' or 'text' to say what it takes.",
        ["AfterNoRoute.detect"] = "{0}detect route '{1}' comes after '{2}', which is no detect route id.",
        ["AfterNoRoute.disarm"] = "{0}disarm route '{1}' comes after '{2}', which is no detect route id.",
        ["AfterNoRoute.other-way"] = "{0}other-way route '{1}' comes after '{2}', which is no detect route id.",
        ["AfterItself"] = "{0}detect route '{1}' comes after itself.",
        ["RankNone.detect"] = "{0}detect route '{1}' names a rank; this system declares no ranks in sheetRules.skills.ranks.",
        ["RankNone.disarm"] = "{0}disarm route '{1}' names a rank; this system declares no ranks in sheetRules.skills.ranks.",
        ["RankNone.other-way"] = "{0}other-way route '{1}' names a rank; this system declares no ranks in sheetRules.skills.ranks.",
        ["SuccessesBad.detect"] = "{0}detect route '{1}' needs a whole number of successes, one or more.",
        ["SuccessesBad.disarm"] = "{0}disarm route '{1}' needs a whole number of successes, one or more.",
        ["SuccessesBad.other-way"] = "{0}other-way route '{1}' needs a whole number of successes, one or more.",
        ["Circle"] = "{0}detect route '{1}' waits, through other routes, for itself.",
        ["DamageDice"] = "{0}a damage row has dice '{1}'; they must be dice such as 2d8.",
        ["DamageCount"] = "{0}a damage row has count '{1}'; it must be dice or a whole number.",
        ["ConditionNoName"] = "{0}a condition row has no name.",
        ["ConditionUnknown"] = "{0}condition '{1}' is not one of the system's conditions."
    };

    /// <summary>The message for <paramref name="key"/> (without the prefix): said by <paramref name="say"/> when there is one (it gets the full key), else in English.</summary>
    public static string Say(Func<string, object?[], string>? say, string key, params object?[] args) =>
        say != null ? say(Prefix + key, args) : args.Length == 0 ? English[key].Replace("{{", "{").Replace("}}", "}") : string.Format(System.Globalization.CultureInfo.InvariantCulture, English[key], args);
}
