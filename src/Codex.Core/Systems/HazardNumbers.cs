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
    public static IEnumerable<string> Problems(ContentEntry entry, ContentTypeDefinition type, SystemDefinition system, Func<string, object?[], string>? say = null)
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
                        ? Say(say, "RowNearKey", at, key, near)
                        : Say(say, "RowUnknownKey", at, key, type.Type);
                }
                else if (field.Type == FieldType.Collection && !IsList(value))
                {
                    yield return Say(say, "RowNotList", at, key);
                }
            }
        }

        var entryNumbers = (IReadOnlyDictionary<string, object>)entry.Properties;
        foreach (var message in SaveProblems(entryNumbers, hazard, say)) yield return message;
        foreach (var message in RoutineProblems(entryNumbers, hazard, say)) yield return message;
        foreach (var message in ListProblems(entryNumbers, entryNumbers, hazard, system, string.Empty, say)) yield return message;

        // A band restates whole lists (the Falling Net's methods, a pit's damage), so each of its lists is checked like the base's.
        foreach (var row in RowsOf(entry, type))
        {
            var levels = Text(row, "levels");
            var merged = For(entry, type, levels);
            var own = merged.Where(p => row.ContainsKey(p.Key) && p.Key != "levels").ToDictionary(p => p.Key, p => p.Value);
            foreach (var message in ListProblems(own, merged, hazard, system, Say(say, "Band", levels), say)) yield return message;
        }
    }

    private static string Say(Func<string, object?[], string>? say, string key, params object?[] args) => HazardMessages.Say(say, key, args);

    /// <summary>The row keys of <c>hazards.scaling</c> that are not columns.</summary>
    public const string ScalingLevelKey = "level";

    /// <inheritdoc cref="ScalingLevelKey"/>
    public const string ScalingSeverityKey = "severity";

    private static readonly Regex RangeText = new(@"^(\d+(?:\.\d+)?)\s*-\s*(\d+(?:\.\d+)?)$", RegexOptions.Compiled);

    /// <summary>A scaling cell as a range: a whole number or dice (by their average) as low = high, or <c>low-high</c>. Null when it is none of these.</summary>
    public static (double Low, double High)? Bounds(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (RangeText.Match(trimmed) is { Success: true } range)
        {
            return (double.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        return Number(trimmed) is { } single ? (single, single) : null;
    }

    /// <summary>The severity words the system's scaling rows use, in the order written; empty for a table without severities.</summary>
    public static IReadOnlyList<string> Severities(SystemDefinition system) =>
        (system.Hazards?.Scaling ?? []).Select(SeverityOfRow).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string SeverityOfRow(IReadOnlyDictionary<string, string> row) =>
        row.TryGetValue(ScalingSeverityKey, out var text) ? text?.Trim() ?? string.Empty : string.Empty;

    // One bound of a cell with the words it is shown in: a single cell keeps its own text (2d10), a range is shown as its numbers.
    private sealed record Bound(double Value, string Text);

    private sealed record GuideCell(Bound Low, Bound High);

    private sealed record GuideLevel(long Level, string Severity, Dictionary<string, GuideCell> Cells);

    private static string Shown(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    // The rows a hazard of this severity reads, by level: the rows of its severity (ignoring case); else, when it has none or the table does not use the word, the
    // rows with no severity; else every row. Rows at one level merge per column (lowest low, highest high), which only matters in the last case.
    private static IReadOnlyList<GuideLevel> ScalingRows(IEnumerable<Dictionary<string, string>> scaling, string? severity, out bool matched)
    {
        var parsed = scaling
            .Select(row => (Level: row.TryGetValue(ScalingLevelKey, out var text) && WholeNumber(text, out var n) ? (long?)n : null, Severity: SeverityOfRow(row), Row: row))
            .Where(r => r.Level != null)
            .ToList();
        var chosen = parsed.Where(r => !string.IsNullOrEmpty(severity) && string.Equals(r.Severity, severity, StringComparison.OrdinalIgnoreCase)).ToList();
        matched = chosen.Count > 0;
        if (!matched)
        {
            chosen = parsed.Where(r => r.Severity.Length == 0).ToList();
            if (chosen.Count == 0) chosen = parsed;
        }

        var levels = new List<GuideLevel>();
        foreach (var group in chosen.GroupBy(r => r.Level!.Value).OrderBy(g => g.Key))
        {
            var cells = new Dictionary<string, GuideCell>(StringComparer.Ordinal);
            foreach (var (_, _, row) in group)
            {
                foreach (var (column, text) in row.Where(p => p.Key != ScalingLevelKey && p.Key != ScalingSeverityKey))
                {
                    if (Bounds(text) is not { } b) continue;
                    var single = text.Trim();
                    var cell = b.Low == b.High && !RangeText.IsMatch(single)
                        ? new GuideCell(new Bound(b.Low, single), new Bound(b.High, single))
                        : new GuideCell(new Bound(b.Low, Shown(b.Low)), new Bound(b.High, Shown(b.High)));
                    cells[column] = cells.TryGetValue(column, out var held)
                        ? new GuideCell(held.Low.Value <= cell.Low.Value ? held.Low : cell.Low, held.High.Value >= cell.High.Value ? held.High : cell.High)
                        : cell;
                }
            }

            levels.Add(new GuideLevel(group.Key, matched ? group.First().Severity : string.Empty, cells));
        }

        return levels;
    }

    // The severity word the numbers of one band carry (the entry's own, with the band laid over it), or null.
    private static string? SeverityOf(IReadOnlyDictionary<string, object> numbers, HazardDefinition hazard) =>
        hazard.Severity is { Length: > 0 } field && numbers.TryGetValue(field, out var value) && value is not (System.Collections.IEnumerable and not string)
            && Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } word ? word : null;

    /// <summary>
    /// Where a hazard's numbers stand against the system's <c>hazards.scaling</c> (HAZARDS_DESIGN.md 3.2), for a hazard the world builder proposes or the wizard edits: one message for
    /// each scaled number (a column of the table the hazard also has, such as a save DC, or its damage) more than one row away from the row for the
    /// hazard's level, and one when a hazard with scaled numbers has no level to read. The level is the lowest whole number in the entry's level field
    /// (<c>5-10</c> reads as 5); the row is the highest one at or below it, or the first. A band of <c>higherLevels</c> is read at its own levels, and at its own severity
    /// (<see cref="For"/> lays the band over the entry). The rows are those of the band's severity; a hazard with none reads the rows with no severity, silently, and one with a
    /// word the table does not use reads the same rows with one warning. The window is the row before to the row after, among those rows; below the first row there is no floor.
    /// A cell may be a range, so a bound is the lowest low and the highest high in the window. These are warnings, never refusals: the game master decides, and
    /// the messages use the fields' labels, name the severity and state the bounds, for the game master and the model alike. Nothing here changes the entry. A system with no <c>hazards.scaling</c> has no messages.
    /// <paramref name="show"/> says one of the system's own words (the severity) in the reader's language, as a localized system's <see cref="SystemDefinition.DisplayValue"/> does;
    /// without it the word is the system's own. Each message is one whole key per shape (a number, a damage average against averages or against dice, with or without a severity).
    /// </summary>
    public static IEnumerable<string> ScalingProblems(ContentEntry entry, ContentTypeDefinition type, SystemDefinition system, Func<string, object?[], string>? say = null, Func<string, string>? show = null)
    {
        if (type.Hazard is not { } hazard || system.Hazards?.Scaling is not { Count: > 0 } scaling) yield break;
        if (ScalingRows(scaling, null, out _).Count == 0) yield break;
        var columns = scaling.SelectMany(r => r.Keys).Where(k => k != ScalingLevelKey && k != ScalingSeverityKey).Distinct(StringComparer.Ordinal).ToList();
        var known = Severities(system);
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string LabelOf(string key) => type.Fields.FirstOrDefault(f => f.Key == key)?.Label is { Length: > 0 } label ? label : key;
        var levelLabel = hazard.Level is { Length: > 0 } levelKey ? LabelOf(levelKey) : Say(say, "LevelWord");

        var bands = new List<(string Prefix, string? Levels, string LevelText)> { (string.Empty, null, hazard.Level is { Length: > 0 } field && entry.Properties.TryGetValue(field, out var own) ? Convert.ToString(own, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty) };
        bands.AddRange(Rows(entry, type).Select(levels => (Say(say, "Band", levels), (string?)levels, levels)));
        foreach (var (prefix, levels, levelText) in bands)
        {
            var numbers = For(entry, type, levels);
            var measured = columns.Select(c => (Column: c, Value: Measure(numbers, c, hazard))).Where(m => m.Value != null).ToList();
            if (measured.Count == 0) continue;
            if (LowestLevel(levelText) is not { } level)
            {
                yield return Say(say, "NoLevel", prefix, string.Join(", ", measured.Select(m => LabelOf(m.Column))), levelLabel);
                continue;
            }

            var severity = SeverityOf(numbers, hazard);
            var rows = ScalingRows(scaling, severity, out var matched);
            if (!matched && severity != null && known.Count > 0 && unknown.Add(severity))
            {
                yield return Say(say, "UnknownSeverity", prefix, severity, string.Join(", ", known.Select(k => show?.Invoke(k) ?? k)));
            }

            var at = rows.ToList().FindLastIndex(r => r.Level <= level);
            if (at < 0) at = 0;
            var window = rows.Skip(Math.Max(at - 1, 0)).Take(Math.Min(at + 1, rows.Count - 1) - Math.Max(at - 1, 0) + 1).ToList();
            var named = matched && rows[at].Severity is { Length: > 0 } word ? show?.Invoke(word) ?? word : null;
            foreach (var (column, value) in measured)
            {
                var cells = window.Select(r => r.Cells.GetValueOrDefault(column)).OfType<GuideCell>().ToList();
                if (cells.Count == 0) continue;
                var low = cells.Select(c => c.Low).MinBy(b => b.Value)!;
                var high = cells.Select(c => c.High).MaxBy(b => b.Value)!;
                var isDamage = string.Equals(hazard.Effect?.Damage, column, StringComparison.Ordinal);
                var number = value.GetValueOrDefault().ToString("0.#", CultureInfo.InvariantCulture);
                var shape = !isDamage ? string.Empty : IsDice(low.Text) || IsDice(high.Text) ? ".Dice" : ".Average";
                var tail = shape + (named != null ? ".Severity" : string.Empty);
                var args = new object?[] { prefix, LabelOf(column), number, level, low.Text, high.Text, named };
                if (value > high.Value) yield return Say(say, "High" + tail, args);
                else if (at > 0 && value < low.Value) yield return Say(say, "Low" + tail, args);
            }
        }
    }

    /// <summary>
    /// The row of the system's <c>hazards.scaling</c> for a level text (<c>5</c>, or a range such as <c>5-10</c>, which reads as its lowest number) and a severity: the highest
    /// row at or below the level among the rows of that severity, or among those with none. Null when the system has no table or the text has no number. A cell that is a
    /// range gives one value inside it: its middle, as dice of the die the severity-less row uses for the column when it has dice. The homebrew wizard fills a new hazard's
    /// numbers from it and they stay editable; it is the reading <see cref="ScalingProblems"/> measures against.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? GuideRow(string? levelText, SystemDefinition system, string? severity = null)
    {
        if (system.Hazards?.Scaling is not { Count: > 0 } scaling || LowestLevel(levelText ?? string.Empty) is not { } level) return null;
        var plain = ScalingRows(scaling, null, out _);
        var chosen = ScalingRows(scaling, severity, out _);
        if (chosen.Count == 0) return null;
        static GuideLevel RowAt(IReadOnlyList<GuideLevel> rows, long level) => rows[Math.Max(rows.ToList().FindLastIndex(r => r.Level <= level), 0)];
        var row = RowAt(chosen, level);
        var fallback = plain.Count == 0 ? null : RowAt(plain, level);
        var result = new Dictionary<string, string>(StringComparer.Ordinal) { [ScalingLevelKey] = row.Level.ToString(CultureInfo.InvariantCulture) };
        foreach (var (column, cell) in row.Cells)
        {
            if (cell.Low.Value == cell.High.Value) result[column] = cell.Low.Text;
            else if (MiddleOf(cell, fallback?.Cells.GetValueOrDefault(column)?.Low.Text) is { } text) result[column] = text;
        }

        return result;
    }

    // A value inside a range cell: dice of the single-value row's die when it has one (the nearest count to the middle, kept inside the range), else the middle as a whole number.
    private static string? MiddleOf(GuideCell cell, string? singleValueText)
    {
        var middle = (cell.Low.Value + cell.High.Value) / 2;
        if (Regex.Match(singleValueText ?? string.Empty, @"^\d+d(\d+)$") is { Success: true } die)
        {
            var average = (double.Parse(die.Groups[1].Value, CultureInfo.InvariantCulture) + 1) / 2;
            var count = Math.Max(1, (int)Math.Round(middle / average, MidpointRounding.AwayFromZero));
            var total = count * average;
            return total >= cell.Low.Value && total <= cell.High.Value ? $"{count}d{die.Groups[1].Value}" : null;
        }

        var whole = Math.Round(middle, MidpointRounding.AwayFromZero);
        return whole >= cell.Low.Value && whole <= cell.High.Value && cell.Low.Value % 1 == 0 && cell.High.Value % 1 == 0 ? Shown(whole) : null;
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
    private static IEnumerable<string> RoutineProblems(IReadOnlyDictionary<string, object> numbers, HazardDefinition hazard, Func<string, object?[], string>? say)
    {
        var strikes = 0;
        foreach (var row in RowsOfField(numbers, hazard.Routine))
        {
            if (!row.TryGetValue("strikes", out var raw) || raw == null) continue;
            var name = Text(row, "name") is { Length: > 0 } n ? n : Say(say, "RoutineRow");
            if (raw is not bool && !bool.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out _))
            {
                yield return Say(say, "StrikesBad", name, raw);
            }
            else if (IsTrue(raw))
            {
                strikes++;
                if (!HasEffect(numbers, hazard)) yield return Say(say, "StrikesNoEffect", name);
            }
        }

        if (strikes > 1) yield return Say(say, "StrikesMany");
    }

    private static readonly string[] OnSaveValues = ["half", "none"];

    // The save, its DC and what a passed save does: one save has all of them; with none, onSave means nothing. An absent onSave is "none".
    private static IEnumerable<string> SaveProblems(IReadOnlyDictionary<string, object> numbers, HazardDefinition hazard, Func<string, object?[], string>? say)
    {
        if (hazard.Effect is not { } effect) yield break;
        bool Has(string? field) => !string.IsNullOrEmpty(field) && numbers.TryGetValue(field, out var v) && Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim().Length > 0;
        var save = Has(effect.Save);
        if (save && !Has(effect.SaveDc)) yield return Say(say, "SaveNoDc");
        if (!save && Has(effect.SaveDc)) yield return Say(say, "DcNoSave");
        if (Has(effect.OnSave))
        {
            var value = Convert.ToString(numbers[effect.OnSave!], CultureInfo.InvariantCulture)!.Trim();
            if (!OnSaveValues.Contains(value, StringComparer.Ordinal)) yield return Say(say, "OnSaveBad", value);
            if (!save) yield return Say(say, "OnSaveNoSave");
        }
    }

    // The route, damage and condition lists among `own` (the whole entry, or the keys a band restates). `all` is what a route's `after` is checked against.
    private static IEnumerable<string> ListProblems(IReadOnlyDictionary<string, object> own, IReadOnlyDictionary<string, object> all, HazardDefinition hazard,
        SystemDefinition system, string prefix, Func<string, object?[], string>? say)
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
                yield return Say(say, "ListNotRows." + label, prefix, field);
                continue;
            }

            foreach (var item in ((System.Collections.IEnumerable)raw).Cast<object?>().Where(i => i is not System.Collections.IDictionary))
            {
                yield return Say(say, "ItemNotRow." + label, prefix, item);
            }
        }

        foreach (var (label, field) in lists.Take(3))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in RowsOfField(own, field))
            {
                // One whole message per kind of route (the key's last part), so a translation words each one in full.
                var name = Text(row, "name") is { Length: > 0 } n ? n : Text(row, "id") is { Length: > 0 } i ? i : Say(say, "RowWord." + label);
                var hasCheck = Text(row, "check").Length > 0;
                if (label == "other-way" && Text(row, "name").Length == 0) yield return Say(say, "OtherWayNoName", prefix);
                if (Text(row, "id") is { Length: > 0 } id && !seen.Add(id)) yield return Say(say, "RepeatId." + label, prefix, name, id);
                if (row.ContainsKey("dc") && !WholeNumber(row["dc"], out _)) yield return Say(say, "DcNotWhole." + label, prefix, name);
                if (Text(row, "dc").Length > 0 && !hasCheck) yield return Say(say, "DcNoCheck." + label, prefix, name);
                if (!hasCheck && Text(row, "needs").Length == 0 && Text(row, "text").Length == 0)
                {
                    yield return Say(say, "NoCheck." + label, prefix, name);
                }

                if (Text(row, "after") is { Length: > 0 } after)
                {
                    if (!noticeIds.Contains(after)) yield return Say(say, "AfterNoRoute." + label, prefix, name, after);
                    else if (after == Text(row, "id") && label == "detect") yield return Say(say, "AfterItself", prefix, name);
                }

                if (Text(row, "rank").Length > 0 && !ranked) yield return Say(say, "RankNone." + label, prefix, name);
                if (row.ContainsKey("successes") && (!WholeNumber(row["successes"], out var successes) || successes < 1)) yield return Say(say, "SuccessesBad." + label, prefix, name);
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
                    yield return Say(say, "Circle", prefix, start);
                    break;
                }
            }
        }

        foreach (var row in RowsOfField(own, hazard.Effect?.Damage))
        {
            if (!IsDice(Text(row, "dice"))) yield return Say(say, "DamageDice", prefix, Text(row, "dice"));
            var count = Text(row, "count");
            if (count.Length > 0 && !IsDice(count) && !WholeNumber(count, out _)) yield return Say(say, "DamageCount", prefix, count);
        }

        var known = new HashSet<string>(system.Conditions, StringComparer.OrdinalIgnoreCase);
        foreach (var row in RowsOfField(own, hazard.Effect?.Conditions))
        {
            var name = Text(row, "name");
            if (name.Length == 0) yield return Say(say, "ConditionNoName", prefix);
            else if (!known.Contains(name)) yield return Say(say, "ConditionUnknown", prefix, name);
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
