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
/// for a declared moment, the input value whose being set fires it. <see cref="ActorOnly"/>: one creature's own act (a sheet button), no time
/// of the table; <see cref="While"/>: the condition the button waits for.</summary>
public sealed record SystemMoment(string Key, string Label, MomentKind Kind, string? OnSet = null, bool ActorOnly = false, string? While = null, string? Note = null)
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
                moments.Add(new SystemMoment(declared.Key, string.IsNullOrWhiteSpace(declared.Label) ? declared.Key : declared.Label, MomentKind.Declared, string.IsNullOrWhiteSpace(declared.OnSet) ? null : declared.OnSet,
                    declared.IsActorScoped, string.IsNullOrWhiteSpace(declared.While) ? null : declared.While,
                    string.IsNullOrWhiteSpace(declared.Note) ? null : declared.Note));
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
    public static IReadOnlyList<string> RefillWords(SystemDefinition? system) => NonRests(system).Where(m => !m.ActorOnly).Select(m => m.Key).Append(Manual).ToList();

    /// <summary>The moments a choice's <c>changesOn</c> may name: every moment of the table (not <c>manual</c>, at which nothing happens, and not a
    /// creature's own act, which is no time of the table).</summary>
    public static IReadOnlyList<string> ChangeMoments(SystemDefinition? system) => Of(system).Where(m => !m.ActorOnly).Select(m => m.Key).ToList();

    /// <summary>The moments the console's party bar may offer: declared ones that are times of the table (not one creature's own act) and dawn.
    /// The console keeps those that matter to some character.</summary>
    public static IReadOnlyList<SystemMoment> PartyMoments(SystemDefinition? system) =>
        Of(system).Where(m => !m.ActorOnly && m.Kind is MomentKind.Declared or MomentKind.Dawn).ToList();

    /// <summary>Whether <paramref name="key"/> names a time of the table: a moment that is not one creature's own act.</summary>
    public static bool IsTableMoment(SystemDefinition? system, string key) => Find(system, key) is { ActorOnly: false };

    /// <summary>Whether the moment's button is offered on this sheet: it does something to it (<see cref="Matters"/>) and, when the moment says
    /// <c>while</c>, that condition holds on the sheet.</summary>
    public static bool Offered(SystemDefinition? system, IReadOnlyDictionary<string, object> sheet, string moment)
    {
        if (!Matters(system, sheet, moment)) return false;
        if (system == null || Find(system, moment)?.While is not { } condition) return true;
        return SheetConditions.Holding(system, sheet).Any(h => Same(h.Name, condition));
    }

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

    /// <summary>Whether the console's party bar sends <paramref name="moment"/> to the player character with this sheet. Dawn goes to every character, as it always
    /// has. A member of the party gets any moment that matters to it (<see cref="Matters"/>). A companion or familiar (<c>partyMember: false</c>,
    /// <see cref="SheetLayout.IsPartyMember"/>) gets it only through its own sheet keys (a counter that refills then, a choice usually changed then, a slot pool), never
    /// through the system's event handler alone: that is how a session's hero point arrives, and a companion is not given one.</summary>
    public static bool ReachesPartyMember(SystemDefinition? system, IReadOnlyDictionary<string, object> sheet, string moment) =>
        Same(moment, FeatureRecovery.Dawn) || SheetLayout.IsPartyMember(system, sheet) || MattersByItsKeys(system, sheet, moment);

    /// <summary>Whether the system's own handler for a moment (<c>events.{moment}</c>) runs for this sheet. The handlers are the party's (a session's hero point), so
    /// only a member of the party gets them; a companion or familiar (<c>partyMember: false</c>) still gets its own sheet's recoveries (counters, slot pools, choices,
    /// tracks) at every moment. One rule for the party bar, the sheet's own moment button and <c>MomentCommand</c>.</summary>
    public static bool RunsHandler(SystemDefinition? system, IReadOnlyDictionary<string, object> sheet) => SheetLayout.IsPartyMember(system, sheet);

    /// <summary>Whether <paramref name="moment"/> does anything to this sheet: a counter of it refills at the moment (<c>uses_*_Rest</c>), a
    /// row choice on it is usually changed then (<c>changes_on__*</c>), or the system's handler for it touches the sheet's kind
    /// (<see cref="HandlerTouches"/>). The table and the sheet offer a moment's button only where this holds.</summary>
    public static bool Matters(SystemDefinition? system, IReadOnlyDictionary<string, object> sheet, string moment) =>
        MattersByItsKeys(system, sheet, moment)
        || (system is { SheetKinds.Count: > 0 } && RunsHandler(system, sheet) && HandlerTouches(system, SheetLayout.KindOf(system, sheet), moment));

    private static bool MattersByItsKeys(SystemDefinition? system, IReadOnlyDictionary<string, object> sheet, string moment) =>
        sheet.Keys.Any(k => SheetRuleEngine.IsUsesKey(k) && FeatureRecovery.For(sheet, k, moment).Count > 0)
        || SlotKeysRecoveredAt(system, sheet.Keys, moment).Count > 0
        || SheetRuleEngine.ChoicesToChangeAfter(sheet, moment).Count > 0;

    /// <summary>The slot pools whose <c>recoversOn</c> names <paramref name="moment"/>: a declared moment or dawn refills them as a rest does, with no rest
    /// button of its own.</summary>
    public static IReadOnlyList<SlotPoolRule> SlotPoolsRecoveringAt(SystemDefinition? system, string moment) =>
        system?.SheetRules?.SlotPools.Where(p => p.RecoversOn.Any(r => Same(r, moment))).ToList() ?? new List<SlotPoolRule>();

    /// <summary>The slot keys among <paramref name="keys"/> (a sheet's or a table pool's) that <see cref="SlotPoolsRecoveringAt"/> refills at the
    /// moment: each slot level's own key, never its maximum.</summary>
    public static IReadOnlyList<string> SlotKeysRecoveredAt(SystemDefinition? system, IEnumerable<string> keys, string moment)
    {
        var pools = SlotPoolsRecoveringAt(system, moment);
        if (pools.Count == 0) return Array.Empty<string>();
        return keys.Where(k => !k.EndsWith("_Max", StringComparison.Ordinal) && pools.Any(p => p.Owns(k))).ToList();
    }

    /// <summary>Off the table: fills each slot key <see cref="SlotKeysRecoveredAt"/> names to its maximum on <paramref name="sheet"/> (the sheet's own
    /// moment button; the table's command does the same on its pool). Returns the keys that were set.</summary>
    public static IReadOnlyList<string> RefillSlots(SystemDefinition? system, Dictionary<string, object> sheet, string moment)
    {
        var filled = new List<string>();
        foreach (var key in SlotKeysRecoveredAt(system, sheet.Keys.ToList(), moment))
        {
            if (YamlValues.GetInt(sheet, SystemDefinition.PoolMaxKey(system, key)) is not { } max) continue;
            sheet[key] = (long)max;
            filled.Add(key);
        }

        return filled;
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
            .Concat(SlotPoolsRecoveringAt(system, moment).Where(p => sheet.Keys.Any(p.Owns)).Select(p => p.Label))
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

        foreach (var pool in system?.SheetRules?.SlotPools ?? new List<SlotPoolRule>())
        {
            if (after.Keys.Any(k => pool.Owns(k) && !k.EndsWith("_Max", StringComparison.Ordinal) && Moved(before, after, k)) && !names.Contains(pool.Label)) names.Add(pool.Label);
        }

        return names;
    }

    private static bool Moved(IReadOnlyDictionary<string, object> before, IReadOnlyDictionary<string, object> after, string key) =>
        // A track not yet written holds 0 (unmarked), so setting it to 0 brought nothing back.
        (YamlValues.ToNumber(before.GetValueOrDefault(key)) ?? 0) != (YamlValues.ToNumber(after.GetValueOrDefault(key)) ?? 0);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
