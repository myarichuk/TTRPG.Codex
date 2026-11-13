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

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
