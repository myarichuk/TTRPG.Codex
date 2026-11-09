using System.Collections;
using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>A clock as the campaign keeps it. <see cref="Visibility"/> is <c>gm</c> or <c>players</c>.</summary>
public sealed record CampaignClock(string Name, int Segments, int Filled, string Visibility)
{
    public bool IsFull => Segments > 0 && Filled >= Segments;
}

/// <summary>A row of a campaign table: its name and its fields' values (absent: the field's default).</summary>
public sealed record CampaignRow(string Name, IReadOnlyDictionary<string, object?> Fields);

/// <summary>What applying intents did to the campaign's objects.</summary>
public sealed class CampaignIntentResult
{
    /// <summary>The state keys that changed (<c>clocks</c>, a table's key), each once, in order.</summary>
    public List<string> Changed { get; } = new();

    /// <summary>Intents the changes raised (a full clock's log line), in order. Nothing on the campaign answers them, so the
    /// table says them.</summary>
    public List<SheetIntent> Raised { get; } = new();

    /// <summary>Intents that named nothing the campaign has (a clock by a name it does not keep), in order.</summary>
    public List<SheetIntent> Left { get; } = new();

    /// <summary>Each change, for the session log: what it was on (a clock's or a row's name, and a field), and the number before
    /// and after.</summary>
    public List<(string Key, string Name, string? Field, double Was, double Now)> Changes { get; } = new();
}

/// <summary>
/// The campaign's own objects (overhaul section 3, "Campaign objects"): clocks, and tables of named rows (Blades' factions).
/// The state is a map like a sheet, stored by the campaign:
/// <code>
/// clocks: [{ name: the vault's alarm, segments: 6, filled: 4, visibility: gm }]
/// factions: [{ name: the city watch, status: -1 }]
/// </code>
/// The system says what is allowed (<see cref="CampaignObjectsDefinition"/>): clock sizes and who may see them, each table's
/// fields with their bounds. Intents reach them by a field rather than a track: <c>adjust { clock, by }</c>,
/// <c>set { clock, to }</c>, <c>adjust { table, row, field, by }</c>, <c>set { table, row, field, to }</c>. A clock that fills
/// raises <c>log { key: clock.full, args: [name] }</c>. Edits (a clock added, a row renamed) are copy-on-write, like
/// <see cref="SheetEdits"/>.
/// </summary>
public static class CampaignObjects
{
    public const string ClocksKey = "clocks";
    public const string FullLogKey = "clock.full";
    public const string GmOnly = "gm";
    public const string Players = "players";

    /// <summary>Where a problem with the campaign's objects is said to be: <c>campaign.clocks</c>, <c>campaign.factions</c>.</summary>
    public static string FieldOf(string key) => "campaign." + key;

    /// <summary>Whether an intent is for the campaign rather than a sheet: it names a clock or a table.</summary>
    public static bool IsFor(SheetIntent intent) => intent.Fields.ContainsKey("clock") || intent.Fields.ContainsKey("table");

    public static bool Has(SystemDefinition system) => system.Campaign is { } c && (c.Clocks != null || c.Tables.Count > 0);

    // ---- Reading ----

    public static IReadOnlyList<CampaignClock> Clocks(IReadOnlyDictionary<string, object>? state) =>
        Entries(state, ClocksKey).Select(Clock).ToList();

    private static CampaignClock Clock(IDictionary<string, object> entry) => new(
        entry.Value("name")?.ToString() ?? "",
        (int)(YamlValues.ToNumber(entry.Value("segments")) ?? 0),
        (int)(YamlValues.ToNumber(entry.Value("filled")) ?? 0),
        entry.Value("visibility")?.ToString() is { Length: > 0 } v ? v : GmOnly);

    public static IReadOnlyList<CampaignRow> Rows(SystemDefinition system, IReadOnlyDictionary<string, object>? state, string table)
    {
        var fields = TableOf(system, table)?.ItemFields ?? new List<ItemFieldDefinition>();
        return Entries(state, table).Select(entry => new CampaignRow(
            entry.Value("name")?.ToString() ?? "",
            fields.ToDictionary(f => f.Key, f => entry.TryGetValue(f.Key, out var value) ? Plain(value) : Plain(f.Default), StringComparer.Ordinal))).ToList();
    }

    public static CampaignTableDefinition? TableOf(SystemDefinition system, string key) =>
        system.Campaign?.Tables.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal));

    /// <summary>The state as a player may see it: no clock or table kept for the GM.</summary>
    public static Dictionary<string, object> Visible(SystemDefinition system, IReadOnlyDictionary<string, object>? state, bool isGm)
    {
        var copy = state == null ? new Dictionary<string, object>() : new Dictionary<string, object>(state);
        if (isGm) return copy;
        if (copy.ContainsKey(ClocksKey))
        {
            copy[ClocksKey] = Entries(state, ClocksKey).Where(c => !string.Equals(Clock(c).Visibility, GmOnly, StringComparison.OrdinalIgnoreCase)).Cast<object>().ToList();
        }

        foreach (var table in system.Campaign?.Tables ?? new List<CampaignTableDefinition>())
        {
            if (string.Equals(table.Visibility, GmOnly, StringComparison.OrdinalIgnoreCase)) copy.Remove(table.Key);
        }

        return copy;
    }

    // ---- Problems ----

    /// <summary>What is wrong with the campaign's objects by the system's rules: a clock of a size the system does not have, a
    /// clock filled past its segments, a visibility that is not allowed, two of a kind with one name, a row's field out of its
    /// bounds.</summary>
    public static IReadOnlyList<RuleViolation> Problems(SystemDefinition system, IReadOnlyDictionary<string, object>? state)
    {
        var problems = new List<RuleViolation>();
        if (state == null || system.Campaign is not { } rules) return problems;
        var clocksField = FieldOf(ClocksKey);
        if (rules.Clocks is { } clockRules)
        {
            var allowed = clockRules.Segments.Count > 0 ? string.Join(", ", clockRules.Segments) : "2 or more";
            foreach (var clock in Clocks(state))
            {
                if (clock.Segments < 2 || (clockRules.Segments.Count > 0 && !clockRules.Segments.Contains(clock.Segments)))
                {
                    problems.Add(new RuleViolation(clocksField, $"{clock.Name}: a clock has {allowed} segments, not {clock.Segments}.",
                        "Actor.Rule.ClockSegments", new object?[] { clock.Name, clock.Segments, allowed }));
                }

                if (clock.Filled < 0 || clock.Filled > clock.Segments)
                {
                    problems.Add(new RuleViolation(clocksField, $"{clock.Name}: {clock.Filled} filled of {clock.Segments}.",
                        "Actor.Rule.ClockFilled", new object?[] { clock.Name, clock.Filled, clock.Segments }));
                }

                if (!clockRules.Visibility.Contains(clock.Visibility, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(new RuleViolation(clocksField, $"{clock.Name}: a clock is seen by {string.Join(" or ", clockRules.Visibility)}, not {clock.Visibility}.",
                        "Actor.Rule.ClockVisibility", new object?[] { clock.Name, clock.Visibility }));
                }
            }

            Duplicates(problems, clocksField, Clocks(state).Select(c => c.Name));
        }

        foreach (var table in rules.Tables)
        {
            var field = FieldOf(table.Key);
            var rows = Rows(system, state, table.Key);
            foreach (var row in rows)
            {
                foreach (var definition in table.ItemFields)
                {
                    if (YamlValues.ToNumber(row.Fields.GetValueOrDefault(definition.Key)) is not { } value) continue;
                    if ((definition.Min is { } min && value < min) || (definition.Max is { } max && value > max))
                    {
                        var label = definition.Label ?? definition.Key;
                        var range = $"{Show(definition.Min)}..{Show(definition.Max)}";
                        problems.Add(new RuleViolation(field, $"{row.Name}: {label} {Show(value)} is outside {range}.",
                            "Actor.Rule.Range", new object?[] { row.Name, label, value, range }));
                    }
                }
            }

            Duplicates(problems, field, rows.Select(r => r.Name));
        }

        return problems;
    }

    private static void Duplicates(List<RuleViolation> problems, string field, IEnumerable<string> names)
    {
        foreach (var name in names.GroupBy(n => n.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1 || g.Key.Length == 0).Select(g => g.Key))
        {
            problems.Add(name.Length == 0
                ? new RuleViolation(field, "A campaign object has no name.", "Actor.Rule.CampaignUnnamed")
                : new RuleViolation(field, $"Two are named {name}.", "Actor.Rule.CampaignDuplicate", new object?[] { name }));
        }
    }

    private static string Show(double? value) => value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : "";

    /// <summary>What is wrong with the system's <c>campaign</c> section, as the loader reports it.</summary>
    public static IReadOnlyList<string> DefinitionProblems(SystemDefinition system)
    {
        var problems = new List<string>();
        if (system.Campaign is not { } rules) return problems;
        if (rules.Clocks is { } clocks)
        {
            foreach (var size in clocks.Segments.Where(s => s < 2)) problems.Add($"A clock has at least 2 segments, not {size}.");
            foreach (var who in clocks.Visibility.Where(v => !v.Equals(GmOnly, StringComparison.OrdinalIgnoreCase) && !v.Equals(Players, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"A clock is seen by gm or players, not \"{who}\".");
            }

            if (clocks.Visibility.Count == 0) problems.Add("Clocks name who may see them (gm, players).");
        }

        foreach (var table in rules.Tables)
        {
            if (string.IsNullOrWhiteSpace(table.Key)) problems.Add("A campaign table has no key.");
            else if (table.Key == ClocksKey) problems.Add($"A campaign table cannot be called \"{ClocksKey}\": that is where clocks are kept.");
            if (!table.Visibility.Equals(GmOnly, StringComparison.OrdinalIgnoreCase) && !table.Visibility.Equals(Players, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"The campaign table \"{table.Key}\" is seen by gm or players, not \"{table.Visibility}\".");
            }

            foreach (var field in table.ItemFields.Where(f => f.Key == "name")) problems.Add($"The campaign table \"{table.Key}\" has a field \"name\": a row's name is its own.");
        }

        foreach (var key in rules.Tables.GroupBy(t => t.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            problems.Add($"The campaign table \"{key}\" is defined twice.");
        }

        return problems;
    }

    // ---- Intents ----

    /// <summary>Applies the campaign's intents to <paramref name="state"/>, in order, writing new lists rather than changing
    /// the ones it holds (a page keeps the state as loaded). Numbers stay within their bounds: a clock between empty and full,
    /// a field within its <c>min</c> and <c>max</c>.</summary>
    public static CampaignIntentResult Apply(SystemDefinition system, Dictionary<string, object> state, IEnumerable<SheetIntent> intents)
    {
        var result = new CampaignIntentResult();
        foreach (var intent in intents)
        {
            if (!Apply(system, state, intent, result)) result.Left.Add(intent);
        }

        return result;
    }

    private static bool Apply(SystemDefinition system, Dictionary<string, object> state, SheetIntent intent, CampaignIntentResult result)
    {
        var fields = intent.Fields;
        var adjust = intent.Kind == SheetEvents.AdjustKind;
        if (!adjust && intent.Kind != SheetEvents.SetKind) return false;
        if (YamlValues.ToNumber(fields.GetValueOrDefault(adjust ? "by" : "to")) is not { } amount) return false;

        if (fields.GetValueOrDefault("clock")?.ToString() is { } name)
        {
            if (system.Campaign?.Clocks == null) return false;
            var entries = Entries(state, ClocksKey);
            var at = entries.FindIndex(e => Same(e.Value("name")?.ToString(), name));
            if (at < 0) return false;
            var clock = Clock(entries[at]);
            var now = (int)Math.Clamp(Math.Round(adjust ? clock.Filled + amount : amount), 0, Math.Max(clock.Segments, 0));
            if (now == clock.Filled) return true;
            entries[at] = new Dictionary<string, object>(entries[at]) { ["filled"] = (long)now };
            state[ClocksKey] = entries.Cast<object>().ToList();
            result.Changes.Add((ClocksKey, clock.Name, null, clock.Filled, now));
            Touch(result, ClocksKey);
            if (now >= clock.Segments && clock.Filled < clock.Segments)
            {
                result.Raised.Add(new SheetIntent(SheetEvents.LogKind, null, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["key"] = FullLogKey,
                    ["args"] = new List<object?> { clock.Name }
                }));
            }

            return true;
        }

        if (fields.GetValueOrDefault("table")?.ToString() is { } key && TableOf(system, key) is { } table
            && fields.GetValueOrDefault("row")?.ToString() is { } rowName
            && (fields.GetValueOrDefault("field")?.ToString() ?? table.ItemFields.FirstOrDefault(f => IsNumber(f))?.Key) is { } fieldKey
            && table.ItemFields.FirstOrDefault(f => f.Key == fieldKey) is { } field && IsNumber(field))
        {
            var rows = Entries(state, table.Key);
            var at = rows.FindIndex(r => Same(r.Value("name")?.ToString(), rowName));
            if (at < 0) return false;
            var was = YamlValues.ToNumber(rows[at].Value(field.Key)) ?? YamlValues.ToNumber(field.Default) ?? 0;
            var now = Math.Clamp(adjust ? was + amount : amount, field.Min ?? double.MinValue, field.Max ?? double.MaxValue);
            if (now == was) return true;
            rows[at] = new Dictionary<string, object>(rows[at]) { [field.Key] = Number(now) };
            state[table.Key] = rows.Cast<object>().ToList();
            result.Changes.Add((table.Key, rows[at].Value("name")?.ToString() ?? rowName, field.Label ?? field.Key, was, now));
            Touch(result, table.Key);
            return true;
        }

        return false;
    }

    private static bool IsNumber(ItemFieldDefinition field) => field.Type.Equals("number", StringComparison.OrdinalIgnoreCase);

    private static void Touch(CampaignIntentResult result, string key)
    {
        if (!result.Changed.Contains(key, StringComparer.Ordinal)) result.Changed.Add(key);
    }

    // ---- Edits ----

    /// <summary>Adds a clock; the first allowed size and visibility unless given. False when the name is taken or empty.</summary>
    public static bool AddClock(SystemDefinition system, Dictionary<string, object> state, string name, int? segments = null, string? visibility = null)
    {
        name = name.Trim();
        var rules = system.Campaign?.Clocks;
        if (rules == null || name.Length == 0 || Clocks(state).Any(c => Same(c.Name, name))) return false;
        var entries = Entries(state, ClocksKey);
        entries.Add(new Dictionary<string, object>
        {
            ["name"] = name,
            ["segments"] = (long)(segments ?? rules.Segments.FirstOrDefault(4)),
            ["filled"] = 0L,
            ["visibility"] = visibility ?? rules.Visibility.FirstOrDefault(GmOnly)
        });
        state[ClocksKey] = entries.Cast<object>().ToList();
        return true;
    }

    /// <summary>Changes a clock's size, visibility or name (null: as it was). A clock made smaller keeps no more filled than
    /// it has segments. False when there is no such clock, or the new name is another's.</summary>
    public static bool EditClock(Dictionary<string, object> state, string name, int? segments = null, string? visibility = null, string? rename = null)
    {
        var entries = Entries(state, ClocksKey);
        var at = entries.FindIndex(e => Same(e.Value("name")?.ToString(), name));
        if (at < 0) return false;
        rename = rename?.Trim();
        if (rename is { Length: 0 } || (rename != null && !Same(rename, name) && entries.Any(e => Same(e.Value("name")?.ToString(), rename)))) return false;
        var clock = Clock(entries[at]);
        var size = segments ?? clock.Segments;
        entries[at] = new Dictionary<string, object>(entries[at])
        {
            ["name"] = rename ?? clock.Name,
            ["segments"] = (long)size,
            ["filled"] = (long)Math.Clamp(clock.Filled, 0, Math.Max(size, 0)),
            ["visibility"] = visibility ?? clock.Visibility
        };
        state[ClocksKey] = entries.Cast<object>().ToList();
        return true;
    }

    public static bool RemoveClock(Dictionary<string, object> state, string name) => Remove(state, ClocksKey, name);

    /// <summary>Adds a row to a table, its fields at their defaults. False when the name is taken or empty.</summary>
    public static bool AddRow(SystemDefinition system, Dictionary<string, object> state, string table, string name)
    {
        name = name.Trim();
        if (TableOf(system, table) is not { } definition || name.Length == 0 || Entries(state, table).Any(r => Same(r.Value("name")?.ToString(), name))) return false;
        var rows = Entries(state, table);
        var row = new Dictionary<string, object> { ["name"] = name };
        foreach (var field in definition.ItemFields)
        {
            if (Plain(field.Default) is { } value) row[field.Key] = value is double d ? Number(d) : value;
        }

        rows.Add(row);
        state[table] = rows.Cast<object>().ToList();
        return true;
    }

    /// <summary>Sets a row's field from what was typed: a number field takes a number (empty: its default), the rest text.</summary>
    public static bool SetRowField(SystemDefinition system, Dictionary<string, object> state, string table, string row, string field, string? text)
    {
        if (TableOf(system, table)?.ItemFields.FirstOrDefault(f => f.Key == field) is not { } definition) return false;
        var rows = Entries(state, table);
        var at = rows.FindIndex(r => Same(r.Value("name")?.ToString(), row));
        if (at < 0) return false;
        var copy = new Dictionary<string, object>(rows[at]);
        if (string.IsNullOrWhiteSpace(text)) copy.Remove(field);
        else if (IsNumber(definition))
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
            copy[field] = Number(number);
        }
        else copy[field] = text.Trim();
        rows[at] = copy;
        state[table] = rows.Cast<object>().ToList();
        return true;
    }

    public static bool RenameRow(Dictionary<string, object> state, string table, string row, string rename)
    {
        rename = rename.Trim();
        var rows = Entries(state, table);
        var at = rows.FindIndex(r => Same(r.Value("name")?.ToString(), row));
        if (at < 0 || rename.Length == 0 || rows.Where((_, i) => i != at).Any(r => Same(r.Value("name")?.ToString(), rename))) return false;
        rows[at] = new Dictionary<string, object>(rows[at]) { ["name"] = rename };
        state[table] = rows.Cast<object>().ToList();
        return true;
    }

    public static bool RemoveRow(Dictionary<string, object> state, string table, string row) => Remove(state, table, row);

    private static bool Remove(Dictionary<string, object> state, string key, string name)
    {
        var entries = Entries(state, key);
        var removed = entries.RemoveAll(e => Same(e.Value("name")?.ToString(), name));
        if (removed == 0) return false;
        if (entries.Count == 0) state.Remove(key);
        else state[key] = entries.Cast<object>().ToList();
        return true;
    }

    // ---- Shapes ----

    /// <summary>A copy of the state deep enough that changing it cannot change the original: the lists and their rows.</summary>
    public static Dictionary<string, object> Copy(IReadOnlyDictionary<string, object>? state) =>
        state == null
            ? new Dictionary<string, object>()
            : state.ToDictionary(p => p.Key, p => YamlValues.Normalize(p.Value) is IEnumerable list and not string and not IDictionary<string, object>
                ? list.Cast<object?>().Select(e => e is IDictionary<string, object> map ? new Dictionary<string, object>(map) : e).Cast<object>().ToList()
                : p.Value, StringComparer.Ordinal);

    /// <summary>The rows under <paramref name="key"/>, each a new map over the stored one's values.</summary>
    private static List<IDictionary<string, object>> Entries(IReadOnlyDictionary<string, object>? state, string key) =>
        state != null && state.TryGetValue(key, out var raw) && YamlValues.Normalize(raw) is IEnumerable list and not string and not IDictionary<string, object>
            ? list.Cast<object?>().OfType<IDictionary<string, object>>().Select(e => (IDictionary<string, object>)new Dictionary<string, object>(e, StringComparer.Ordinal)).ToList()
            : new List<IDictionary<string, object>>();

    private static object? Value(this IDictionary<string, object> map, string key) => map.TryGetValue(key, out var value) ? value : null;

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static object? Plain(object? value) => YamlValues.ToNumber(value) is { } number && value is not bool ? number : YamlValues.Normalize(value);

    private static object Number(double number) => number == Math.Floor(number) && Math.Abs(number) < long.MaxValue ? (object)(long)number : number;
}
