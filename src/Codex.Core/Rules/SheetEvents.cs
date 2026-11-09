using System.Collections;
using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>Something that happened at the table, to one sheet: <c>damage</c> with its <c>amount</c>, <c>turnStart</c>, a
/// <c>roll</c> with its <c>definition</c>, <c>inputs</c> and <c>outcome</c>. Handlers read the fields as <c>ev.amount</c>.</summary>
public sealed record SheetEvent(string Type, IReadOnlyDictionary<string, object?> Fields)
{
    public static SheetEvent Of(string type, params (string Key, object? Value)[] fields) =>
        new(type, fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal));
}

/// <summary>What an event came to on the sheet.</summary>
public sealed class SheetEventResult
{
    /// <summary>The intents the handlers returned, then those the changes raised (an overflow's <c>ask</c>), in order. An event
    /// that is itself an intent (the GM marking stress) has only the raised ones.</summary>
    public List<SheetIntent> Intents { get; } = new();

    /// <summary>What applying them did to the sheet, and what is left for the table (<see cref="SheetIntentResult.Left"/>).</summary>
    public SheetIntentResult Applied { get; init; } = new();

    /// <summary>What the campaign's intents did to its objects; null when no campaign state was given, and they are left for
    /// the table with the rest.</summary>
    public CampaignIntentResult? Campaign { get; init; }
}

/// <summary>A handler that cannot run: a formula that fails, a builtin that does not exist. <see cref="NotYet"/> is set when
/// the handler is one this version does not run at all (a script), saying why.</summary>
public sealed class SheetEventException(string message, string? notYet = null) : Exception(message)
{
    public string? NotYet { get; } = notYet;
}

/// <summary>
/// The system's event handlers (overhaul section 3, "Events → intents"). A handler reads the sheet and the event and returns
/// intents; it never changes anything. <see cref="Raise"/> then applies the intents for this sheet
/// (<see cref="SheetIntents"/>) and leaves the rest (another actor's, rolls, asks, log lines) for the table.
/// <para>A handler is <c>{ builtin: name, ...options }</c>, <c>{ script: file#export }</c> (overhaul step 8; not run by this
/// version), or a list of <c>{ when, intents }</c> lines, each line's intents added when its <c>when</c> holds (one without
/// <c>when</c> always does). In an intent's fields, <c>by</c>, <c>to</c>, <c>level</c> and <c>slot</c> are formulas; <c>key</c>,
/// <c>definition</c> and <c>who</c> are names; any other text is itself when it is a bare word or words (<c>stress</c>, <c>cut hand</c>), and otherwise a formula
/// (<c>ev.helper</c>, <c>'xp_' + actions[ev.inputs.action].group</c>). Formulas read the sheet, and the event as
/// <c>ev</c>.</para>
/// <para>An event whose type is a kind of intent (<c>adjust</c>, <c>set</c>, <c>mark</c>, <c>clear</c>, <c>condition</c>) is
/// that intent, applied straight to the sheet: it is the table's own edit, and no handler runs for it.</para>
/// <para>A line with <c>target: campaign.clocks</c> or <c>target: campaign.&lt;table&gt;</c> acts on the campaign's objects
/// (<see cref="CampaignObjects"/>): its intents name a clock, or a table's row (the target's table when they name none). An
/// intent that names a clock or a table is the campaign's wherever it comes from, and is applied to the campaign state when
/// <see cref="Raise"/> is given one.</para>
/// </summary>
public static partial class SheetEvents
{
    /// <summary>The kinds of intent (overhaul section 3).</summary>
    public const string AdjustKind = "adjust";
    public const string SetKind = "set";
    public const string MarkKind = "mark";
    public const string ClearKind = "clear";
    public const string ConditionKind = "condition";
    public const string RollKind = "roll";
    public const string AskKind = "ask";
    public const string LogKind = "log";

    public static readonly IReadOnlyList<string> IntentKinds = new[] { "adjust", "set", "mark", "clear", "condition", "roll", "ask", "log" };

    /// <summary>The intents an event can be on its own: changes to the sheet.</summary>
    public static readonly IReadOnlyList<string> DirectKinds = new[] { "adjust", "set", "mark", "clear", "condition" };

    /// <summary>The handlers built in, by name, with the options each reads (see <see cref="Damage"/>).</summary>
    public static readonly IReadOnlyList<string> Builtins = new[] { "damage" };

    private static readonly string[] NumberFields = { "by", "to", "level", "slot" };

    /// <summary>Fields that name something and are never formulas: a log line's translation key (<c>Harm.Taken</c>), a roll's
    /// definition, who is asked.</summary>
    private static readonly string[] NameFields = { "key", "definition", "who" };

    /// <summary>Whether the system says what an event of this type does: a handler, or an event that is an intent.</summary>
    public static bool Handles(SystemDefinition system, string type) =>
        DirectKinds.Contains(type, StringComparer.Ordinal) || system.Events.ContainsKey(type);

    /// <summary>Raises <paramref name="ev"/> on <paramref name="sheet"/>: runs its handlers, then applies the intents for this
    /// sheet. <paramref name="self"/> is the sheet's actor id, so an intent naming it applies too.</summary>
    /// <exception cref="SheetEventException">A handler that cannot run.</exception>
    /// <param name="campaign">The campaign's objects, which the campaign's intents change; null leaves those for the table.</param>
    public static SheetEventResult Raise(SystemDefinition system, IEntryResolver resolver, Dictionary<string, object> sheet, SheetEvent ev, string? self = null,
        Dictionary<string, object>? campaign = null)
    {
        var direct = DirectKinds.Contains(ev.Type, StringComparer.Ordinal);
        var intents = direct ? new List<SheetIntent>() : Handle(system, resolver, sheet, ev).ToList();
        var toApply = direct
            ? new List<SheetIntent> { new(ev.Type, ev.Fields.GetValueOrDefault("actor")?.ToString(), ev.Fields.Where(f => f.Key != "actor").ToDictionary(f => f.Key, f => YamlValues.Normalize(f.Value), StringComparer.Ordinal)) }
            : intents;
        var forCampaign = toApply.Where(CampaignObjects.IsFor).ToList();
        var applied = SheetIntents.Apply(system, resolver, sheet, toApply.Where(i => !CampaignObjects.IsFor(i)), self);
        CampaignIntentResult? onCampaign = null;
        if (campaign != null)
        {
            onCampaign = CampaignObjects.Apply(system, campaign, forCampaign);
            applied.Left.AddRange(onCampaign.Left);
            applied.Left.AddRange(onCampaign.Raised);
        }
        else
        {
            applied.Left.AddRange(forCampaign);
        }

        var result = new SheetEventResult { Applied = applied, Campaign = onCampaign };
        result.Intents.AddRange(intents);
        result.Intents.AddRange(applied.Raised);
        if (onCampaign != null) result.Intents.AddRange(onCampaign.Raised);
        return result;
    }

    /// <summary>What the handlers for <paramref name="ev"/> return, nothing applied. No handler: none.</summary>
    /// <exception cref="SheetEventException">A handler that cannot run.</exception>
    public static IReadOnlyList<SheetIntent> Handle(SystemDefinition system, IEntryResolver resolver, IReadOnlyDictionary<string, object> sheet, SheetEvent ev)
    {
        if (!system.Events.TryGetValue(ev.Type, out var raw)) return Array.Empty<SheetIntent>();
        var model = new SheetModel(system, resolver, sheet);
        var names = new Dictionary<string, object>(StringComparer.Ordinal) { ["ev"] = Bind(ev.Fields) };
        var handler = YamlValues.Normalize(raw);
        switch (handler)
        {
            case IDictionary<string, object> map when map.TryGetValue("builtin", out var builtin):
                return builtin?.ToString() switch
                {
                    "damage" => Damage(model, sheet, ev, map),
                    var name => throw new SheetEventException($"The {ev.Type} handler names \"{name}\", which is not a built-in handler ({string.Join(", ", Builtins)}).")
                };
            case IDictionary<string, object> map when map.TryGetValue("script", out var script):
                throw new SheetEventException($"The {ev.Type} handler is a script ({script}).", "scripted handlers run from overhaul step 8 (Jint)");
            case IEnumerable lines and not string and not IDictionary<string, object>:
                var intents = new List<SheetIntent>();
                foreach (var line in lines.Cast<object?>())
                {
                    if (line is not IDictionary<string, object> entry) throw new SheetEventException($"A {ev.Type} handler line is not {{ when, intents }}.");
                    var target = entry.TryGetValue("target", out var named) ? Target(system, ev, named?.ToString()) : null;
                    if (entry.Get("when")?.ToString() is { } when && Evaluate(model, ev, "when", when, names) is not true) continue;
                    foreach (var intent in entry.Get("intents") as IEnumerable ?? Array.Empty<object>())
                    {
                        var made = Intent(model, ev, intent, names);
                        if (target is { } on) made = OnTarget(ev, on, made);
                        intents.Add(made);
                    }
                }

                return intents;
            default:
                throw new SheetEventException($"The {ev.Type} handler is neither {{ builtin }}, {{ script }} nor a list of {{ when, intents }}.");
        }
    }

    // ---- Campaign targets ----

    /// <summary>What a line's <c>target</c> names: <c>clocks</c>, or a table's key.</summary>
    private static string Target(SystemDefinition system, SheetEvent ev, string? target) =>
        TargetProblem(system, target) is { } problem ? throw new SheetEventException($"A {ev.Type} handler line: {problem}") : target!["campaign.".Length..];

    /// <summary>Why a line's <c>target</c> is not one the system has; null when it is.</summary>
    internal static string? TargetProblem(SystemDefinition system, string? target)
    {
        const string prefix = "campaign.";
        if (target == null || !target.StartsWith(prefix, StringComparison.Ordinal)) return $"target is campaign.clocks or campaign.<table>, not \"{target}\".";
        var key = target[prefix.Length..];
        if (key == CampaignObjects.ClocksKey) return system.Campaign?.Clocks == null ? "the target is the campaign's clocks, and the system has no clocks." : null;
        return CampaignObjects.TableOf(system, key) == null ? $"the target is the campaign table \"{key}\", which the system does not have." : null;
    }

    /// <summary>A target line's intent, naming its table when it names none; one for the clocks must name its clock.</summary>
    private static SheetIntent OnTarget(SheetEvent ev, string target, SheetIntent intent)
    {
        if (target == CampaignObjects.ClocksKey)
        {
            return intent.Fields.ContainsKey("clock") ? intent : throw new SheetEventException($"A {ev.Type} handler's {intent.Kind} acts on the clocks, and names no clock.");
        }

        if (intent.Fields.ContainsKey("table")) return intent;
        var fields = new Dictionary<string, object?>(intent.Fields, StringComparer.Ordinal) { ["table"] = target };
        return intent with { Fields = fields };
    }

    // ---- Intents from a handler line ----

    private static SheetIntent Intent(SheetModel model, SheetEvent ev, object? raw, Dictionary<string, object> names)
    {
        if (raw is not IDictionary<string, object> { Count: 1 } intent || intent.First().Value is not IDictionary<string, object> map)
        {
            throw new SheetEventException($"A {ev.Type} handler's intent is one kind (adjust, mark...) and its fields.");
        }

        var kind = intent.First().Key;
        string? actor = null;
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            var worked = Field(model, ev, kind, key, value, names);
            if (key == "actor") actor = worked?.ToString();
            else fields[key] = worked;
        }

        return new SheetIntent(kind, actor, fields);
    }

    private static object? Field(SheetModel model, SheetEvent ev, string kind, string key, object? value, Dictionary<string, object> names)
    {
        switch (value)
        {
            case string formula when NumberFields.Contains(key):
                try
                {
                    return SheetFormulas.Number(Evaluate(model, ev, $"{kind}'s {key}", formula, names));
                }
                catch (FormulaException ex)
                {
                    throw new SheetEventException($"The {ev.Type} handler: {kind}'s {key} ({formula}) is not a number: {ex.Message}.");
                }
            case string name when NameFields.Contains(key):
                return name;
            case string text when BareWords().IsMatch(text) && text is not ("true" or "false" or "null"):
                return text;
            case string formula:
                return Evaluate(model, ev, $"{kind}'s {key}", formula, names) switch
                {
                    FormulaItem item and not FormulaList => item.Value ?? (object?)item.Flag,
                    var result => result
                };
            case IDictionary<string, object> map:
                return map.ToDictionary(p => p.Key, p => Field(model, ev, kind, $"{key}.{p.Key}", p.Value, names), StringComparer.Ordinal);
            // A list's elements are text (a clock's name, which may hold an apostrophe), except a dotted path, which is read:
            // a log line's args: [ev.helper].
            case System.Collections.IEnumerable list and not string:
                return list.Cast<object?>().Select(element => element is string path && DottedPath().IsMatch(path)
                    ? Evaluate(model, ev, $"{kind}'s {key}", path, names) switch
                    {
                        FormulaItem item and not FormulaList => item.Value ?? (object?)item.Flag,
                        var result => result
                    }
                    : SheetModel.Plain(element) ?? YamlValues.Normalize(element)).ToList();
            default:
                return SheetModel.Plain(value) ?? YamlValues.Normalize(value);
        }
    }

    /// <summary>A word or words, nothing a formula would add: <c>stress</c>, <c>Less effect</c>, <c>xp_playbook</c>.</summary>
    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{N}_\- ]*$")]
    private static partial Regex BareWords();

    /// <summary>A name with one or more members: <c>ev.helper</c>, <c>item.name</c>.</summary>
    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{N}_]*(\.[\p{L}_][\p{L}\p{N}_]*)+$")]
    private static partial Regex DottedPath();

    private static object? Evaluate(SheetModel model, SheetEvent ev, string what, string formula, Dictionary<string, object> names)
    {
        try
        {
            return model.Run(formula, names);
        }
        catch (FormulaException ex)
        {
            throw new SheetEventException($"The {ev.Type} handler: {what} ({formula}) can't be worked out: {ex.Message}.");
        }
    }

    /// <summary>An event's fields as formulas read them: maps as items (<c>ev.inputs.position</c>), lists as lists (each
    /// entry an item, so <c>count(ev.answers, a => a)</c> works), the rest as numbers, strings and flags.</summary>
    internal static FormulaItem Bind(IReadOnlyDictionary<string, object?> fields) => new(name => Bound(fields.GetValueOrDefault(name)));

    private static object? Bound(object? value) => YamlValues.Normalize(value) switch
    {
        null => null,
        IDictionary<string, object> map => new FormulaItem(name => Bound(map.Get(name))),
        IEnumerable list and not string => new FormulaList(list.Cast<object?>().Select(Element).ToList()),
        var plain => SheetModel.Plain(plain)
    };

    private static FormulaItem Element(object? value) => Bound(value) switch
    {
        FormulaItem item => item,
        double number => new FormulaNumber(number),
        bool flag => new FormulaFlag(flag),
        var other => new FormulaText(other?.ToString())
    };

    // ---- Built-in handlers ----

    /// <summary>
    /// <c>{ builtin: damage, track, nonlethal: { tag, track }, reduction: { list, amount, bypassedByTag, onlyTypes },
    /// resistance: { property } }</c>, for an event of <c>{ amount, damageType, tags }</c>:
    /// <list type="number">
    /// <item>damage reduction, when the damage type is one of <c>onlyTypes</c> (all, unset): the best <c>amount</c> among the
    /// sheet's <c>list</c> entries whose <c>bypassedByTag</c> property is not one of the event's tags (reductions do not
    /// stack);</item>
    /// <item>resistance: the sheet's <c>property</c> map, by damage type;</item>
    /// <item>what is left (never below none) adds to the <c>nonlethal</c> track when the event has its tag, and otherwise
    /// comes off <c>track</c> (<c>HP</c> unset).</item>
    /// </list>
    /// Bands and thresholds follow from the track's new value.
    /// </summary>
    private static IReadOnlyList<SheetIntent> Damage(SheetModel model, IReadOnlyDictionary<string, object> sheet, SheetEvent ev, IDictionary<string, object> options)
    {
        if (YamlValues.ToNumber(ev.Fields.GetValueOrDefault("amount")) is not { } amount)
        {
            throw new SheetEventException("A damage event needs an amount.");
        }

        var type = ev.Fields.GetValueOrDefault("damageType")?.ToString();
        var tags = Strings(YamlValues.Normalize(ev.Fields.GetValueOrDefault("tags")));

        if (options.Get("reduction") is IDictionary<string, object> reduction
            && (reduction.Get("onlyTypes") is not IEnumerable only || (type != null && Strings(only).Contains(type, StringComparer.OrdinalIgnoreCase))))
        {
            var list = reduction.Get("list")?.ToString() ?? "damageReduction";
            var amountKey = reduction.Get("amount")?.ToString() ?? "amount";
            var bypassKey = reduction.Get("bypassedByTag")?.ToString();
            var best = (YamlValues.Normalize(sheet.GetValueOrDefault(list)) as IEnumerable ?? Array.Empty<object>()).Cast<object?>()
                .OfType<IDictionary<string, object>>()
                .Where(entry => bypassKey == null || entry.Get(bypassKey)?.ToString() is not { } bypass || !tags.Contains(bypass, StringComparer.OrdinalIgnoreCase))
                .Select(entry => YamlValues.ToNumber(entry.Get(amountKey)) ?? 0)
                .DefaultIfEmpty(0).Max();
            amount -= best;
        }

        if (type != null && options.Get("resistance") is IDictionary<string, object> resistance
            && resistance.Get("property")?.ToString() is { } property
            && YamlValues.Normalize(sheet.GetValueOrDefault(property)) is IDictionary<string, object> resistances
            && resistances.FirstOrDefault(r => string.Equals(r.Key, type, StringComparison.OrdinalIgnoreCase)).Value is { } resisted)
        {
            amount -= YamlValues.ToNumber(resisted) ?? 0;
        }

        if (amount <= 0) return Array.Empty<SheetIntent>();
        if (options.Get("nonlethal") is IDictionary<string, object> nonlethal
            && nonlethal.Get("tag")?.ToString() is { } tag && tags.Contains(tag, StringComparer.OrdinalIgnoreCase)
            && nonlethal.Get("track")?.ToString() is { } nonlethalTrack)
        {
            return new[] { Adjust(nonlethalTrack, amount) };
        }

        return new[] { Adjust(options.Get("track")?.ToString() ?? "HP", -amount) };
    }

    private static object? Get(this IDictionary<string, object> map, string key) => map.TryGetValue(key, out var value) ? value : null;

    private static SheetIntent Adjust(string track, double by) =>
        new("adjust", null, new Dictionary<string, object?>(StringComparer.Ordinal) { ["track"] = track, ["by"] = by });

    private static List<string> Strings(object? value) => value switch
    {
        null => new List<string>(),
        string s => new List<string> { s },
        IEnumerable list => list.Cast<object?>().Select(o => o?.ToString()).Where(o => !string.IsNullOrEmpty(o)).Select(o => o!).ToList(),
        _ => new List<string> { value.ToString()! }
    };

    // ---- Load problems ----

    /// <summary>What is wrong with the system's handlers and the parts of tracks that act when a track changes, as the loader
    /// reports it: shapes and names. Formulas are checked when they run.</summary>
    public static IReadOnlyList<string> Problems(SystemDefinition system)
    {
        var problems = new List<string>();
        foreach (var (type, raw) in system.Events)
        {
            if (DirectKinds.Contains(type, StringComparer.Ordinal))
            {
                problems.Add($"The {type} handler never runs: an event of that type is the intent itself, applied as it comes.");
                continue;
            }

            switch (YamlValues.Normalize(raw))
            {
                case IDictionary<string, object> map when map.TryGetValue("builtin", out var builtin):
                    if (!Builtins.Contains(builtin?.ToString(), StringComparer.Ordinal)) problems.Add($"The {type} handler names \"{builtin}\", which is not a built-in handler ({string.Join(", ", Builtins)}).");
                    break;
                case IDictionary<string, object> map when map.ContainsKey("script"):
                    problems.Add($"The {type} handler is a script, which this version does not run: the event does nothing.");
                    break;
                case IEnumerable lines and not string and not IDictionary<string, object>:
                    foreach (var line in lines.Cast<object?>())
                    {
                        if (line is not IDictionary<string, object> entry || entry.Get("intents") is not IEnumerable intents)
                        {
                            problems.Add($"A {type} handler line is not {{ when, intents }}.");
                            continue;
                        }

                        if (entry.TryGetValue("target", out var target) && TargetProblem(system, target?.ToString()) is { } targetProblem)
                        {
                            problems.Add($"A {type} handler line: {targetProblem}");
                        }

                        foreach (var intent in intents.Cast<object?>())
                        {
                            if (intent is not IDictionary<string, object> { Count: 1 } one || one.First().Value is not IDictionary<string, object>)
                            {
                                problems.Add($"A {type} handler's intent is not one kind and its fields ({{ adjust: {{ track, by }} }}).");
                            }
                            else if (!IntentKinds.Contains(one.First().Key, StringComparer.Ordinal))
                            {
                                problems.Add($"A {type} handler asks for \"{one.First().Key}\", which is not an intent ({string.Join(", ", IntentKinds)}).");
                            }
                        }
                    }

                    break;
                default:
                    problems.Add($"The {type} handler is neither {{ builtin }}, {{ script }} nor a list of {{ when, intents }}.");
                    break;
            }
        }

        foreach (var kind in system.SheetKinds)
        {
            var keys = kind.Tracks.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var track in kind.Tracks)
            {
                var name = $"The {kind.Kind} track \"{track.Key}\"";
                if (track.Overflow is { } overflow)
                {
                    if (!keys.Contains(overflow.Into)) problems.Add($"{name} overflows into \"{overflow.Into}\", which is not a track of the sheet kind.");
                    if (overflow.Then.ToLowerInvariant() is not ("reset" or "carry" or "keep")) problems.Add($"{name}: overflow's then is reset, carry or keep, not \"{overflow.Then}\".");
                    if (track.Max == null) problems.Add($"{name} overflows, but has no maximum to overflow.");
                }

                var inLevels = track.Each == null && track.Levels is IEnumerable and not string;
                if (track.WhenFull != null && !track.WhenFull.Equals("pushUp", StringComparison.OrdinalIgnoreCase)) problems.Add($"{name}: whenFull is pushUp, not \"{track.WhenFull}\".");
                if ((track.WhenFull != null || track.Beyond != null) && !inLevels) problems.Add($"{name}: whenFull and beyond are for slots in levels.");
            }
        }

        return problems;
    }
}
