using System.Collections;
using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>What applying intents to a sheet came to.</summary>
public sealed class SheetIntentResult
{
    /// <summary>Intents the track changes raised (an overflow's <c>ask</c>), in order. They are also in <see cref="Left"/>,
    /// since nothing on the sheet can answer them.</summary>
    public List<SheetIntent> Raised { get; } = new();

    /// <summary>What the sheet could not do, for the table: another actor's intents, rolls, asks, log lines, and changes that
    /// did not fit (a full level of slots that does not push up). In order.</summary>
    public List<SheetIntent> Left { get; } = new();

    /// <summary>The sheet keys that changed (tracks, and <see cref="SheetModel.ConditionsKey"/>), each once, in order.</summary>
    public List<string> Changed { get; } = new();

    internal void Touch(string key)
    {
        if (!Changed.Contains(key, StringComparer.Ordinal)) Changed.Add(key);
    }
}

/// <summary>
/// Applies intents (overhaul section 3, "Events → intents") to one sheet:
/// <list type="bullet">
/// <item><c>adjust { track, by }</c> and <c>set { track, to }</c>: a numeric track's value, kept within its minimum and
/// maximum. A rise past the maximum of a track with <c>overflow</c> adds to the other track (which may overflow in turn),
/// then resets, carries what went past, or keeps the maximum, and raises the overflow's <c>ask</c>. <c>set</c> never
/// overflows: it is somebody saying what the value is.</item>
/// <item><c>mark { track, by?, level?, text? }</c> and <c>clear</c>: a box track up or down by one (or <c>by</c>); for a slot
/// track, <c>text</c> into a free slot (of <c>level</c>, pushing up a level at a time when the track says <c>whenFull:
/// pushUp</c>, and bringing <c>beyond</c>'s condition past the last), or out of it.</item>
/// <item><c>condition { add | remove }</c>: the sheet's own conditions.</item>
/// <item><c>set { list, key, field, to }</c> and <c>adjust { list, key, field, by }</c>: a per-pick field of one entry, by its
/// key in a keyed list (<c>skills</c>, <c>handgun</c>) or its id in a list of picks; a number field is kept within its
/// <c>min</c> and <c>max</c>, and only a number field adjusts.</item>
/// </list>
/// A track the sheet has no value for starts from its <c>start</c> (none declared: from nothing, 0 within its bounds). Thresholds and bands are not applied here: they are worked
/// out from the values whenever the sheet is read (<see cref="SheetModel"/>). Everything else comes back in
/// <see cref="SheetIntentResult.Left"/>.
/// </summary>
public static class SheetIntents
{
    /// <summary>How many overflows one change may set off (stress into trauma into ...): a track that overflows into itself
    /// must stop.</summary>
    public const int MaxOverflow = 8;

    /// <param name="self">The sheet's actor id: an intent naming another actor is left for the table. Null: only intents that
    /// name no actor apply.</param>
    public static SheetIntentResult Apply(SystemDefinition system, IEntryResolver resolver, Dictionary<string, object> sheet, IEnumerable<SheetIntent> intents, string? self = null)
    {
        var result = new SheetIntentResult();
        var applier = new Applier(system, resolver, sheet, result);
        foreach (var intent in intents)
        {
            if (intent.Actor != null && !string.Equals(intent.Actor, self, StringComparison.Ordinal))
            {
                result.Left.Add(intent);
                continue;
            }

            if (!applier.Apply(intent)) result.Left.Add(intent);
        }

        return result;
    }

    private sealed class Applier(SystemDefinition system, IEntryResolver resolver, Dictionary<string, object> sheet, SheetIntentResult result)
    {
        // A fresh model for each read: the values a track's bounds and start depend on (HP for nonlethal damage, turf for
        // reputation) change as intents apply.
        private SheetTrack? Track(string key) => new SheetModel(system, resolver, sheet).TrackOf(key);

        public bool Apply(SheetIntent intent)
        {
            var fields = intent.Fields;
            switch (intent.Kind)
            {
                case "adjust" or "set" when fields.GetValueOrDefault("list")?.ToString() is { Length: > 0 } list:
                    return ListField(intent.Kind == "set", list, fields);
                case "adjust" when Key(fields) is { } track && YamlValues.ToNumber(fields.GetValueOrDefault("by")) is { } by:
                    return Adjust(track, by, 0);
                case "set" when Key(fields) is { } track && YamlValues.ToNumber(fields.GetValueOrDefault("to")) is { } to:
                    return Set(track, to);
                case "mark" or "clear" when Key(fields) is { } track:
                    return Mark(intent.Kind == "mark", track, fields);
                case "condition":
                    return Condition(fields);
                default:
                    return false;
            }
        }

        private static string? Key(IReadOnlyDictionary<string, object?> fields) =>
            fields.GetValueOrDefault("track")?.ToString() is { Length: > 0 } track ? track : null;

        /// <summary>The sheet's value; for a track it has none of, the track's start, else nothing (0, within the bounds).</summary>
        private double Current(string key, SheetTrack? track) =>
            YamlValues.ToNumber(sheet.GetValueOrDefault(key)) ?? track?.Start ?? Clamp(0, track);

        /// <summary>Whether the sheet lacks the track: on a system with sheet kinds, a track its kind does not declare (a crew track named by a
        /// handler that also reaches a scoundrel). The intent is left unapplied, so nothing stray is written.</summary>
        private bool Lacks(SheetTrack? track) => track == null && system.SheetKinds.Count > 0;

        private bool Set(string key, double to)
        {
            var track = Track(key);
            if (track?.IsSlots == true || Lacks(track)) return false;
            Write(key, Clamp(to, track));
            return true;
        }

        private bool Adjust(string key, double by, int depth)
        {
            var track = Track(key);
            if (track?.IsSlots == true || Lacks(track)) return false;
            var next = Current(key, track) + by;
            if (track is { Max: { } max, Definition.Overflow: { } overflow } && next > max && depth < MaxOverflow)
            {
                // Each time round is one overflow: heat 7 + 13 on a track of 9 is wanted +2 and heat 2 when it carries.
                var excess = next - max;
                while (true)
                {
                    var into = overflow.By is { } formula ? Amount(formula, excess, key) : 1;
                    if (into is { } amount) Adjust(overflow.Into, amount, depth + 1);
                    if (!overflow.Then.Equals("carry", StringComparison.OrdinalIgnoreCase) || excess <= max || ++depth >= MaxOverflow) break;
                    excess -= max;
                }

                next = overflow.Then.ToLowerInvariant() switch
                {
                    "reset" => track.Min ?? 0,
                    "carry" => Math.Min(excess, max),
                    _ => max
                };

                if (overflow.Ask is { } ask)
                {
                    var asked = Ask(ask);
                    result.Raised.Add(asked);
                    result.Left.Add(asked);
                }
            }

            Write(key, Clamp(next, track));
            return true;
        }

        private static SheetIntent Ask(TrackAskDefinition ask) =>
            new("ask", null, new Dictionary<string, object?> { ["who"] = ask.Who, ["choices"] = ask.Choices.Cast<object?>().ToList() });

        /// <summary>An overflow's <c>by</c>: a number, or a formula reading <c>excess</c>. One that cannot be worked out adds
        /// nothing (and the sheet's evaluation warns about the formula).</summary>
        private double? Amount(string formula, double excess, string track)
        {
            if (double.TryParse(formula, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
            try
            {
                return SheetFormulas.Number(new SheetModel(system, resolver, sheet).Run(formula, new Dictionary<string, object> { ["excess"] = excess }));
            }
            catch (FormulaException)
            {
                System.Diagnostics.Trace.TraceWarning($"{system.Id}: the overflow of \"{track}\" ({formula}) can't be worked out.");
                return null;
            }
        }

        private static double Clamp(double value, SheetTrack? track)
        {
            if (track?.Min is { } floor) value = Math.Max(value, floor);
            if (track?.Max is { } ceiling) value = Math.Min(value, ceiling);
            return value;
        }

        private void Write(string key, double value)
        {
            sheet[key] = value == Math.Floor(value) && Math.Abs(value) < long.MaxValue ? (object)(long)value : value;
            result.Touch(key);
        }

        // ---- Fields of list entries ----

        private bool ListField(bool set, string listKey, IReadOnlyDictionary<string, object?> fields)
        {
            if (fields.GetValueOrDefault("key")?.ToString() is not { Length: > 0 } entryKey || fields.GetValueOrDefault("field")?.ToString() is not { Length: > 0 } fieldKey) return false;
            var kind = new SheetModel(system, resolver, sheet).Kind;
            if (kind?.KeyedLists.FirstOrDefault(l => l.Key == listKey) is { } keyed)
            {
                var declared = keyed.Keys.Any(k => k.Parameter == null ? k.Key == entryKey : entryKey.StartsWith(k.Key + ".", StringComparison.Ordinal) && entryKey.Length > k.Key.Length + 1);
                if (!declared || keyed.ItemFields.FirstOrDefault(f => f.Key == fieldKey) is not { } field) return false;
                var stored = Copy(sheet.GetValueOrDefault(listKey) as IDictionary<string, object>);
                var entry = Copy(stored.GetValueOrDefault(entryKey) as IDictionary<string, object>);
                if (FieldValue(set, field, entry.GetValueOrDefault(fieldKey), fields) is not { } value) return false;
                entry[fieldKey] = value;
                stored[entryKey] = entry;
                sheet[listKey] = stored;
                result.Touch(listKey);
                return true;
            }

            if (kind?.Lists.FirstOrDefault(l => l.Key == listKey) is { } picks)
            {
                if (picks.ItemFields.FirstOrDefault(f => f.Key == fieldKey) is not { } field || sheet.GetValueOrDefault(listKey) is not IEnumerable raw || raw is string) return false;
                var items = raw.Cast<object?>().ToList();
                var at = items.FindIndex(i => (i is IDictionary<string, object> map ? (map.TryGetValue("id", out var id) ? id?.ToString() : null) : i?.ToString()) == entryKey);
                if (at < 0) return false;
                var entry = items[at] is IDictionary<string, object> found ? Copy(found) : new Dictionary<string, object> { ["id"] = entryKey };
                if (FieldValue(set, field, entry.GetValueOrDefault(fieldKey), fields) is not { } value) return false;
                entry[fieldKey] = value;
                items[at] = entry;
                sheet[listKey] = items.Where(i => i != null).Cast<object>().ToList();
                result.Touch(listKey);
                return true;
            }

            return false;
        }

        /// <summary>The field's new value: a number within the field's bounds, or (set, a field that is not a number) the text
        /// given. Null when the intent does not fit the field.</summary>
        private static object? FieldValue(bool set, ItemFieldDefinition field, object? current, IReadOnlyDictionary<string, object?> fields)
        {
            if (!field.Type.Equals("Number", StringComparison.OrdinalIgnoreCase))
            {
                return set && fields.GetValueOrDefault("to") is { } text ? text.ToString() : null;
            }

            double next;
            if (set)
            {
                if (YamlValues.ToNumber(fields.GetValueOrDefault("to")) is not { } to) return null;
                next = to;
            }
            else
            {
                if (YamlValues.ToNumber(fields.GetValueOrDefault("by")) is not { } by) return null;
                next = (YamlValues.ToNumber(current) ?? YamlValues.ToNumber(field.Default) ?? 0) + by;
            }

            if (field.Min is { } min) next = Math.Max(next, min);
            if (field.Max is { } max) next = Math.Min(next, max);
            return next == Math.Floor(next) && Math.Abs(next) < long.MaxValue ? (object)(long)next : next;
        }

        private static Dictionary<string, object> Copy(IDictionary<string, object>? map) =>
            map == null ? new Dictionary<string, object>(StringComparer.Ordinal) : new Dictionary<string, object>(map, StringComparer.Ordinal);

        // ---- Boxes and slots ----

        private bool Mark(bool mark, string key, IReadOnlyDictionary<string, object?> fields)
        {
            var track = Track(key);
            var by = YamlValues.ToNumber(fields.GetValueOrDefault("by")) ?? 1;
            if (track?.IsSlots != true) return Adjust(key, mark ? by : -by, 0);

            var text = fields.GetValueOrDefault("text")?.ToString();
            var level = YamlValues.ToNumber(fields.GetValueOrDefault("level") ?? fields.GetValueOrDefault("slot")) is { } l ? (int)l : (int?)null;
            if (track.Levels.Count == 0) return mark ? MarkList(key, track, text) : ClearList(key, text);
            return mark ? MarkLevel(key, track, level, text) : ClearLevel(key, level, text);
        }

        private bool MarkList(string key, SheetTrack track, string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var held = Texts(sheet.GetValueOrDefault(key));
            if (track.Max is { } max && held.Count >= max) return false;
            held.Add(text);
            sheet[key] = held.Cast<object>().ToList();
            result.Touch(key);
            return true;
        }

        private bool ClearList(string key, string? text)
        {
            var held = Texts(sheet.GetValueOrDefault(key));
            if (text == null) held.Clear();
            else if (held.FindIndex(h => string.Equals(h, text, StringComparison.OrdinalIgnoreCase)) is var at and >= 0) held.RemoveAt(at);
            else return false;
            sheet[key] = held.Cast<object>().ToList();
            result.Touch(key);
            return true;
        }

        /// <summary>Into a free slot of the level (the first level when none is named); a full level pushes up when the track
        /// says so, and past the last level brings <c>beyond</c>'s condition.</summary>
        private bool MarkLevel(string key, SheetTrack track, int? level, string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var levels = track.Levels.OrderBy(l => l.Level).ToList();
            var at = levels.FindIndex(l => l.Level == (level ?? levels[0].Level));
            if (at < 0) return false;
            var filled = Levels(sheet.GetValueOrDefault(key));
            var pushUp = string.Equals(track.Definition.WhenFull, "pushUp", StringComparison.OrdinalIgnoreCase);
            for (; at < levels.Count; at++)
            {
                var name = levels[at].Level.ToString(CultureInfo.InvariantCulture);
                var held = filled.GetValueOrDefault(name) ?? new List<string>();
                if (held.Count < levels[at].Slots)
                {
                    held.Add(text);
                    filled[name] = held;
                    WriteLevels(key, filled);
                    return true;
                }

                if (!pushUp) return false;
            }

            if (track.Definition.Beyond?.Condition is not { } beyond) return false;
            AddCondition(beyond);
            return true;
        }

        private bool ClearLevel(string key, int? level, string? text)
        {
            var filled = Levels(sheet.GetValueOrDefault(key));
            var names = level is { } l ? new[] { l.ToString(CultureInfo.InvariantCulture) } : filled.Keys.ToArray();
            var cleared = false;
            foreach (var name in names)
            {
                if (!filled.TryGetValue(name, out var held)) continue;
                if (text == null)
                {
                    cleared |= held.Count > 0;
                    held.Clear();
                }
                else if (held.FindIndex(h => string.Equals(h, text, StringComparison.OrdinalIgnoreCase)) is var at and >= 0)
                {
                    held.RemoveAt(at);
                    cleared = true;
                    if (level == null) break;
                }
            }

            if (!cleared) return false;
            WriteLevels(key, filled);
            return true;
        }

        private void WriteLevels(string key, Dictionary<string, List<string>> filled)
        {
            sheet[key] = filled.Where(p => p.Value.Count > 0).OrderBy(p => p.Key, StringComparer.Ordinal)
                .ToDictionary(p => p.Key, p => (object)p.Value.Cast<object>().ToList(), StringComparer.Ordinal);
            result.Touch(key);
        }

        private static List<string> Texts(object? value) => value is IEnumerable list and not string
            ? list.Cast<object?>().Select(o => o?.ToString()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList()
            : new List<string>();

        private static Dictionary<string, List<string>> Levels(object? value)
        {
            var levels = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (YamlValues.Normalize(value) is not IDictionary<string, object> map) return levels;
            foreach (var (level, texts) in map) levels[level] = Texts(texts);
            return levels;
        }

        // ---- Conditions ----

        private bool Condition(IReadOnlyDictionary<string, object?> fields)
        {
            if (fields.GetValueOrDefault("add")?.ToString() is { Length: > 0 } add)
            {
                AddCondition(add);
                return true;
            }

            if (fields.GetValueOrDefault("remove")?.ToString() is not { Length: > 0 } remove) return false;
            var conditions = SheetRuleEngine.RawList(sheet, SheetModel.ConditionsKey);
            if (conditions.RemoveAll(c => string.Equals(c, remove, StringComparison.OrdinalIgnoreCase)) == 0) return true;
            sheet[SheetModel.ConditionsKey] = conditions.Cast<object>().ToList();
            result.Touch(SheetModel.ConditionsKey);
            return true;
        }

        private void AddCondition(string condition)
        {
            var conditions = SheetRuleEngine.RawList(sheet, SheetModel.ConditionsKey);
            if (conditions.Contains(condition, StringComparer.OrdinalIgnoreCase)) return;
            conditions.Add(condition);
            sheet[SheetModel.ConditionsKey] = conditions.Cast<object>().ToList();
            result.Touch(SheetModel.ConditionsKey);
        }
    }
}
