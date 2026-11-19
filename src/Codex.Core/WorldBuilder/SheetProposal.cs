using System.Globalization;
using System.Text.Json;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;

namespace Codex.Core.WorldBuilder;

/// <summary>How a sheet key's value is written in a sheet edit.</summary>
public enum SheetKeyKind
{
    /// <summary>A line or a paragraph of text.</summary>
    Text,
    /// <summary>A number, within the key's bounds; a whole number for an attribute.</summary>
    Number,
    /// <summary>true or false.</summary>
    Boolean,
    /// <summary>One of the key's options.</summary>
    Choice,
    /// <summary>The id of a Library entry of the key's type (a class, a background).</summary>
    Reference,
    /// <summary>A list of Library entries, each as <c>id</c> or <c>id|choice:n</c> (the inventory, a spell list).</summary>
    Picks,
    /// <summary>A list of short words.</summary>
    Tags
}

/// <summary>A sheet key a proposal may set or clear.</summary>
/// <param name="TargetTypes">For <see cref="SheetKeyKind.Reference"/> and <see cref="SheetKeyKind.Picks"/>: the entity types an id may be of.</param>
public sealed record SheetKey(string Key, string Label, SheetKeyKind Kind, double? Min, double? Max, IReadOnlyList<string>? Options, IReadOnlyList<string> TargetTypes, bool Integer = false);

/// <summary>
/// A change to one character's sheet, proposed through the review gate (<c>existing:sheet:&lt;actorId&gt;</c>). Deliberately narrow: only the keys the
/// system declares for its sheet can be set or cleared (<see cref="EditableKeys"/>: attributes, actor fields, the inventory and the
/// system's own lists), every value is checked against the key's own type, bounds and options and against the Library, and the whole
/// change is run through the system's sheet rules (<see cref="Check"/>): it is refused when it would introduce a rule violation the
/// sheet did not have. Derived values (hit points, proficiency, armor class), rows of a collection and images are never set by a proposal.
/// Pure: no database, no model.
/// </summary>
public static class SheetProposal
{
    /// <summary>The most keys one sheet edit changes.</summary>
    public const int MaxKeys = 30;

    /// <summary>The most entries one list holds.</summary>
    public const int MaxItems = 100;

    /// <summary>The longest text a key is set to.</summary>
    public const int MaxText = 4000;

    /// <summary>The keys a proposal may change, sorted by key (ordinal), each once. None for a system with no sheet to speak of.</summary>
    public static IReadOnlyList<SheetKey> EditableKeys(SystemDefinition? system)
    {
        var keys = new Dictionary<string, SheetKey>(StringComparer.OrdinalIgnoreCase);
        if (system == null) return Array.Empty<SheetKey>();

        foreach (var a in system.Attributes)
        {
            keys[a.Key] = new SheetKey(a.Key, a.Label, SheetKeyKind.Number, a.Min, a.Max, null, Array.Empty<string>(), Integer: true);
        }

        foreach (var f in system.ActorFields)
        {
            var kind = f.Type switch
            {
                FieldType.Text or FieldType.LongText => SheetKeyKind.Text,
                FieldType.Number => SheetKeyKind.Number,
                FieldType.Boolean => SheetKeyKind.Boolean,
                FieldType.Dropdown => SheetKeyKind.Choice,
                FieldType.EntityReference when !string.IsNullOrEmpty(f.TargetEntityType) => SheetKeyKind.Reference,
                FieldType.TagList => SheetKeyKind.Tags,
                _ => (SheetKeyKind?)null
            };
            if (kind == null || f.Key == SheetModel.KindKey) continue;
            keys[f.Key] = new SheetKey(f.Key, f.Label, kind.Value, f.Min, f.Max, f.Options, kind == SheetKeyKind.Reference ? new[] { f.TargetEntityType! } : Array.Empty<string>());
        }

        if (system.SheetRules?.Inventory is { } inventory)
        {
            keys[inventory.Key] = new SheetKey(inventory.Key, inventory.Label, SheetKeyKind.Picks, null, null, null, inventory.Types.OrderBy(t => t, StringComparer.Ordinal).ToList());
        }

        foreach (var list in system.SheetRules?.Lists ?? new List<ListRule>())
        {
            if (string.IsNullOrEmpty(list.TargetEntityType)) continue;
            keys[list.Key] = new SheetKey(list.Key, list.Label, SheetKeyKind.Picks, null, null, null, new[] { list.TargetEntityType });
        }

        return keys.Values.OrderBy(k => k.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>The editable key a name stands for (any case), or null.</summary>
    public static SheetKey? Find(SystemDefinition? system, string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : EditableKeys(system).FirstOrDefault(k => string.Equals(k.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a sheet edit can set or clear <paramref name="key"/>: what the checker uses to say a sheet issue is fixable.</summary>
    public static bool IsEditable(SystemDefinition? system, string? key) => Find(system, key) != null;

    /// <summary>The change as a sheet edit entry spells it: <c>{"key": value, "other": null}</c> (null clears the key). Shape only (type, bounds,
    /// options); the Library and the rules are <see cref="Check"/>'s. Errors name the entry by <paramref name="label"/>.</summary>
    /// <returns>New values as JSON text (a number, a string, true or false, an array of strings), and the keys to clear, each sorted by key.</returns>
    public static (SortedDictionary<string, string> Set, List<string> Clear) Read(SystemDefinition? system, JsonElement sheet, string label, List<string> errors)
    {
        var set = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var clear = new List<string>();
        if (sheet.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{label}: \"sheet\" must be an object of the sheet keys to change, e.g. {{\"{EditableKeys(system).FirstOrDefault()?.Key ?? "key"}\": value}}; null clears a key.");
            return (set, clear);
        }

        var editable = EditableKeys(system);
        var count = 0;
        foreach (var property in sheet.EnumerateObject())
        {
            if (++count > MaxKeys)
            {
                errors.Add($"{label}: a sheet edit changes at most {MaxKeys} keys; split it.");
                break;
            }

            if (editable.FirstOrDefault(k => string.Equals(k.Key, property.Name.Trim(), StringComparison.OrdinalIgnoreCase)) is not { } key)
            {
                errors.Add($"{label}: '{property.Name}' is not a sheet key a proposal can change. The keys are: {(editable.Count == 0 ? "none (this system's sheet has none a proposal may set)" : string.Join(", ", editable.Select(k => k.Key)))}. Derived values (hit points, armor class, bonuses) are computed by the sheet and cannot be set.");
                continue;
            }

            if (set.ContainsKey(key.Key) || clear.Contains(key.Key))
            {
                errors.Add($"{label}: '{key.Key}' is given twice.");
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                clear.Add(key.Key);
                continue;
            }

            if (ReadValue(key, property.Value, label, errors) is { } json) set[key.Key] = json;
        }

        clear.Sort(StringComparer.Ordinal);
        return (set, clear);
    }

    private static string? ReadValue(SheetKey key, JsonElement value, string label, List<string> errors)
    {
        var name = $"{label}: '{key.Key}'";
        switch (key.Kind)
        {
            case SheetKeyKind.Text:
                if (value.ValueKind != JsonValueKind.String) return Fail($"{name} takes text.");
                var text = value.GetString()!.Trim();
                if (text.Length > MaxText) return Fail($"{name} is {text.Length} characters; at most {MaxText} fit.");
                return JsonSerializer.Serialize(text);
            case SheetKeyKind.Number:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number)) return Fail($"{name} takes a number.");
                if (key.Integer && number != Math.Floor(number)) return Fail($"{name} takes a whole number.");
                if (key.Min is { } min && number < min) return Fail($"{name} is {Format(number)}, below its lowest value {Format(min)}.");
                if (key.Max is { } max && number > max) return Fail($"{name} is {Format(number)}, above its highest value {Format(max)}.");
                return number == Math.Floor(number) && Math.Abs(number) < 1e15 ? ((long)number).ToString(CultureInfo.InvariantCulture) : number.ToString("R", CultureInfo.InvariantCulture);
            case SheetKeyKind.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? (value.GetBoolean() ? "true" : "false") : Fail($"{name} takes true or false.");
            case SheetKeyKind.Choice:
                if (value.ValueKind != JsonValueKind.String) return Fail($"{name} takes one of its options: {string.Join(", ", key.Options ?? new List<string>())}.");
                var option = (key.Options ?? new List<string>()).FirstOrDefault(o => string.Equals(o, value.GetString()!.Trim(), StringComparison.OrdinalIgnoreCase));
                return option == null ? Fail($"{name} is not one of its options: {string.Join(", ", key.Options ?? new List<string>())}.") : JsonSerializer.Serialize(option);
            case SheetKeyKind.Reference:
                if (value.ValueKind != JsonValueKind.String || value.GetString()!.Trim().Length == 0 || value.GetString()!.Trim().Length > 200) return Fail($"{name} takes the id of a Library {key.TargetTypes[0]} entry (as search_library lists it); null clears it.");
                return JsonSerializer.Serialize(value.GetString()!.Trim());
            default:
                // Picks and tags: an array of strings.
                if (value.ValueKind != JsonValueKind.Array) return Fail($"{name} takes a list (an array of strings); null clears it.");
                var items = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString()!.Trim() is not { Length: > 0 and <= 200 } s || s.Contains('\n'))
                    {
                        return Fail($"{name}: every list entry is a short non-empty string.");
                    }

                    items.Add(s);
                }

                if (items.Count > MaxItems) return Fail($"{name} holds {items.Count} entries; at most {MaxItems} fit.");
                return JsonSerializer.Serialize(items);
        }

        string? Fail(string message)
        {
            errors.Add(message);
            return null;
        }
    }

    private static string Format(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A stored value as the sheet holds it: a long or double for a number, a bool, a string, a list of strings for a list.</summary>
    public static object ToSheetValue(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => e.EnumerateArray().Select(i => (object)i.GetString()!).ToList(),
            _ => e.GetString() ?? string.Empty
        };
    }

    /// <summary>A sheet value as JSON text, whatever shape the database or the editor stored it in; <c>null</c> for a key the sheet does not have.</summary>
    public static string ToJson(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case string s: return JsonSerializer.Serialize(s);
            case bool b: return b ? "true" : "false";
            case IFormattable f when value is not System.Collections.IEnumerable: return Convert.ToDouble(f, CultureInfo.InvariantCulture) is var d && d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);
            case System.Collections.IEnumerable list:
                return JsonSerializer.Serialize(list.Cast<object?>().Select(i => i?.ToString() ?? string.Empty).ToList());
            default: return JsonSerializer.Serialize(value.ToString());
        }
    }

    /// <summary>What a value looks like on the review card: a list as its entries, nothing as a dash.</summary>
    public static string Display(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null") return "—";
        try
        {
            return ToSheetValue(json) switch
            {
                List<object> list => list.Count == 0 ? "—" : string.Join(", ", list),
                bool b => b ? "true" : "false",
                var other => Convert.ToString(other, CultureInfo.InvariantCulture) ?? "—"
            };
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>The sheet with the change laid over it: a copy, the original untouched.</summary>
    public static Dictionary<string, object> Apply(IReadOnlyDictionary<string, object> sheet, IReadOnlyDictionary<string, string> set, IEnumerable<string> clear)
    {
        var copy = new Dictionary<string, object>(sheet);
        foreach (var key in clear) copy.Remove(key);
        foreach (var (key, json) in set) copy[key] = ToSheetValue(json);
        return copy;
    }

    /// <summary>The current values of the keys a change touches, as JSON text (<c>null</c> for a key the sheet lacks): what the review card
    /// shows as "now" and what applying compares to see that the sheet did not change meanwhile.</summary>
    public static SortedDictionary<string, string> Current(IReadOnlyDictionary<string, object> sheet, IEnumerable<string> keys)
    {
        var now = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys) now[key] = sheet.TryGetValue(key, out var value) ? ToJson(value) : "null";
        return now;
    }

    /// <summary>Whether the stored values of <paramref name="keys"/> are still those in <paramref name="before"/>.</summary>
    public static bool Unchanged(IReadOnlyDictionary<string, object> sheet, IReadOnlyDictionary<string, string> before) =>
        Current(sheet, before.Keys).All(c => before.TryGetValue(c.Key, out var was) && was == c.Value);

    /// <summary>
    /// What the Library and the system's rules say about the change, against the sheet as it is now. Errors refuse the proposal: an id that is
    /// not in the Library, or a rule violation the new sheet has and the old one did not (<paramref name="enforcement"/> <c>off</c>: no rules are
    /// run). Warnings are rule warnings the change introduces. The point-buy rules are left out, as in the consistency check.
    /// <paramref name="linked"/> are the sheets the character's links point to, as the sheet page runs the rules with them: without them a value that
    /// depends on a link is not recomputed, so a change would be judged against a stale derived value.
    /// </summary>
    public static (IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings) Check(SystemDefinition system, IEntryResolver library, string enforcement,
        IReadOnlyDictionary<string, object> sheet, IReadOnlyDictionary<string, string> set, IEnumerable<string> clear, string label, ILinkedSheets? linked = null)
    {
        clear = clear.ToList();
        var errors = new List<string>();
        var warnings = new List<string>();
        // A key the system no longer lists as editable (it was reloaded between the review and the apply, or a derived key was set) is refused, never
        // skipped: Apply would write it all the same.
        foreach (var name in set.Keys.Concat(clear).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (Find(system, name) == null) errors.Add($"{label}: '{name}' is not a sheet key a proposal can change; nothing was changed.");
        }

        if (errors.Count > 0) return (errors, warnings);

        foreach (var (name, json) in set)
        {
            if (Find(system, name) is not { } key) continue;
            var value = ToSheetValue(json);
            var ids = key.Kind switch
            {
                SheetKeyKind.Reference => new List<string> { (string)value },
                SheetKeyKind.Picks => ((List<object>)value).Select(p => SheetRuleEngine.ParsePick((string)p).Id).ToList(),
                _ => new List<string>()
            };
            foreach (var id in ids.Distinct(StringComparer.Ordinal))
            {
                if (!key.TargetTypes.Any(type => library.Resolve(type, id) != null))
                {
                    errors.Add($"{label}: '{id}' (for '{key.Key}') is not in the Library as {(key.TargetTypes.Count == 1 ? "a " + key.TargetTypes[0] : "one of " + string.Join(", ", key.TargetTypes))}; use an id search_library lists. Nothing is invented.");
                }
            }
        }

        if (errors.Count > 0 || system.SheetRules == null || string.Equals(enforcement, "off", StringComparison.OrdinalIgnoreCase)) return (errors, warnings);

        var engine = new SheetRuleEngine(system, library);
        var before = Evaluate(engine, new Dictionary<string, object>(sheet), linked);
        var after = Evaluate(engine, Apply(sheet, set, clear), linked);
        if (after == null)
        {
            errors.Add($"{label}: the sheet rules could not be evaluated for the changed sheet; the change was refused.");
            return (errors, warnings);
        }

        var had = (before?.Violations.Concat(before.Warnings) ?? Enumerable.Empty<RuleViolation>()).Select(Signature).ToHashSet(StringComparer.Ordinal);
        foreach (var v in after.Violations.Where(v => !IsPointBuy(v) && !had.Contains(Signature(v))).OrderBy(Signature, StringComparer.Ordinal))
        {
            errors.Add($"{label}: the change would break a rule of the sheet: {v.Message} Choose values that keep the sheet legal.");
        }

        foreach (var w in after.Warnings.Where(w => !IsPointBuy(w) && !had.Contains(Signature(w))).OrderBy(Signature, StringComparer.Ordinal))
        {
            warnings.Add($"{label}: the change makes the sheet warn: {w.Message}");
        }

        return (errors, warnings);
    }

    private static bool IsPointBuy(RuleViolation v) => v.Key != null && v.Key.StartsWith("Actor.Rule.PointBuy", StringComparison.Ordinal);

    // The field and the rule, not the words: a violation that was there and is still there (a score that was too high and is still too high,
    // only less) is not one the change introduced.
    private static string Signature(RuleViolation v) => v.Key == null ? $"{v.FieldKey}||{v.Message}" : $"{v.FieldKey}|{v.Key}";

    private static SheetEvaluation? Evaluate(SheetRuleEngine engine, Dictionary<string, object> sheet, ILinkedSheets? linked)
    {
        try
        {
            return engine.Recompute(sheet, linked);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }
}
