using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>What kind of time a <see cref="SystemMoment"/> is.</summary>
public enum MomentKind
{
    /// <summary>A rest from <c>vitals.rests</c>: the table also runs the rest's own rule (hit points, hit dice...).</summary>
    Rest,

    /// <summary>One the system declares in <c>moments:</c>.</summary>
    Declared,

    /// <summary>Built in: the initiative roll at the table.</summary>
    Initiative,

    /// <summary>Built in: the GM's dawn button.</summary>
    Dawn
}

/// <summary>A named time at which counters refill and the system's event handler of the same name runs. <see cref="OnSet"/>:
/// for a declared moment, the input value whose being set fires it.</summary>
public sealed record SystemMoment(string Key, string Label, MomentKind Kind, string? OnSet = null)
{
    public bool IsRest => Kind == MomentKind.Rest;
}

/// <summary>
/// The one place that knows a system's moments: its rests, the moments it declares, then <c>initiative</c> and <c>dawn</c>, which
/// every system has. <c>manual</c> is not a moment: it is a word a refill may name when only a person does it by hand.
/// </summary>
public static class SystemMoments
{
    public const string Initiative = FeatureRecovery.Initiative;
    public const string Dawn = FeatureRecovery.Dawn;
    public const string Manual = FeatureRecovery.Manual;

    /// <summary>Every moment of <paramref name="system"/>: rests, declared moments, initiative, dawn. With no system, the two built in.</summary>
    public static IReadOnlyList<SystemMoment> Of(SystemDefinition? system)
    {
        var moments = new List<SystemMoment>();
        foreach (var rest in system?.Vitals?.Rests ?? new List<RestRule>())
        {
            if (!string.IsNullOrWhiteSpace(rest.Name) && !moments.Any(m => Same(m.Key, rest.Name))) moments.Add(new SystemMoment(rest.Name, rest.Name, MomentKind.Rest));
        }

        foreach (var declared in system?.Moments ?? new List<MomentDefinition>())
        {
            if (!string.IsNullOrWhiteSpace(declared.Key) && !moments.Any(m => Same(m.Key, declared.Key)))
            {
                moments.Add(new SystemMoment(declared.Key, string.IsNullOrWhiteSpace(declared.Label) ? declared.Key : declared.Label, MomentKind.Declared, string.IsNullOrWhiteSpace(declared.OnSet) ? null : declared.OnSet));
            }
        }

        moments.Add(new SystemMoment(Initiative, Initiative, MomentKind.Initiative));
        moments.Add(new SystemMoment(Dawn, Dawn, MomentKind.Dawn));
        return moments;
    }

    /// <summary>The moment named <paramref name="key"/> (any case), or null.</summary>
    public static SystemMoment? Find(SystemDefinition? system, string key) => Of(system).FirstOrDefault(m => Same(m.Key, key));

    /// <summary>Whether <paramref name="key"/> names a moment of the system: a rest, a declared moment, initiative or dawn.</summary>
    public static bool IsMoment(SystemDefinition? system, string key) => Find(system, key) != null;

    /// <summary>The moments that are not rests: declared ones, initiative, dawn (what a refill may name besides a rest).</summary>
    public static IReadOnlyList<SystemMoment> NonRests(SystemDefinition? system) => Of(system).Where(m => !m.IsRest).ToList();

    /// <summary>Whether a refill's <c>on</c> means a rest: anything that is not <c>manual</c>, initiative, dawn or a declared moment
    /// (a slot pool's rest need not be one <c>vitals.rests</c> names).</summary>
    public static bool IsRest(SystemDefinition? system, string on) =>
        !Same(on, Manual) && !NonRests(system).Any(m => Same(m.Key, on));

    /// <summary>The words a refill's <c>on</c> may name that are not rests: declared moments, initiative, dawn, then <c>manual</c>.</summary>
    public static IReadOnlyList<string> RefillWords(SystemDefinition? system) => NonRests(system).Select(m => m.Key).Append(Manual).ToList();

    /// <summary>The moments a choice's <c>changesOn</c> may name: every moment (not <c>manual</c>, at which nothing happens).</summary>
    public static IReadOnlyList<string> ChangeMoments(SystemDefinition? system) => Of(system).Select(m => m.Key).ToList();

    /// <summary>The declared moment fired by setting the input value <paramref name="valueKey"/>, or null.</summary>
    public static SystemMoment? FiredBySetting(SystemDefinition? system, string valueKey) =>
        Of(system).FirstOrDefault(m => m.OnSet != null && Same(m.OnSet, valueKey));

    private static readonly string[] TrackIntents = { SheetEvents.AdjustKind, SheetEvents.SetKind, SheetEvents.MarkKind, SheetEvents.ClearKind };

    /// <summary>The literal <c>track:</c> keys that the system's handler for <paramref name="moment"/> names in its <c>adjust</c>, <c>set</c>,
    /// <c>mark</c> and <c>clear</c> intents, in order; null when the handler is a script, a built-in or a compiled type, which cannot be
    /// read without running it. A line that acts on the campaign's objects names none. Empty: no handler, or none that names a track.</summary>
    public static IReadOnlyList<string>? HandlerTracks(SystemDefinition? system, string moment)
    {
        if (system == null || !system.Events.TryGetValue(moment, out var raw)) return Array.Empty<string>();
        var handler = YamlValues.Normalize(raw);
        if (handler is IDictionary<string, object> || handler is not IEnumerable<object?> lines) return null;
        var tracks = new List<string>();
        foreach (var line in lines)
        {
            if (line is not IDictionary<string, object> entry) continue;
            if (entry.TryGetValue("target", out var target) && target != null) continue;
            if (entry.TryGetValue("intents", out var intents) && intents is IEnumerable<object?> listed)
            {
                foreach (var intent in listed)
                {
                    if (intent is IDictionary<string, object> { Count: 1 } one && TrackIntents.Contains(one.First().Key, StringComparer.Ordinal)
                        && one.First().Value is IDictionary<string, object> fields && fields.TryGetValue("track", out var track) && track is string name && name.Length > 0)
                    {
                        tracks.Add(name);
                    }
                }
            }
        }

        return tracks;
    }

    /// <summary>Whether the system's handler for <paramref name="moment"/> touches a sheet of <paramref name="kind"/>: it names, as a literal
    /// <c>track:</c>, a track that kind declares. A handler that cannot be read (a script) is taken to touch every kind.</summary>
    public static bool HandlerTouches(SystemDefinition? system, SheetKindDefinition? kind, string moment)
    {
        if (kind == null) return false;
        var named = HandlerTracks(system, moment);
        return named == null || named.Any(t => kind.Tracks.Any(k => Same(k.Key, t)));
    }

    /// <summary>Whether <paramref name="moment"/> does anything to this sheet: a counter of it refills at the moment (<c>uses_*_Rest</c>), a
    /// row choice on it is usually changed then (<c>changes_on__*</c>), or the system's handler for it touches the sheet's kind
    /// (<see cref="HandlerTouches"/>). The table and the sheet offer a moment's button only where this holds.</summary>
    public static bool Matters(SystemDefinition? system, IReadOnlyDictionary<string, object> sheet, string moment)
    {
        if (sheet.Keys.Any(k => SheetRuleEngine.IsUsesKey(k) && FeatureRecovery.For(sheet, k, moment).Count > 0)) return true;
        if (SheetRuleEngine.ChoicesToChangeAfter(sheet, moment).Count > 0) return true;
        return system is { SheetKinds.Count: > 0 } && HandlerTouches(system, SheetLayout.KindOf(system, sheet), moment);
    }

    /// <summary>What a moment does to a sheet, in the sheet's words: the tracks its handler names (<see cref="HandlerTracks"/>) that the sheet's
    /// kind has, and the feature counters that refill at it. A moment's button says this before it is pressed.</summary>
    public static (IReadOnlyList<string> Tracks, IReadOnlyList<string> Counters) Effects(SystemDefinition? system, IReadOnlyDictionary<string, object> sheet, string moment)
    {
        var kind = system is { SheetKinds.Count: > 0 } ? SheetLayout.KindOf(system, sheet) : null;
        var tracks = (HandlerTracks(system, moment) ?? Array.Empty<string>())
            .Select(t => kind?.Tracks.FirstOrDefault(k => Same(k.Key, t)))
            .OfType<TrackDefinition>()
            .Select(t => string.IsNullOrWhiteSpace(t.Label) ? t.Key : t.Label!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var counters = sheet.Keys.Where(k => SheetRuleEngine.IsUsesKey(k) && FeatureRecovery.For(sheet, k, moment).Count > 0)
            .Select(k => SheetRuleEngine.UsesLabel(sheet, k))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return (tracks, counters);
    }

    /// <summary>The names of what differs between two states of one sheet: a track of its kind, or a feature counter, whose number moved. In
    /// the order of the kind's tracks, then the counters. What a moment fired on the sheet says came back.</summary>
    public static IReadOnlyList<string> WhatChanged(SystemDefinition? system, IReadOnlyDictionary<string, object> before, IReadOnlyDictionary<string, object> after)
    {
        var kind = system is { SheetKinds.Count: > 0 } ? SheetLayout.KindOf(system, after) : null;
        var names = new List<string>();
        foreach (var track in kind?.Tracks ?? new List<TrackDefinition>())
        {
            if (Moved(before, after, track.Key)) names.Add(string.IsNullOrWhiteSpace(track.Label) ? track.Key : track.Label!);
        }

        foreach (var key in after.Keys.Where(SheetRuleEngine.IsUsesKey))
        {
            if (Moved(before, after, key)) names.Add(SheetRuleEngine.UsesLabel(after, key));
        }

        return names;
    }

    private static bool Moved(IReadOnlyDictionary<string, object> before, IReadOnlyDictionary<string, object> after, string key) =>
        YamlValues.ToNumber(before.GetValueOrDefault(key)) != YamlValues.ToNumber(after.GetValueOrDefault(key));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
