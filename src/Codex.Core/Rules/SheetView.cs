using System.Collections;
using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>One number or choice of a <c>values</c> widget. <see cref="Input"/> is entered on the sheet; an attribute's score is
/// entered too.</summary>
public sealed record SheetValueView(string Key, string Label, object? Value, bool Input, IReadOnlyList<string>? Options);

/// <summary>A track as a <c>track</c> widget shows it: the definition and bounds, the current number, and for a slot track what
/// its slots hold (by level when it has levels).</summary>
public sealed record SheetTrackView(SheetTrack Track, double Current, IReadOnlyList<string> Held, IReadOnlyDictionary<int, IReadOnlyList<string>> HeldByLevel)
{
    public bool IsBoxes => Track.Definition.Display.Equals("boxes", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A cell of a <c>list</c> or <c>table</c> widget: its value, and the per-pick field it edits (null: worked out or read from
/// the entry).</summary>
public sealed record SheetCellView(string Column, object? Value, ItemFieldDefinition? Field);

public sealed record SheetRowView(string Id, string Label, string? Group, int? Index, IReadOnlyList<SheetCellView> Cells);

/// <summary>
/// A sheet as the layout's generic widgets read it (overhaul step 6): the sheet kind's values, tracks, rows and modifier
/// breakdowns, worked out once per edit by <see cref="SheetModel"/>. The page renders these; it computes nothing itself, so what
/// a widget shows is what a test of this class sees. Edits go through <see cref="SheetEdits"/>.
/// </summary>
public sealed class SheetView
{
    private readonly SystemDefinition _system;
    private readonly SheetModel _model;

    public SheetKindDefinition? Kind => _model.Kind;
    public SheetModelResult Result { get; }

    public SheetView(SystemDefinition system, IEntryResolver resolver, IReadOnlyDictionary<string, object> sheet)
    {
        _system = system;
        Sheet = sheet;
        _model = new SheetModel(system, resolver, sheet);
        Result = _model.Evaluate();
    }

    public IReadOnlyDictionary<string, object> Sheet { get; }

    public IReadOnlyList<SheetValueView> Values(LayoutWidget widget) => widget.Keys.Select(Value).ToList();

    public SheetValueView Value(string key)
    {
        var definition = Kind?.Values.FirstOrDefault(v => v.Key == key);
        var attribute = _system.Attributes.FirstOrDefault(a => a.Key == key);
        var label = definition?.Label ?? attribute?.Label ?? TrackLabel(key) ?? key;
        object? value = Result.Values.TryGetValue(key, out var worked) ? worked
            : attribute != null ? YamlValues.ToNumber(Sheet.GetValueOrDefault(key)) ?? attribute.Default
            : _model.Path(key);
        return new SheetValueView(key, label, value, definition?.Input == true || attribute != null, definition?.Options);
    }

    private string? TrackLabel(string key) =>
        key.EndsWith(SheetModel.MaxSuffix, StringComparison.Ordinal) && Kind?.Tracks.FirstOrDefault(t => t.Key + SheetModel.MaxSuffix == key) is { } track
            ? track.Label ?? track.Key
            : null;

    /// <summary>The tracks a <c>track</c> widget names: one, or every generated one its <c>prefix*</c> starts.</summary>
    public IReadOnlyList<SheetTrackView> Tracks(LayoutWidget widget) =>
        widget.Key is not { } key ? Array.Empty<SheetTrackView>()
            : _model.TrackKeys().Where(k => SheetLayout.Matches(key, k)).Select(k => _model.TrackOf(k)).OfType<SheetTrack>().Select(Track).ToList();

    private SheetTrackView Track(SheetTrack track)
    {
        var raw = Sheet.GetValueOrDefault(track.Key);
        var current = YamlValues.ToNumber(raw) ?? track.Start ?? Math.Clamp(0, track.Min ?? double.MinValue, track.Max ?? double.MaxValue);
        var held = new List<string>();
        var byLevel = new Dictionary<int, IReadOnlyList<string>>();
        if (track.IsSlots)
        {
            if (track.Levels.Count > 0 && YamlValues.Normalize(raw) is IDictionary<string, object> levels)
            {
                foreach (var (level, texts) in levels)
                {
                    if (int.TryParse(level, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) byLevel[number] = Texts(texts);
                }
            }
            else
            {
                held = Texts(raw);
            }
        }

        return new SheetTrackView(track, current, held, byLevel);
    }

    private static List<string> Texts(object? value) => value is IEnumerable list and not string
        ? list.Cast<object?>().Select(o => o?.ToString()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList()
        : new List<string>();

    /// <summary>The rows of a <c>list</c> or <c>table</c> widget, with its columns; a column that is a per-pick field is editable.</summary>
    public IReadOnlyList<SheetRowView> Rows(LayoutWidget widget)
    {
        if (widget.Key is not { } key || Kind == null) return Array.Empty<SheetRowView>();
        var fields = (Kind.Lists.FirstOrDefault(l => l.Key == key)?.ItemFields ?? Kind.KeyedLists.FirstOrDefault(l => l.Key == key)?.ItemFields ?? new List<ItemFieldDefinition>())
            .ToDictionary(f => f.Key, StringComparer.Ordinal);
        return _model.Rows(key, widget.Columns, widget.GroupBy)
            .Select(r => new SheetRowView(r.Id, r.Label, r.Group, r.Index, widget.Columns.Select(c => new SheetCellView(c, r.Cells.GetValueOrDefault(c), fields.GetValueOrDefault(c))).ToList()))
            .ToList();
    }

    /// <summary>The entry type a pick list holds, for its "add" choices; null for a keyed list or a list of plain records.</summary>
    public string? ListType(string key) => Kind?.Lists.FirstOrDefault(l => l.Key == key)?.Type;

    public bool IsKeyedList(string key) => Kind?.KeyedLists.Any(l => l.Key == key) == true;

    /// <summary>The breakdown of a value's modifiers: each that reached it, and whether it counted under its type's stacking.</summary>
    public IReadOnlyList<AppliedModifier> Modifiers(string target) => Result.Modifiers.GetValueOrDefault(target) ?? Array.Empty<AppliedModifier>();

    /// <summary>Whether a widget has anything to show, so a tab of empty widgets is not offered.</summary>
    public bool Shows(LayoutWidget widget) => widget.Widget.ToLowerInvariant() switch
    {
        "values" => widget.Keys.Count > 0,
        "track" => Tracks(widget).Count > 0,
        "list" or "table" => widget.Key != null && (Kind?.Lists.Any(l => l.Key == widget.Key) == true || IsKeyedList(widget.Key)),
        "roll" => _system.Rolls.Any(r => r.Key == widget.Roll),
        "text" => widget.Key != null,
        "modifiers" => widget.Target != null,
        _ => IsCompiled(widget)
    };

    /// <summary>Whether the widget is one the system's DLL supplies (<see cref="SystemDefinition.Widgets"/>); a host widget of the
    /// same name is the host's.</summary>
    public bool IsCompiled(LayoutWidget widget) =>
        _system.Widgets.ContainsKey(widget.Widget) && !SheetLayout.GenericWidgets.Contains(widget.Widget) && !SheetLayout.BuiltinWidgets.ContainsKey(widget.Widget);

    /// <summary>What a compiled widget shows (<see cref="CompiledSuppliers.Widget"/>), handed the sheet as a handler reads it, or
    /// why it cannot be shown.</summary>
    /// <param name="language">The reader's language tag, for the widget's own text.</param>
    public (WidgetContent? Content, string? Failure) Compiled(LayoutWidget widget, string language)
    {
        try
        {
            var label = widget.Label ?? (_system.Widgets.TryGetValue(widget.Widget, out var definition) ? definition.Label : null);
            var request = new WidgetRequest(SheetEvents.ScriptView(_system, _model, Sheet), widget.Key, widget.Keys, label, language);
            return (CompiledSuppliers.Widget(_system, widget.Widget, request), null);
        }
        catch (FormulaException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>A number as a sheet shows it: whole numbers without a decimal point.</summary>
    public static string Format(object? value, IFormatProvider? culture = null) => value switch
    {
        null => "—",
        bool b => b ? "✓" : "—",
        double d when d == Math.Floor(d) && Math.Abs(d) < 1e15 => ((long)d).ToString(culture ?? CultureInfo.CurrentCulture),
        double d => d.ToString("0.##", culture ?? CultureInfo.CurrentCulture),
        long l => l.ToString(culture ?? CultureInfo.CurrentCulture),
        _ => value.ToString() ?? ""
    };
}

/// <summary>
/// Edits a generic widget makes to a sheet. Each writes a new list or map rather than changing the one the sheet holds, since the
/// page keeps the sheet as loaded to count what changed, and a nested list changed in place would change both.
/// </summary>
public static class SheetEdits
{
    /// <summary>An entered value: a number when it reads as one and the value is not a choice, else the text; empty clears it.</summary>
    public static void SetValue(Dictionary<string, object> sheet, string key, string? text, bool isChoice)
    {
        if (string.IsNullOrWhiteSpace(text)) sheet.Remove(key);
        else if (!isChoice && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) sheet[key] = Number(number);
        else sheet[key] = text.Trim();
    }

    private static object Number(double number) => number == Math.Floor(number) && Math.Abs(number) < long.MaxValue ? (object)(long)number : number;

    /// <summary>A per-pick or per-key field: for a pick list, the pick at <paramref name="index"/>; for a keyed list, the entry
    /// <paramref name="id"/>.</summary>
    public static void SetField(Dictionary<string, object> sheet, string listKey, bool keyed, string id, int? index, ItemFieldDefinition field, object? value)
    {
        var typed = Typed(field, value);
        if (keyed)
        {
            var map = YamlValues.Normalize(sheet.GetValueOrDefault(listKey)) as Dictionary<string, object> ?? new Dictionary<string, object>();
            var entry = map.GetValueOrDefault(id) as Dictionary<string, object> ?? new Dictionary<string, object>();
            entry = new Dictionary<string, object>(entry);
            if (typed == null) entry.Remove(field.Key); else entry[field.Key] = typed;
            map = new Dictionary<string, object>(map) { [id] = entry };
            sheet[listKey] = map;
            return;
        }

        var picks = Picks(sheet, listKey);
        if (index is not { } at || at < 0 || at >= picks.Count) return;
        var pick = new Dictionary<string, object>(picks[at]);
        if (typed == null) pick.Remove(field.Key); else pick[field.Key] = typed;
        picks[at] = pick;
        sheet[listKey] = picks.Cast<object>().ToList();
    }

    public static void AddPick(Dictionary<string, object> sheet, string listKey, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var picks = Picks(sheet, listKey);
        picks.Add(new Dictionary<string, object> { ["id"] = id });
        sheet[listKey] = picks.Cast<object>().ToList();
    }

    public static void RemovePick(Dictionary<string, object> sheet, string listKey, int index)
    {
        var picks = Picks(sheet, listKey);
        if (index < 0 || index >= picks.Count) return;
        picks.RemoveAt(index);
        if (picks.Count == 0) sheet.Remove(listKey);
        else sheet[listKey] = picks.Cast<object>().ToList();
    }

    // A pick stored as its bare id is read as { id }.
    private static List<Dictionary<string, object>> Picks(Dictionary<string, object> sheet, string listKey) =>
        YamlValues.Normalize(sheet.GetValueOrDefault(listKey)) is List<object> list
            ? list.Select(p => p as Dictionary<string, object> ?? new Dictionary<string, object> { ["id"] = p.ToString() ?? "" }).ToList()
            : new List<Dictionary<string, object>>();

    private static object? Typed(ItemFieldDefinition field, object? value) => field.Type.ToLowerInvariant() switch
    {
        "boolean" or "bool" => value is true or "true" or "on",
        "number" => value is string text ? double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? Number(n) : null
            : YamlValues.ToNumber(value) is { } number ? Number(number) : null,
        _ => string.IsNullOrWhiteSpace(value?.ToString()) ? null : value!.ToString()!.Trim()
    };
}
