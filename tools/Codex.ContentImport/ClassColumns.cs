using System.Globalization;
using System.Text.Json;

namespace Codex.ContentImport;

/// <summary>
/// The per-level numbers of a class table that the 5e-bits Levels file carries as <c>class_specific</c> (<c>rage_count</c>,
/// <c>channel_divinity_charges</c>...), and the formula that gives each one back from the class level. Only the main class levels count
/// (the Levels file's subclass levels are left out by the caller).
/// </summary>
public sealed class ClassColumns
{
    private readonly Dictionary<(string ClassId, string Column), SortedDictionary<int, int>> _values = new();

    /// <summary>Reads <c>class_specific</c> of each level record of a Levels file: <c>[{ "level": 3, "class": { "index": "barbarian" }, "class_specific": { "rage_count": 3 } }]</c>.
    /// A record with a subclass is skipped.</summary>
    public static ClassColumns Read(IEnumerable<JsonElement> levels)
    {
        var columns = new ClassColumns();
        foreach (var record in levels)
        {
            if (record.Get("subclass") != null) continue;
            var classId = record.Get("class")?.Str("index");
            var level = record.Int("level");
            if (classId == null || level == null || record.Get("class_specific") is not { ValueKind: JsonValueKind.Object } specific) continue;
            foreach (var column in specific.EnumerateObject())
            {
                if (column.Value.ValueKind != JsonValueKind.Number) continue;
                if (!columns._values.TryGetValue((classId, column.Name), out var byLevel)) columns._values[(classId, column.Name)] = byLevel = new SortedDictionary<int, int>();
                byLevel[level.Value] = (int)Math.Round(column.Value.GetDouble());
            }
        }

        return columns;
    }

    /// <summary>Whether the file has the column for the class.</summary>
    public bool Has(string classId, string column) => _values.ContainsKey((classId, column));

    /// <summary>The value at a level (null when the file has none for it).</summary>
    public int? ValueAt(string classId, string column, int level) =>
        _values.TryGetValue((classId, column), out var byLevel) && byLevel.TryGetValue(level, out var value) ? value : null;

    /// <summary>
    /// A formula over <c>classLevel</c> that gives the column's value at every level it has: <c>classLevel</c> when it equals the level, a plain
    /// number when it never changes, else a step formula, highest level first
    /// (<c>classLevel &gt;= 17 ? 6 : classLevel &gt;= 12 ? 5 : 4</c>). Levels below the first the file has take that first value.
    /// </summary>
    public string Formula(string classId, string column)
    {
        // A 0 is "the class has none yet" (Focus Points at level 1): the feature's own level says where it starts, so it is left out.
        if (!_values.TryGetValue((classId, column), out var all) || all.Values.All(v => v <= 0))
        {
            throw new InvalidDataException($"The Levels file has no \"{column}\" for {classId}.");
        }

        var byLevel = new SortedDictionary<int, int>(all.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value));
        if (byLevel.All(p => p.Key == p.Value)) return "classLevel";

        // Where the value changes: each step is a level from which a new value holds.
        var steps = new List<(int From, int Value)>();
        foreach (var (level, value) in byLevel)
        {
            if (steps.Count == 0 || steps[^1].Value != value) steps.Add((level, value));
        }

        if (steps.Count == 1) return steps[0].Value.ToString(CultureInfo.InvariantCulture);

        var formula = steps[0].Value.ToString(CultureInfo.InvariantCulture);
        for (var i = 1; i < steps.Count; i++)
        {
            formula = $"classLevel >= {steps[i].From.ToString(CultureInfo.InvariantCulture)} ? {steps[i].Value.ToString(CultureInfo.InvariantCulture)} : " + formula;
        }

        return formula;
    }
}
