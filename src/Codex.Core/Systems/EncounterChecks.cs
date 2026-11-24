using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Systems;

/// <summary>The problems with a system's <c>encounters.difficulty</c> block (ENCOUNTER_BUILDER_DESIGN.md 2.1): each is a sentence naming the path, so an author can find the line.
/// A half-edited block (a field of the other shape left behind) is an error, not something ignored.</summary>
internal static partial class EncounterChecks
{
    private const string At = "encounters.difficulty";

    [GeneratedRegex("^[a-z][a-zA-Z0-9]*$")]
    private static partial Regex BandKeyPattern();

    public static IEnumerable<string> Problems(SystemDefinition definition)
    {
        if (definition.Encounters?.Difficulty is not { } rules) yield break;

        var shape = rules.Shape;
        var known = shape is DifficultyRules.ShapeSum or DifficultyRules.ShapeByLevel;
        if (!known) yield return $"{At}.shape is '{shape}'; use '{DifficultyRules.ShapeSum}' or '{DifficultyRules.ShapeByLevel}'.";
        if (rules.Pick is not (DifficultyRules.PickAtMost or DifficultyRules.PickAtLeast))
        {
            yield return $"{At}.pick is '{rules.Pick}'; use '{DifficultyRules.PickAtMost}' or '{DifficultyRules.PickAtLeast}'.";
        }

        if (rules.Bands.Count == 0) yield return $"{At}.bands needs at least one band.";
        var bandKeys = new List<string>();
        for (var i = 0; i < rules.Bands.Count; i++)
        {
            var band = rules.Bands[i];
            if (!BandKeyPattern().IsMatch(band.Key ?? string.Empty))
            {
                yield return $"{At}.bands[{i}].key '{band.Key}' must start with a lower-case letter and then use only letters and digits.";
            }
            else if (bandKeys.Contains(band.Key, StringComparer.Ordinal))
            {
                yield return $"{At}.bands[{i}].key '{band.Key}' is used by an earlier band.";
            }
            else if (shape == DifficultyRules.ShapeSum && band.Key == "level")
            {
                yield return $"{At}.bands[{i}].key 'level' is reserved: a budget row names its character level with it.";
            }

            bandKeys.Add(band.Key ?? string.Empty);
            if (string.IsNullOrWhiteSpace(band.Label)) yield return $"{At}.bands[{i}] ('{band.Key}') needs a label.";
        }

        var levelKey = string.IsNullOrWhiteSpace(rules.PartyLevel) ? definition.FieldRoles.Level : rules.PartyLevel;
        if (string.IsNullOrWhiteSpace(levelKey))
        {
            yield return $"{At}.partyLevel is not given, and fieldRoles.level is not set either; name the actor field that holds a character's level.";
        }
        else
        {
            foreach (var problem in ActorFieldProblem(definition, levelKey, !string.IsNullOrWhiteSpace(rules.PartyLevel) ? $"{At}.partyLevel" : "fieldRoles.level (the default for encounters.difficulty.partyLevel)")) yield return problem;
        }

        if (!known) yield break;
        var sum = shape == DifficultyRules.ShapeSum;
        if (sum)
        {
            foreach (var problem in WrongShape("creatureLevel", rules.CreatureLevel != null)) yield return problem;
            foreach (var problem in WrongShape("values", rules.Values.Count > 0)) yield return problem;
            foreach (var problem in WrongShape("partySize", rules.PartySize != null)) yield return problem;
            for (var i = 0; i < rules.Bands.Count; i++)
            {
                if (rules.Bands[i].Value != null) yield return $"{At}.bands[{i}].value belongs to shape '{DifficultyRules.ShapeByLevel}', and this block is shape '{shape}'; remove it or change the shape.";
                if (rules.Bands[i].PerCharacter != null) yield return $"{At}.bands[{i}].perCharacter belongs to shape '{DifficultyRules.ShapeByLevel}', and this block is shape '{shape}'; remove it or change the shape.";
            }

            foreach (var problem in CreatureFieldProblems(definition, rules.CreatureValue, "creatureValue")) yield return problem;
            foreach (var problem in SumBudget(rules, bandKeys)) yield return problem;
        }
        else
        {
            foreach (var problem in WrongShape("creatureValue", rules.CreatureValue != null)) yield return problem;
            foreach (var problem in WrongShape("unit", rules.Unit != null)) yield return problem;
            foreach (var problem in WrongShape("budget", rules.Budget.Count > 0)) yield return problem;
            foreach (var problem in CreatureFieldProblems(definition, rules.CreatureLevel, "creatureLevel")) yield return problem;
            foreach (var problem in LevelValues(rules)) yield return problem;
            foreach (var problem in BandBudgets(rules)) yield return problem;
        }

        IEnumerable<string> WrongShape(string field, bool present)
        {
            if (present) yield return $"{At}.{field} belongs to shape '{(sum ? DifficultyRules.ShapeByLevel : DifficultyRules.ShapeSum)}', and this block is shape '{shape}'; remove it or change the shape.";
        }
    }

    private static IEnumerable<string> ActorFieldProblem(SystemDefinition definition, string key, string where)
    {
        var field = definition.ActorFields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
        if (field == null)
        {
            var near = definition.ActorFields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
            yield return near != null ? $"{where} names '{key}', which is not an actor field; did you mean '{near.Key}'?" : $"{where} names '{key}', which is not an actor field.";
        }
        else if (field.Type != FieldType.Number)
        {
            yield return $"{where} names '{key}', which is {field.Type}; it must be a Number.";
        }
    }

    /// <summary>The creature field: on at least one creature type (an <c>actorBlueprint</c>), and a Number on each type that has it. A type without it is allowed: a creature of it is "no value" when rated.</summary>
    private static IEnumerable<string> CreatureFieldProblems(SystemDefinition definition, string? key, string name)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            yield return $"{At}.{name} is required for this shape.";
            yield break;
        }

        var types = definition.EntityTypes.Where(t => t.ActorBlueprint).ToList();
        if (types.Count == 0)
        {
            yield return $"{At}.{name} names '{key}', but this system has no entity type with actorBlueprint: true.";
            yield break;
        }

        var having = 0;
        foreach (var type in types)
        {
            var field = type.Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
            if (field == null) continue;
            having++;
            if (field.Type != FieldType.Number) yield return $"{At}.{name} names '{key}', which is {field.Type} on '{type.Type}'; it must be a Number.";
        }

        if (having == 0)
        {
            var near = types.SelectMany(t => t.Fields).FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
            yield return near != null
                ? $"{At}.{name} names '{key}', which no creature type declares; did you mean '{near.Key}'?"
                : $"{At}.{name} names '{key}', which no creature type (actorBlueprint: true) declares.";
        }
    }

    private static bool Whole(Dictionary<string, string> row, string key, out int value)
    {
        value = 0;
        return row.TryGetValue(key, out var text) && int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static IEnumerable<string> SumBudget(DifficultyRules rules, List<string> bandKeys)
    {
        if (rules.Budget.Count == 0)
        {
            yield return $"{At}.budget needs at least one row.";
            yield break;
        }

        int? previousLevel = null;
        for (var i = 0; i < rules.Budget.Count; i++)
        {
            var row = rules.Budget[i];
            var at = $"{At}.budget[{i}]";
            var good = true;
            foreach (var key in bandKeys.Where(k => !row.ContainsKey(k)))
            {
                good = false;
                yield return $"{at} has no number for band '{key}'.";
            }

            foreach (var key in row.Keys.Where(k => k != "level" && !bandKeys.Contains(k, StringComparer.Ordinal)))
            {
                good = false;
                yield return $"{at} has '{key}', which is neither 'level' nor a band key ({string.Join(", ", bandKeys)}).";
            }

            if (!Whole(row, "level", out var level) || level < 0)
            {
                good = false;
                yield return $"{at} needs a whole-number level, zero or more.";
            }
            else if (previousLevel is { } before && level <= before)
            {
                yield return level == before ? $"{at} repeats level {level}." : $"{at} is level {level}, after level {before}; rows go from the lowest level to the highest.";
            }
            else
            {
                previousLevel = level;
            }

            if (!good) continue;
            int? lastBand = null;
            foreach (var key in bandKeys)
            {
                if (!Whole(row, key, out var number) || number < 0)
                {
                    yield return $"{at}.{key} is '{row[key]}'; a budget is a whole number, zero or more.";
                    lastBand = null;
                    continue;
                }

                if (lastBand is { } smaller && number < smaller) yield return $"{at}.{key} is {number}, less than the band before it ({smaller}); budgets do not go down from one band to the next.";
                lastBand = number;
            }
        }
    }

    private static IEnumerable<string> LevelValues(DifficultyRules rules)
    {
        if (rules.Values.Count == 0) yield return $"{At}.values needs at least one entry.";
        for (var i = 0; i < rules.Values.Count; i++)
        {
            var at = $"{At}.values[{i}]";
            var entry = rules.Values[i];
            if (entry.Delta == null) yield return $"{at}.delta is required.";
            if (entry.Value == null) yield return $"{at}.value is required.";
            else if (entry.Value < 0) yield return $"{at}.value is {entry.Value}; it must be zero or more.";
            if (i > 0 && entry.Delta is { } delta && rules.Values[i - 1].Delta is { } before && delta != before + 1)
            {
                yield return $"{at}.delta is {delta}, after {before}; the deltas go up by exactly one, with none missing.";
            }
        }
    }

    /// <summary>The budgets each band of a <c>byLevel</c> block carries, and that the bands never cross: for every party of 1 to 20 a band's budget is at least the band before it.
    /// A budget is linear in the party size before its floor at zero, so the two ends are enough.</summary>
    private static IEnumerable<string> BandBudgets(DifficultyRules rules)
    {
        if (rules.PartySize is not { } size) yield return $"{At}.partySize is required for this shape.";
        else if (size < 1) yield return $"{At}.partySize is {size}; it must be 1 or more.";

        var complete = true;
        for (var i = 0; i < rules.Bands.Count; i++)
        {
            var band = rules.Bands[i];
            var at = $"{At}.bands[{i}]";
            if (band.Value == null) { complete = false; yield return $"{at}.value is required for this shape."; }
            else if (band.Value < 0) { complete = false; yield return $"{at}.value is {band.Value}; a budget is a whole number, zero or more."; }

            if (band.PerCharacter == null) { complete = false; yield return $"{at}.perCharacter is required for this shape."; }
            else if (band.PerCharacter < 0) { complete = false; yield return $"{at}.perCharacter is {band.PerCharacter}; it is a whole number, zero or more."; }
        }

        if (!complete || rules.PartySize is not { } written || written < 1) yield break;
        foreach (var party in new[] { 1, DifficultyRules.MaxParty })
        {
            for (var i = 1; i < rules.Bands.Count; i++)
            {
                long Budget(int index) => rules.Bands[index].Value!.Value + (long)rules.Bands[index].PerCharacter!.Value * (party - written);
                if (Budget(i) < Budget(i - 1))
                {
                    yield return $"{At}.bands[{i}] ('{rules.Bands[i].Key}') would have a smaller budget than the band before it for a party of {party}; bands may not cross for any party of 1 to {DifficultyRules.MaxParty}.";
                }
            }
        }
    }
}