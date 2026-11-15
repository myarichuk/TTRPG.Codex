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

/// <summary>One step of a complex hazard's routine: a row of its <c>routine</c> list (a name and what it does on the hazard's turn or on another creature's). <see cref="Strikes"/>
/// is the row's <c>strikes: true</c>: the one row that applies the hazard's effect to the creatures it names (the Rolling Stone's Crush). Every other row is a note.</summary>
public sealed record HazardStep(string Name, string Description, bool Strikes = false);

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

    /// <summary>True when the entry's <c>complex</c> field is true: it is also an actor, with a turn in initiative (HAZARDS_DESIGN.md 3.3). A type with no <c>complex</c> field has none.</summary>
    public static bool IsComplex(ContentEntry entry, ContentTypeDefinition type) =>
        type.Hazard?.Complex is { Length: > 0 } field && entry.Properties.TryGetValue(field, out var value)
        && (value is bool flag ? flag : bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) && parsed);

    /// <summary>A complex hazard's initiative bonus (its <c>initiative</c> field), 0 when it has none.</summary>
    public static int InitiativeOf(IReadOnlyDictionary<string, object> numbers, HazardDefinition hazard) =>
        hazard.Initiative is { Length: > 0 } field && numbers.TryGetValue(field, out var value) && WholeNumber(value, out var bonus) ? (int)bonus : 0;

    /// <summary>A complex hazard's routine, in the order written: each row a name and what it does. The numbers come from the overlaid <paramref name="numbers"/>.</summary>
    public static IReadOnlyList<HazardStep> Routine(IReadOnlyDictionary<string, object> numbers, HazardDefinition hazard) =>
        RowsOfField(numbers, hazard.Routine)
            .Select(r => new HazardStep(Text(r, "name"), Text(r, "description"), IsTrue(Raw(r, "strikes"))))
            .Where(s => s.Name.Length > 0 || s.Description.Length > 0)
            .ToList();

    /// <summary>True when the hazard has an effect to apply: a save, an attack or a damage row among the overlaid <paramref name="numbers"/> (the entry's own values, not the type's field names).</summary>
    public static bool HasEffect(IReadOnlyDictionary<string, object> numbers, HazardDefinition hazard)
    {
        if (hazard.Effect is not { } effect) return false;
        bool Has(string? field) => !string.IsNullOrEmpty(field) && numbers.TryGetValue(field, out var v) && Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim().Length > 0;
        return Has(effect.Save) || Has(effect.Attack) || Damage(numbers, hazard).Count > 0;
    }

    private static bool IsTrue(object? value) => value is bool flag ? flag : bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) && parsed;

    /// <summary>The <c>levels</c> of each scaled row the entry has, in the order written.</summary>
    public static IReadOnlyList<string> Rows(ContentEntry entry, ContentTypeDefinition type) =>
        RowsOf(entry, type).Select(r => Text(r, "levels")).Where(l => l.Length > 0).ToList();

    /// <summary>The entry's properties with the row named <paramref name="levels"/> laid over them. A null or unknown row gives the base numbers. The copy is shallow: the lists and rows inside are the entry's own, so treat them as read only.</summary>
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

        var entryNumbers = (IReadOnlyDictionary<string, object>)entry.Properties;
        foreach (var message in SaveProblems(entryNumbers, hazard)) yield return message;
        foreach (var message in RoutineProblems(entryNumbers, hazard)) yield return message;
        foreach (var message in ListProblems(entryNumbers, entryNumbers, hazard, system, string.Empty)) yield return message;

        // A band restates whole lists (the Falling Net's methods, a pit's damage), so each of its lists is checked like the base's.
        foreach (var row in RowsOf(entry, type))
        {
            var levels = Text(row, "levels");
            var merged = For(entry, type, levels);
            var own = merged.Where(p => row.ContainsKey(p.Key) && p.Key != "levels").ToDictionary(p => p.Key, p => p.Value);
            foreach (var message in ListProblems(own, merged, hazard, system, $"band {levels}: ")) yield return message;
        }
    }

    /// <summary>
    /// Where a hazard's numbers stand against the system's <c>hazards.scaling</c> (HAZARDS_DESIGN.md 3.2), for a hazard the world builder proposes: one message for
    /// each scaled number (a column of the table the hazard also has, such as a save DC, or its damage) more than one row away from the row for the
    /// hazard's level, and one when a hazard with scaled numbers has no level to read. The level is the lowest whole number in the entry's level field
    /// (<c>5-10</c> reads as 5); the row is the highest one at or below it, or the first. A band of <c>higherLevels</c> is read at its own levels. The
    /// window is the row before to the row after; below the first row there is no floor. These are warnings, never refusals: the game master decides, and
    /// the messages use the fields' labels and state the range, for the game master and the model alike. Nothing here changes the entry. The table has
    /// no severity, so a mild hazard is read against the same rows as a deadly one. A system with no <c>hazards.scaling</c> has no messages.
    /// </summary>
    public static IEnumerable<string> ScalingProblems(ContentEntry entry, ContentTypeDefinition type, SystemDefinition system)
    {
        if (type.Hazard is not { } hazard || system.Hazards?.Scaling is not { Count: > 0 } scaling) yield break;
        var rows = scaling
            .Select(row => (Level: row.TryGetValue("level", out var text) && WholeNumber(text, out var n) ? (long?)n : null, Row: row))
            .Where(r => r.Level != null)
            .OrderBy(r => r.Level)
            .ToList();
        if (rows.Count == 0) yield break;
        var columns = rows.SelectMany(r => r.Row.Keys).Where(k => k != "level").Distinct(StringComparer.Ordinal).ToList();
        string LabelOf(string key) => type.Fields.FirstOrDefault(f => f.Key == key)?.Label is { Length: > 0 } label ? label : key;
        var levelLabel = hazard.Level is { Length: > 0 } levelKey ? LabelOf(levelKey) : "level";

        var bands = new List<(string Prefix, string? Levels, string LevelText)> { (string.Empty, null, hazard.Level is { Length: > 0 } field && entry.Properties.TryGetValue(field, out var own) ? Convert.ToString(own, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty) };
        bands.AddRange(Rows(entry, type).Select(levels => ($"band {levels}: ", (string?)levels, levels)));
        foreach (var (prefix, levels, levelText) in bands)
        {
            var numbers = For(entry, type, levels);
            var measured = columns.Select(c => (Column: c, Value: Measure(numbers, c, hazard))).Where(m => m.Value != null).ToList();
            if (measured.Count == 0) continue;
            if (LowestLevel(levelText) is not { } level)
            {
                yield return $"{prefix}it has scaled numbers ({string.Join(", ", measured.Select(m => LabelOf(m.Column)))}) and no level to read them against; fill in {levelLabel} with a number such as 5 or a range such as 1-4.";
                continue;
            }

            var at = rows.FindLastIndex(r => r.Level <= level);
            if (at < 0) at = 0;
            foreach (var (column, value) in measured)
            {
                var near = rows.Skip(Math.Max(at - 1, 0)).Take(Math.Min(at + 1, rows.Count - 1) - Math.Max(at - 1, 0) + 1)
                    .Select(r => (Text: r.Row.GetValueOrDefault(column) ?? string.Empty, Value: ScaledValue(r.Row, column))).Where(v => v.Value != null).ToList();
                if (near.Count == 0) continue;
                var low = near.MinBy(v => v.Value)!;
                var high = near.MaxBy(v => v.Value)!;
                var shown = string.Equals(hazard.Effect?.Damage, column, StringComparison.Ordinal) ? $"averaging {value.GetValueOrDefault():0.#}" : $"{value.GetValueOrDefault():0.#}";
                if (value > high.Value) yield return $"{prefix}{LabelOf(column)} {shown} is high for a level {level} hazard; the system's guide gives {low.Text} to {high.Text}.";
                else if (at > 0 && value < low.Value) yield return $"{prefix}{LabelOf(column)} {shown} is low for a level {level} hazard; the system's guide gives {low.Text} to {high.Text}.";
            }
        }
    }

    /// <summary>
    /// The row of the system's <c>hazards.scaling</c> for a level text (<c>5</c>, or a range such as <c>5-10</c>, which reads as its lowest number): the highest
    /// row at or below the level, or the first. Null when the system has no table or the text has no number. The homebrew wizard fills a new hazard's numbers from it
    /// and they stay editable; the same reading <see cref="ScalingProblems"/> measures against.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? GuideRow(string? levelText, SystemDefinition system)
    {
        if (system.Hazards?.Scaling is not { Count: > 0 } scaling || LowestLevel(levelText ?? string.Empty) is not { } level) return null;
        var rows = scaling
            .Select(row => (Level: row.TryGetValue("level", out var text) && WholeNumber(text, out var n) ? (long?)n : null, Row: row))
            .Where(r => r.Level != null)
            .OrderBy(r => r.Level)
            .ToList();
        if (rows.Count == 0) return null;
        var at = rows.FindLastIndex(r => r.Level <= level);
        return rows[at < 0 ? 0 : at].Row;
    }

    /// <summary>The rows of a Collection value as maps the editor can change: each row copied (its lists stay the entry's own, so replace them, never edit them),
    /// whatever form the store gave them in. Anything that is not a row is left out.</summary>
    public static List<Dictionary<string, object>> EditableRows(object? value)
    {
        var rows = new List<Dictionary<string, object>>();
        if (value is not System.Collections.IEnumerable items || value is string) return rows;
        foreach (var item in items)
        {
            if (item is IDictionary<string, object> typed) rows.Add(new Dictionary<string, object>(typed, StringComparer.Ordinal));
            else if (item is IDictionary<object, object> loose)
            {
                rows.Add(loose.Where(p => p.Value != null).ToDictionary(p => Convert.ToString(p.Key, CultureInfo.InvariantCulture) ?? string.Empty, p => p.Value, StringComparer.Ordinal));
            }
        }

        return rows;
    }

    /// <summary>
    /// Takes the blank rows out of the hazard's lists (routes, other ways, damage, conditions, routine and the bands, and the lists inside a band) and drops a list left
    /// empty, so an "add a row" that was never filled in is not saved as a row the table could not run. A row is blank when every value in it is empty; <c>false</c> is a value.
    /// Changes <paramref name="properties"/> in place.
    /// </summary>
    public static void Tidy(IDictionary<string, object> properties, ContentTypeDefinition type)
    {
        if (type.Hazard is not { } hazard) return;
        foreach (var field in new[] { hazard.Notice?.Routes, hazard.Disable?.Routes, hazard.Methods, hazard.Effect?.Damage, hazard.Effect?.Conditions, hazard.Routine, hazard.HigherLevels })
        {
            if (string.IsNullOrEmpty(field) || !properties.TryGetValue(field, out var value)) continue;
            if (TidyRows(value) is { Count: > 0 } kept) properties[field] = kept;
            else properties.Remove(field);
        }
    }

    private static List<object>? TidyRows(object? value)
    {
        if (value is not System.Collections.IEnumerable || value is string) return null;
        var kept = new List<object>();
        foreach (var row in EditableRows(value))
        {
            foreach (var key in row.Keys.ToList())
            {
                if (row[key] is System.Collections.IEnumerable list && row[key] is not string)
                {
                    // A list of rows is tidied like the others; a list of words (triggersOn) stays unless it is empty.
                    var items = list.Cast<object?>().ToList();
                    if (items.Any(i => i is System.Collections.IDictionary))
                    {
                        if (TidyRows(list) is { Count: > 0 } inner) row[key] = inner;
                        else row.Remove(key);
                    }
                    else if (items.Count == 0) row.Remove(key);
                }
                else if (row[key] is string text && text.Trim().Length == 0) row.Remove(key);
            }

            if (row.Count > 0) kept.Add(row);
        }

        return kept;
    }

    private static long? LowestLevel(string text) =>
        Regex.Match(text ?? string.Empty, @"\d+") is { Success: true } m && long.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    // A table row's value for a column: a whole number, or dice by their average.
    private static double? ScaledValue(IReadOnlyDictionary<string, string> row, string column) =>
        row.TryGetValue(column, out var text) ? Number(text) : null;

    // A hazard's own number for a column: the damage field is its parts' average, summed (a part's count multiplies it); any other is a number or dice.
    private static double? Measure(IReadOnlyDictionary<string, object> numbers, string column, HazardDefinition hazard)
    {
        if (string.Equals(hazard.Effect?.Damage, column, StringComparison.Ordinal))
        {
            var parts = RowsOfField(numbers, column).Select(r => (Dice: Number(Text(r, "dice")), Count: Text(r, "count"))).Where(p => p.Dice != null).ToList();
            return parts.Count == 0 ? null : parts.Sum(p => p.Dice!.Value * (p.Count.Length == 0 ? 1 : Number(p.Count) ?? 1));
        }

        return numbers.TryGetValue(column, out var value) && value is not (System.Collections.IEnumerable and not string) ? Number(Convert.ToString(value, CultureInfo.InvariantCulture)) : null;
    }

    // A whole number as itself, dice (2d10, 1d6 + 2) as their average; anything else is not a number.
    private static double? Number(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) return whole;
        if (!IsDice(trimmed)) return null;
        double sum = 0;
        foreach (Match term in Regex.Matches(trimmed.Replace(" ", string.Empty), @"([+-]?)(\d+)(?:d(\d+))?"))
        {
            var sign = term.Groups[1].Value == "-" ? -1 : 1;
            var count = double.Parse(term.Groups[2].Value, CultureInfo.InvariantCulture);
            sum += sign * (term.Groups[3].Success ? count * (double.Parse(term.Groups[3].Value, CultureInfo.InvariantCulture) + 1) / 2 : count);
        }

        return sum;
    }

    // A routine row's `strikes` is true or false, and only a hazard with an effect has a row that applies it (one row at most).
    private static IEnumerable<string> RoutineProblems(IReadOnlyDictionary<string, object> numbers, HazardDefinition hazard)
    {
        var strikes = 0;
        foreach (var row in RowsOfField(numbers, hazard.Routine))
        {
            if (!row.TryGetValue("strikes", out var raw) || raw == null) continue;
            var name = Text(row, "name") is { Length: > 0 } n ? n : "routine row";
            if (raw is not bool && !bool.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out _))
            {
                yield return $"routine row '{name}' has strikes '{raw}'; it must be true or false.";
            }
            else if (IsTrue(raw))
            {
                strikes++;
                if (!HasEffect(numbers, hazard)) yield return $"routine row '{name}' strikes, and the hazard has no effect (no save, attack or damage) to apply.";
            }
        }

        if (strikes > 1) yield return "more than one routine row has strikes: true; one row applies the effect.";
    }

    private static readonly string[] OnSaveValues = ["half", "none"];

    // The save, its DC and what a passed save does: one save has all of them; with none, onSave means nothing. An absent onSave is "none".
    private static IEnumerable<string> SaveProblems(IReadOnlyDictionary<string, object> numbers, HazardDefinition hazard)
    {
        if (hazard.Effect is not { } effect) yield break;
        bool Has(string? field) => !string.IsNullOrEmpty(field) && numbers.TryGetValue(field, out var v) && Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim().Length > 0;
        var save = Has(effect.Save);
        if (save && !Has(effect.SaveDc)) yield return "it has a save and no save DC.";
        if (!save && Has(effect.SaveDc)) yield return "it has a save DC and no save.";
        if (Has(effect.OnSave))
        {
            var value = Convert.ToString(numbers[effect.OnSave!], CultureInfo.InvariantCulture)!.Trim();
            if (!OnSaveValues.Contains(value, StringComparer.Ordinal)) yield return $"onSave is '{value}'; it must be half or none.";
            if (!save) yield return "it has an onSave and no save.";
        }
    }

    // The route, damage and condition lists among `own` (the whole entry, or the keys a band restates). `all` is what a route's `after` is checked against.
    private static IEnumerable<string> ListProblems(IReadOnlyDictionary<string, object> own, IReadOnlyDictionary<string, object> all, HazardDefinition hazard,
        SystemDefinition system, string prefix)
    {
        var ranked = system.SheetRules?.Skills is { Ranks.Count: > 0 };
        var noticeRows = RoutesOf(all, hazard.Notice?.Routes);
        var noticeIds = noticeRows.Select(r => r.Id).Where(i => !string.IsNullOrEmpty(i)).ToHashSet(StringComparer.Ordinal);
        var lists = new[] { ("detect", hazard.Notice?.Routes), ("disarm", hazard.Disable?.Routes), ("other-way", hazard.Methods), ("damage", hazard.Effect?.Damage), ("condition", hazard.Effect?.Conditions) };
        foreach (var (label, field) in lists)
        {
            if (string.IsNullOrEmpty(field) || !own.TryGetValue(field, out var raw)) continue;
            if (!IsList(raw))
            {
                yield return $"{prefix}the {label} field '{field}' is one value; it needs a list of rows, each a map of keys.";
                continue;
            }

            foreach (var item in ((System.Collections.IEnumerable)raw).Cast<object?>().Where(i => i is not System.Collections.IDictionary))
            {
                yield return $"{prefix}the {label} list has '{item}', which is not a row of keys (write it as {{ name: ... }}).";
            }
        }

        foreach (var (label, field) in lists.Take(3))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in RowsOfField(own, field))
            {
                var name = Text(row, "name") is { Length: > 0 } n ? n : Text(row, "id") is { Length: > 0 } i ? i : label + " row";
                var hasCheck = Text(row, "check").Length > 0;
                if (label == "other-way" && Text(row, "name").Length == 0) yield return $"{prefix}an other-way row has no name.";
                if (Text(row, "id") is { Length: > 0 } id && !seen.Add(id)) yield return $"{prefix}{label} route '{name}' repeats the id '{id}'.";
                if (row.ContainsKey("dc") && !WholeNumber(row["dc"], out _)) yield return $"{prefix}{label} route '{name}' has a dc that is not a whole number.";
                if (Text(row, "dc").Length > 0 && !hasCheck) yield return $"{prefix}{label} route '{name}' has a dc and no check.";
                if (!hasCheck && Text(row, "needs").Length == 0 && Text(row, "text").Length == 0)
                {
                    yield return $"{prefix}{label} route '{name}' has no check; it needs 'needs' or 'text' to say what it takes.";
                }

                if (Text(row, "after") is { Length: > 0 } after)
                {
                    if (!noticeIds.Contains(after)) yield return $"{prefix}{label} route '{name}' comes after '{after}', which is no detect route id.";
                    else if (after == Text(row, "id") && label == "detect") yield return $"{prefix}detect route '{name}' comes after itself.";
                }

                if (Text(row, "rank").Length > 0 && !ranked) yield return $"{prefix}{label} route '{name}' names a rank; this system declares no ranks in sheetRules.skills.ranks.";
                if (row.ContainsKey("successes") && (!WholeNumber(row["successes"], out var successes) || successes < 1)) yield return $"{prefix}{label} route '{name}' needs a whole number of successes, one or more.";
            }
        }

        // A detect route that waits for another must not wait in a circle.
        var waits = noticeRows.Where(r => r.Id != null && r.After != null).GroupBy(r => r.Id!).ToDictionary(g => g.Key, g => g.First().After!, StringComparer.Ordinal);
        foreach (var start in waits.Keys.Where(k => waits[k] != k))
        {
            var current = start;
            for (var steps = 0; steps < waits.Count && waits.TryGetValue(current, out var next); steps++)
            {
                current = next;
                if (current == start)
                {
                    yield return $"{prefix}detect route '{start}' waits, through other routes, for itself.";
                    break;
                }
            }
        }

        foreach (var row in RowsOfField(own, hazard.Effect?.Damage))
        {
            if (!IsDice(Text(row, "dice"))) yield return $"{prefix}a damage row has dice '{Text(row, "dice")}'; they must be dice such as 2d8.";
            var count = Text(row, "count");
            if (count.Length > 0 && !IsDice(count) && !WholeNumber(count, out _)) yield return $"{prefix}a damage row has count '{count}'; it must be dice or a whole number.";
        }

        var known = new HashSet<string>(system.Conditions, StringComparer.OrdinalIgnoreCase);
        foreach (var row in RowsOfField(own, hazard.Effect?.Conditions))
        {
            var name = Text(row, "name");
            if (name.Length == 0) yield return $"{prefix}a condition row has no name.";
            else if (!known.Contains(name)) yield return $"{prefix}condition '{name}' is not one of the system's conditions.";
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
