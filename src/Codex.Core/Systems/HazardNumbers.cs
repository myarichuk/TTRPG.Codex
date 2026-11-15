using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Systems;

/// <summary>One way to find, disarm or deal with a hazard: a row of the detect, disarm or methods list (HAZARDS_DESIGN.md 3.1). The keys are fixed engine words.</summary>
/// <param name="Id">The name other rows refer to by <c>after</c>; unique in its list.</param>
/// <param name="Name">What it is about, shown on the button.</param>
/// <param name="Action">The method word as free text: Search, Study, 10 minutes.</param>
/// <param name="Check">A skill or attribute, by key or label. Empty: no check is rolled.</param>
/// <param name="Dc">The whole-number DC, only with a check.</param>
/// <param name="Needs">What it takes besides a check, or instead of one.</param>
/// <param name="After">The <c>id</c> of a notice route that must have succeeded first.</param>
/// <param name="TriggersOn">Outcome keys (<c>failure</c>, <c>criticalFailure</c>) that set the hazard off.</param>
/// <param name="Rank">The lowest rank that can try; only on a system with ranked skills.</param>
/// <param name="Successes">How many successes it takes, default 1.</param>
/// <param name="Passive">False stops the block's passive scores for this route; null follows the block.</param>
/// <param name="Text">The rest, in the project's words.</param>
public sealed record HazardRoute(string? Id, string? Name, string? Action, string? Check, int? Dc, string? Needs, string? After,
    IReadOnlyList<string> TriggersOn, string? Rank, int Successes, bool? Passive, string? Text)
{
    /// <summary>True when the route rolls a check. A route without one is done by what it needs.</summary>
    public bool HasCheck => !string.IsNullOrWhiteSpace(Check);

    /// <summary>What a button shows: the name, else the id, else the check.</summary>
    public string Shown => Name ?? Id ?? Check ?? string.Empty;
}

/// <summary>One part of a hazard's damage: dice of one type, hitting <c>Count</c> times (dice or a whole number; one when empty).</summary>
public sealed record HazardDamagePart(string Dice, string? Type, string? Count);

/// <summary>A condition a hazard puts on a creature, and how long it lasts.</summary>
public sealed record HazardCondition(string Name, string? Lasts);

/// <summary>
/// The numbers of a hazard entry as one placement uses them (HAZARDS_DESIGN.md 3.1): the entry's base properties, with the fields of one of its own
/// scaled rows (<c>higherLevels</c>) laid over them. The table reads a hazard's numbers only through <see cref="For"/>, so a placement made for one
/// party level does not change when the party grows. A row replaces a whole key (a damage list is restated whole), one level deep.
/// </summary>
public static class HazardNumbers
{
    private static readonly Regex DiceText = new(@"^\d+d\d+(\s*[+-]\s*(\d+d\d+|\d+))*$", RegexOptions.Compiled);

    /// <summary>True for dice such as <c>4d10</c> or <c>1d6 + 2</c>.</summary>
    public static bool IsDice(string? text) => DiceText.IsMatch(text ?? string.Empty);

    /// <summary>The <c>levels</c> of each scaled row the entry has, in the order written.</summary>
    public static IReadOnlyList<string> Rows(ContentEntry entry, ContentTypeDefinition type) =>
        RowsOf(entry, type).Select(r => Text(r, "levels")).Where(l => l.Length > 0).ToList();

    /// <summary>The entry's properties with the row named <paramref name="levels"/> laid over them. A null or unknown row gives the base numbers.</summary>
    public static IReadOnlyDictionary<string, object> For(ContentEntry entry, ContentTypeDefinition type, string? levels)
    {
        var numbers = new Dictionary<string, object>(entry.Properties, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(levels)) return numbers;
        var row = RowsOf(entry, type).FirstOrDefault(r => Text(r, "levels") == levels.Trim());
        if (row == null) return numbers;
        foreach (var (key, value) in row)
        {
            if (!string.Equals(key, "levels", StringComparison.Ordinal) && value != null) numbers[key] = value;
        }

        return numbers;
    }

    /// <summary>The ways to find the hazard, from the overlaid <paramref name="numbers"/>.</summary>
    public static IReadOnlyList<HazardRoute> NoticeRoutes(IReadOnlyDictionary<string, object> numbers, HazardDefinition hazard) =>
        RoutesOf(numbers, hazard.Notice?.Routes);

    /// <summary>The ways to make the hazard safe.</summary>
    public static IReadOnlyList<HazardRoute> DisableRoutes(IReadOnlyDictionary<string, object> numbers, HazardDefinition hazard) =>
        RoutesOf(numbers, hazard.Disable?.Routes);

    /// <summary>The other ways to deal with the hazard (escape, slow, destroy, set).</summary>
    public static IReadOnlyList<HazardRoute> Methods(IReadOnlyDictionary<string, object> numbers, HazardDefinition hazard) =>
        RoutesOf(numbers, hazard.Methods);

    /// <summary>The damage parts, in the order written.</summary>
    public static IReadOnlyList<HazardDamagePart> Damage(IReadOnlyDictionary<string, object> numbers, HazardDefinition hazard) =>
        RowsOfField(numbers, hazard.Effect?.Damage)
            .Select(r => new HazardDamagePart(Text(r, "dice"), Empty(Text(r, "type")), Empty(Text(r, "count"))))
            .Where(p => p.Dice.Length > 0)
            .ToList();

    /// <summary>The conditions a failed save (or the hazard itself, with no save) puts on a creature.</summary>
    public static IReadOnlyList<HazardCondition> Conditions(IReadOnlyDictionary<string, object> numbers, HazardDefinition hazard) =>
        RowsOfField(numbers, hazard.Effect?.Conditions)
            .Select(r => new HazardCondition(Text(r, "name"), Empty(Text(r, "lasts"))))
            .Where(c => c.Name.Length > 0)
            .ToList();

    /// <summary>What is wrong inside an entry's collection rows: a scaled row naming a key that is no field of the type (or one that only differs in
    /// case, which would be ignored without a word) or giving a Collection as something other than a list, a route row with a DC and no check, with
    /// neither a check nor a need, with a repeated id, an <c>after</c> that names no notice route, a rank on a system without ranks or a bad
    /// success count, a damage row whose dice are not dice, a condition the system does not have, and an other-way row with no name.</summary>
    public static IEnumerable<string> Problems(ContentEntry entry, ContentTypeDefinition type, SystemDefinition system)
    {
        if (type.Hazard is not { } hazard) yield break;
        var fields = type.Fields.ToDictionary(f => f.Key, f => f, StringComparer.Ordinal);
        foreach (var row in RowsOf(entry, type))
        {
            var at = Text(row, "levels");
            foreach (var (key, value) in row.Where(p => p.Key != "levels"))
            {
                if (!fields.TryGetValue(key, out var field))
                {
                    var near = fields.Keys.FirstOrDefault(f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase));
                    yield return near != null
                        ? $"scaled row {at} names '{key}'; the field is '{near}', so the row would be ignored."
                        : $"scaled row {at} names '{key}', which is not a field of {type.Type}.";
                }
                else if (field.Type == FieldType.Collection && !IsList(value))
                {
                    yield return $"scaled row {at} gives '{key}' as one value; the field is a Collection, so it needs a list of rows.";
                }
            }
        }

        var ranked = system.SheetRules?.Skills is { Ranks.Count: > 0 };
        var entryNumbers = (IReadOnlyDictionary<string, object>)entry.Properties;
        var noticeIds = RoutesOf(entryNumbers, hazard.Notice?.Routes).Select(r => r.Id).Where(i => !string.IsNullOrEmpty(i)).ToHashSet(StringComparer.Ordinal);
        foreach (var (label, field) in new[] { ("detect", hazard.Notice?.Routes), ("disarm", hazard.Disable?.Routes), ("other-way", hazard.Methods) })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in RowsOfField(entryNumbers, field))
            {
                var name = Text(row, "name") is { Length: > 0 } n ? n : Text(row, "id") is { Length: > 0 } i ? i : label + " row";
                var hasCheck = Text(row, "check").Length > 0;
                if (label == "other-way" && Text(row, "name").Length == 0) yield return "an other-way row has no name.";
                if (Text(row, "id") is { Length: > 0 } id && !seen.Add(id)) yield return $"{label} route '{name}' repeats the id '{id}'.";
                if (row.ContainsKey("dc") && !WholeNumber(row["dc"], out _)) yield return $"{label} route '{name}' has a dc that is not a whole number.";
                if (Text(row, "dc").Length > 0 && !hasCheck) yield return $"{label} route '{name}' has a dc and no check.";
                if (!hasCheck && Text(row, "needs").Length == 0 && Text(row, "text").Length == 0)
                {
                    yield return $"{label} route '{name}' has no check; it needs 'needs' or 'text' to say what it takes.";
                }

                if (Text(row, "after") is { Length: > 0 } after && !noticeIds.Contains(after)) yield return $"{label} route '{name}' comes after '{after}', which is no detect route id.";
                if (Text(row, "rank").Length > 0 && !ranked) yield return $"{label} route '{name}' names a rank; this system declares no ranks in sheetRules.skills.ranks.";
                if (row.ContainsKey("successes") && (!WholeNumber(row["successes"], out var successes) || successes < 1)) yield return $"{label} route '{name}' needs a whole number of successes, one or more.";
            }
        }

        foreach (var row in RowsOfField(entryNumbers, hazard.Effect?.Damage))
        {
            if (!IsDice(Text(row, "dice"))) yield return $"a damage row has dice '{Text(row, "dice")}'; they must be dice such as 2d8.";
            var count = Text(row, "count");
            if (count.Length > 0 && !IsDice(count) && !WholeNumber(count, out _)) yield return $"a damage row has count '{count}'; it must be dice or a whole number.";
        }

        var known = new HashSet<string>(system.Conditions, StringComparer.OrdinalIgnoreCase);
        foreach (var row in RowsOfField(entryNumbers, hazard.Effect?.Conditions))
        {
            var name = Text(row, "name");
            if (!known.Contains(name)) yield return $"condition '{name}' is not one of the system's conditions.";
        }
    }

    private static IEnumerable<IDictionary<string, object?>> RowsOf(ContentEntry entry, ContentTypeDefinition type) => Rows(entry.Properties, type.Hazard?.HigherLevels);

    private static IEnumerable<IDictionary<string, object?>> RowsOfField(IReadOnlyDictionary<string, object> numbers, string? field) => Rows(numbers, field);

    private static IReadOnlyList<HazardRoute> RoutesOf(IReadOnlyDictionary<string, object> numbers, string? field) =>
        Rows(numbers, field).Select(Route).ToList();

    private static HazardRoute Route(IDictionary<string, object?> row)
    {
        int? dc = WholeNumber(Raw(row, "dc"), out var parsedDc) ? (int)parsedDc : null;
        var successes = WholeNumber(Raw(row, "successes"), out var parsedSuccesses) && parsedSuccesses >= 1 ? (int)parsedSuccesses : 1;
        bool? passive = Raw(row, "passive") is { } p && bool.TryParse(Convert.ToString(p, CultureInfo.InvariantCulture), out var flag) ? flag : null;
        return new HazardRoute(Empty(Text(row, "id")), Empty(Text(row, "name")), Empty(Text(row, "action")), Empty(Text(row, "check")), dc,
            Empty(Text(row, "needs")), Empty(Text(row, "after")), Keys(Raw(row, "triggersOn")), Empty(Text(row, "rank")), successes, passive, Empty(Text(row, "text")));
    }

    private static IReadOnlyList<string> Keys(object? value) => value switch
    {
        null => Array.Empty<string>(),
        string s => s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        System.Collections.IEnumerable items => items.Cast<object?>().Select(i => Convert.ToString(i, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty).Where(s => s.Length > 0).ToList(),
        _ => Array.Empty<string>()
    };

    private static bool IsList(object? value) => value is System.Collections.IEnumerable && value is not string && value is not System.Collections.IDictionary;

    private static IEnumerable<IDictionary<string, object?>> Rows(IEnumerable<KeyValuePair<string, object>> source, string? field)
    {
        if (string.IsNullOrEmpty(field)) yield break;
        object? value = null;
        var found = false;
        foreach (var pair in source)
        {
            if (!string.Equals(pair.Key, field, StringComparison.Ordinal)) continue;
            value = pair.Value;
            found = true;
            break;
        }

        if (!found || value is not System.Collections.IEnumerable items || value is string) yield break;
        foreach (var item in items)
        {
            if (item is IDictionary<string, object> typed) yield return typed.ToDictionary(p => p.Key, p => (object?)p.Value);
            else if (item is IDictionary<object, object> loose)
            {
                yield return loose.ToDictionary(p => Convert.ToString(p.Key, CultureInfo.InvariantCulture) ?? string.Empty, p => (object?)p.Value);
            }
        }
    }

    private static object? Raw(IDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value : null;

    private static string? Empty(string text) => text.Length == 0 ? null : text;

    private static bool WholeNumber(object? value, out long number)
    {
        number = 0;
        return value != null && long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    private static string Text(IDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty : string.Empty;
}
