using System.Text;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// <c>codex.d.ts</c> for one system (overhaul section 4, script supplier): TypeScript declarations of what a script in the
/// system's folder is handed and may return, with the system's own names (its tracks, lists and their fields, functions, rolls,
/// events) as literal types, so an editor completes them and flags a misspelt one. A script stays plain JavaScript; it opts in
/// with JSDoc: <c>/** @type {import("./codex").Handler} */</c>. <c>Codex.SystemCheck --types</c> writes it.
/// </summary>
public static class ScriptTypings
{
    public const string FileName = "codex.d.ts";

    public static string For(SystemDefinition system)
    {
        var tracks = system.SheetKinds.SelectMany(k => k.Tracks).GroupBy(t => t.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var keyed = system.SheetKinds.SelectMany(k => k.KeyedLists).GroupBy(l => l.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var picks = system.SheetKinds.SelectMany(k => k.Lists).GroupBy(l => l.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var values = system.SheetKinds.SelectMany(k => k.Values).Select(v => v.Key)
            .Concat(keyed.SelectMany(l => l.Keys.Where(k => k.Parameter == null).Select(k => $"{l.Key}.{k.Key}"))).Distinct(StringComparer.Ordinal).ToList();
        var w = new StringBuilder();
        w.AppendLine($"// codex.d.ts for {system.Id} ({system.Name}), written by Codex.SystemCheck --types. Regenerate it when system.yaml changes.");
        w.AppendLine("// What a script beside system.yaml is handed and may return. Everything is plain data, made afresh for each call:");
        w.AppendLine("// changing it changes nothing. A script keeps no state between calls, and rolls only through ctx.d.");
        w.AppendLine();
        w.AppendLine($"export type AttributeKey = {Union(system.Attributes.Select(a => a.Key))};");
        // A literal union completes the names; `string & {}` still takes one a script works out (`SP_${ev.location}`).
        w.AppendLine($"export type TrackKey = {Union(tracks.Where(t => !t.Key.Contains('{')).Select(t => t.Key), "string")} | (string & {{}});");
        w.AppendLine($"export type ValueKey = {Union(values, "string")};");
        w.AppendLine($"export type ConditionName = {Union(system.Conditions, "string")};");
        w.AppendLine($"export type FunctionName = {Union(system.Functions.Keys)};");
        w.AppendLine($"export type RollKey = {Union(system.Rolls.Select(r => r.Key))};");
        w.AppendLine($"export type EventType = {Union(system.Events.Keys)};");
        w.AppendLine("export type Plain = number | string | boolean | null;");
        w.AppendLine();

        foreach (var list in keyed)
        {
            w.AppendLine($"/** One entry of the keyed list \"{list.Key}\" ({list.Label}): its fields, and what derive works out. */");
            w.AppendLine($"export interface {TypeName(list.Key)}Entry {{");
            foreach (var field in list.ItemFields) w.AppendLine($"  readonly {Member(field.Key)}: {FieldType(field)};");
            foreach (var derived in list.Derive.Keys) w.AppendLine($"  readonly {Member(derived)}?: number;");
            w.AppendLine("  readonly [field: string]: Plain | undefined;");
            w.AppendLine("}");
        }

        foreach (var list in picks)
        {
            w.AppendLine($"/** One pick of the list \"{list.Key}\" ({list.Label}): the entry's id and its per-pick fields. */");
            w.AppendLine($"export interface {TypeName(list.Key)}Pick {{");
            w.AppendLine("  readonly id: string;");
            foreach (var field in list.ItemFields) w.AppendLine($"  readonly {Member(field.Key)}?: {FieldType(field)};");
            w.AppendLine("  readonly [field: string]: Plain | undefined;");
            w.AppendLine("}");
        }

        w.AppendLine();
        w.AppendLine("/** The sheet an event is on. */");
        w.AppendLine("export interface Sheet {");
        w.AppendLine("  readonly kind: string | null;");
        w.AppendLine("  readonly attributes: { readonly [key in AttributeKey]: number };");
        w.AppendLine("  /** Every value of the sheet kind; a keyed list's entries as \"list.key\". */");
        w.AppendLine("  readonly values: { readonly [key in ValueKey]?: number | string };");
        w.AppendLine("  /** Each track's current number; a slot track's is what its slots hold. */");
        w.AppendLine("  readonly tracks: {");
        foreach (var track in tracks.Where(t => !t.Key.Contains('{')))
        {
            var slots = track.Display.Equals("slots", StringComparison.OrdinalIgnoreCase) || track.Holds != null;
            w.AppendLine($"    readonly {Member(track.Key)}: {(slots ? "readonly string[]" : "number")};");
        }

        var anySlots = tracks.Any(t => t.Display.Equals("slots", StringComparison.OrdinalIgnoreCase) || t.Holds != null);
        w.AppendLine($"    readonly [key: string]: {(anySlots ? "number | readonly string[]" : "number")} | undefined;");
        w.AppendLine("  };");
        w.AppendLine("  readonly lists: {");
        foreach (var list in keyed) w.AppendLine($"    readonly {Member(list.Key)}: {{ readonly [key: string]: {TypeName(list.Key)}Entry | undefined }};");
        foreach (var list in picks) w.AppendLine($"    readonly {Member(list.Key)}: readonly {TypeName(list.Key)}Pick[];");
        w.AppendLine("  };");
        w.AppendLine("  /** The conditions that hold, those the tracks' thresholds bring included. */");
        w.AppendLine("  readonly conditions: readonly ConditionName[];");
        w.AppendLine("}");
        w.AppendLine();
        w.AppendLine("/** An event's fields, as the table or the GM gave them: numbers, text, flags, and lists and maps of them. What");
        w.AppendLine(" * fields an event has is the system's to say, so they are not typed here. */");
        w.AppendLine("export interface Event { readonly [field: string]: any }");
        w.AppendLine();
        w.AppendLine("export interface Context {");
        w.AppendLine("  /** Calls one of the system's named functions, formula or script. */");
        w.AppendLine("  call(name: FunctionName, ...args: Plain[]): number;");
        w.AppendLine("  /** Rolls one die through the table, which logs the face. Math.random is not available. */");
        w.AppendLine("  d(sides: number): number;");
        w.AppendLine("}");
        w.AppendLine();
        w.AppendLine("/** What a handler returns: intents, applied in order. actor names another actor's sheet; none is this one. */");
        w.AppendLine("export type Intent =");
        w.AppendLine("  | { adjust: { track: TrackKey; by: number; actor?: string } }");
        w.AppendLine("  | { set: { track: TrackKey; to: number; actor?: string } }");
        w.AppendLine("  | { mark: { track: TrackKey; by?: number; level?: number; text?: string; actor?: string } }");
        w.AppendLine("  | { clear: { track: TrackKey; by?: number; level?: number; text?: string; actor?: string } }");
        w.AppendLine("  | { condition: { add: ConditionName; actor?: string } | { remove: ConditionName; actor?: string } }");
        var listKeys = Union(keyed.Select(l => l.Key).Concat(picks.Select(l => l.Key)), "never");
        w.AppendLine($"  | {{ set: {{ list: {listKeys}; key: string; field: string; to: Plain; actor?: string }} }}");
        w.AppendLine($"  | {{ adjust: {{ list: {listKeys}; key: string; field: string; by: number; actor?: string }} }}");
        w.AppendLine("  | { roll: { definition: RollKey; actor?: string; [input: string]: unknown } }");
        w.AppendLine("  | { ask: { who: \"gm\" | \"player\"; choices: string[] } }");
        w.AppendLine("  | { log: { key: string; args?: Plain[] } }");
        if (system.Campaign?.Clocks != null)
        {
            w.AppendLine("  | { adjust: { clock: string; by: number } }");
            w.AppendLine("  | { set: { clock: string; to: number } }");
        }

        if (system.Campaign?.Tables.Count > 0)
        {
            var tables = Union(system.Campaign.Tables.Select(t => t.Key));
            w.AppendLine($"  | {{ adjust: {{ table: {tables}; row: string; field?: string; by: number }} }}");
            w.AppendLine($"  | {{ set: {{ table: {tables}; row: string; field?: string; to: number }} }}");
        }

        w.AppendLine("  ;");
        w.AppendLine();
        w.AppendLine("/** A function's result: a number, or one with its working. */");
        w.AppendLine("export type Value = number | { value: number; parts?: [label: string, amount: number][] };");
        w.AppendLine("export type ScriptFunction = (...args: Plain[]) => Value;");
        w.AppendLine("/** An event handler: what it returns is applied in order; nothing it changes on its arguments is kept. */");
        w.AppendLine("export type Handler = (ev: Event, sheet: Sheet, ctx: Context) => Intent[];");

        var references = system.Functions.Where(f => f.Value.Script != null).Select(f => $"//   {f.Value.Script}: function {f.Key} (ScriptFunction)")
            .Concat(system.Events.Select(e => (e.Key, Script: YamlValues.Normalize(e.Value) is IDictionary<string, object> map && map.TryGetValue("script", out var script) ? script?.ToString() : null))
                .Where(e => e.Script != null).Select(e => $"//   {e.Script}: the {e.Key} handler (Handler)")).ToList();
        if (references.Count > 0)
        {
            w.AppendLine();
            w.AppendLine("// The exports system.yaml names:");
            foreach (var line in references) w.AppendLine(line);
        }

        return w.ToString();
    }

    private static string FieldType(ItemFieldDefinition field) => field.Type switch
    {
        _ when field.Type.Equals("Number", StringComparison.OrdinalIgnoreCase) => "number",
        _ when field.Type.Equals("Boolean", StringComparison.OrdinalIgnoreCase) || field.Type.Equals("Checkbox", StringComparison.OrdinalIgnoreCase) => "boolean",
        _ when field.Options is { Count: > 0 } options => Union(options),
        _ => "string"
    };

    private static string Union(IEnumerable<string> names, string empty = "never")
    {
        var quoted = names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).Select(Quote).ToList();
        return quoted.Count == 0 ? empty : string.Join(" | ", quoted);
    }

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>A property name: bare when it is an identifier, quoted otherwise (<c>"SP_head"</c> is fine bare, <c>"a-b"</c> is not).</summary>
    private static string Member(string key) =>
        key.Length > 0 && (char.IsLetter(key[0]) || key[0] is '_' or '$') && key.All(c => char.IsLetterOrDigit(c) || c is '_' or '$') ? key : Quote(key);

    private static string TypeName(string key)
    {
        var name = new StringBuilder();
        var upper = true;
        foreach (var c in key)
        {
            if (!char.IsLetterOrDigit(c))
            {
                upper = true;
                continue;
            }

            name.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return name.Length == 0 || char.IsDigit(name[0]) ? "List" + name : name.ToString();
    }
}
