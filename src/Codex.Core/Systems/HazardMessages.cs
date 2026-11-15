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
        ["RowWord"] = "{0} row",
        ["Kind.detect"] = "detect",
        ["Kind.disarm"] = "disarm",
        ["Kind.other-way"] = "other-way",
        ["Kind.damage"] = "damage",
        ["Kind.condition"] = "condition",
        ["Averaging"] = "averaging {0}",
        ["GivesAverage"] = "an average of {0} to {1}",
        ["GivesRange"] = "{0} to {1}",
        ["RowNearKey"] = "scaled row {0} names '{1}'; the field is '{2}', so the row would be ignored.",
        ["RowUnknownKey"] = "scaled row {0} names '{1}', which is not a field of {2}.",
        ["RowNotList"] = "scaled row {0} gives '{1}' as one value; the field is a Collection, so it needs a list of rows.",
        ["NoLevel"] = "{0}it has scaled numbers ({1}) and no level to read them against; fill in {2} with a number such as 5 or a range such as 1-4.",
        ["UnknownSeverity"] = "{0}Severity '{1}' is not one the system's guide has ({2}); its numbers were read against the guide for any severity.",
        ["High"] = "{0}{1} {2} is high for a level {3}{4} hazard; the system's guide gives {5}.",
        ["Low"] = "{0}{1} {2} is low for a level {3}{4} hazard; the system's guide gives {5}.",
        ["StrikesBad"] = "routine row '{0}' has strikes '{1}'; it must be true or false.",
        ["StrikesNoEffect"] = "routine row '{0}' strikes, and the hazard has no effect (no save, attack or damage) to apply.",
        ["StrikesMany"] = "more than one routine row has strikes: true; one row applies the effect.",
        ["SaveNoDc"] = "it has a save and no save DC.",
        ["DcNoSave"] = "it has a save DC and no save.",
        ["OnSaveBad"] = "onSave is '{0}'; it must be half or none.",
        ["OnSaveNoSave"] = "it has an onSave and no save.",
        ["ListNotRows"] = "{0}the {1} field '{2}' is one value; it needs a list of rows, each a map of keys.",
        ["ItemNotRow"] = "{0}the {1} list has '{2}', which is not a row of keys (write it as {{ name: ... }}).",
        ["OtherWayNoName"] = "{0}an other-way row has no name.",
        ["RepeatId"] = "{0}{1} route '{2}' repeats the id '{3}'.",
        ["DcNotWhole"] = "{0}{1} route '{2}' has a dc that is not a whole number.",
        ["DcNoCheck"] = "{0}{1} route '{2}' has a dc and no check.",
        ["NoCheck"] = "{0}{1} route '{2}' has no check; it needs 'needs' or 'text' to say what it takes.",
        ["AfterNoRoute"] = "{0}{1} route '{2}' comes after '{3}', which is no detect route id.",
        ["AfterItself"] = "{0}detect route '{1}' comes after itself.",
        ["RankNone"] = "{0}{1} route '{2}' names a rank; this system declares no ranks in sheetRules.skills.ranks.",
        ["SuccessesBad"] = "{0}{1} route '{2}' needs a whole number of successes, one or more.",
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
