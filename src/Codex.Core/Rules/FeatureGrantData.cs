using System.Globalization;

namespace Codex.Core.Rules;

/// <summary>One modifier of a feature: <c>{ target, amount, type, when }</c>. Keys the editor does not know stay in <see cref="Extra"/>.</summary>
public sealed class GrantModifier
{
    public string Target { get; set; } = string.Empty;
    public string Amount { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string When { get; set; } = string.Empty;
    public Dictionary<string, object> Extra { get; } = new();
}

/// <summary>One thing a feature adds to a list: <c>{ to, entries, level }</c>.</summary>
public sealed class GrantAdd
{
    public string To { get; set; } = string.Empty;
    public List<string> Entries { get; set; } = new();
    public int? Level { get; set; }
    public Dictionary<string, object> Extra { get; } = new();
}

/// <summary>A feature's counter: <c>{ max, recoversOn, key }</c>.</summary>
public sealed class GrantUses
{
    public string Max { get; set; } = "1";
    public List<string> RecoversOn { get; set; } = new();
    public Dictionary<string, object> Extra { get; } = new();
}

/// <summary>
/// The <c>modifiers</c>, <c>adds</c> and <c>uses</c> of one feature (a row, or a whole entry) as the feature editor edits them.
/// <see cref="Read"/> takes them out of the feature's dictionary the way the sheet rules read them (a lone map counts as a
/// list of one; <c>uses: 2</c> as a counter of two), <see cref="Apply"/> puts them back: numbers as numbers, formulas as
/// text, and a structure with nothing in it is left out. A modifier or add being filled in stays, so the editor can show it.
/// </summary>
public sealed class FeatureGrantData
{
    public List<GrantModifier> Modifiers { get; } = new();
    public List<GrantAdd> Adds { get; } = new();
    public GrantUses? Uses { get; set; }

    /// <summary>The grants held in <paramref name="feature"/>; empty when it holds none.</summary>
    public static FeatureGrantData Read(IReadOnlyDictionary<string, object>? feature)
    {
        var data = new FeatureGrantData();
        if (feature == null) return data;

        foreach (var map in Maps(feature.GetValueOrDefault("modifiers")))
        {
            var modifier = new GrantModifier
            {
                Target = Text(map, "target"), Amount = Text(map, "amount"), Type = Text(map, "type"), When = Text(map, "when")
            };
            CopyExtra(map, modifier.Extra, "target", "amount", "type", "when");
            data.Modifiers.Add(modifier);
        }

        foreach (var map in Maps(feature.GetValueOrDefault("adds")))
        {
            var add = new GrantAdd { To = Text(map, "to"), Entries = Texts(Get(map, "entries")), Level = Whole(Get(map, "level")) };
            CopyExtra(map, add.Extra, "to", "entries", "level");
            data.Adds.Add(add);
        }

        switch (feature.GetValueOrDefault("uses"))
        {
            case IDictionary<string, object> map:
                var uses = new GrantUses { Max = Text(map, "max"), RecoversOn = Texts(Get(map, "recoversOn")) };
                CopyExtra(map, uses.Extra, "max", "recoversOn");
                data.Uses = uses;
                break;
            case null or string { Length: 0 }:
                break;
            case var plain when Convert.ToString(plain, CultureInfo.InvariantCulture) is { Length: > 0 } text:
                data.Uses = new GrantUses { Max = text };
                break;
        }

        return data;
    }

    /// <summary>Writes the grants into <paramref name="feature"/>, replacing its <c>modifiers</c>, <c>adds</c> and <c>uses</c>
    /// and removing a key that has nothing left. Every other key of the feature is untouched.</summary>
    public void Apply(IDictionary<string, object> feature)
    {
        Set(feature, "modifiers", Modifiers.Count == 0 ? null : Modifiers.Select(ModifierMap).Cast<object>().ToList());
        Set(feature, "adds", Adds.Count == 0 ? null : Adds.Select(AddMap).Cast<object>().ToList());
        Set(feature, "uses", Uses == null ? null : UsesMap(Uses));
    }

    /// <summary>How many modifiers, adds and counters the feature has that the sheet will apply (a target or a list chosen,
    /// entries given, a maximum set).</summary>
    public (int Modifiers, int Adds, int Counters) Counts() =>
        (Modifiers.Count(m => m.Target.Length > 0), Adds.Count(a => a.To.Length > 0 && a.Entries.Count > 0), Uses is { Max.Length: > 0 } ? 1 : 0);

    /// <summary>A number typed into an amount, a maximum or a level stays a number; anything else is a formula, and stays text.</summary>
    public static object Value(string text)
    {
        var trimmed = text.Trim();
        if (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)) return whole;
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return number;
        return trimmed;
    }

    /// <summary>Entries typed as <c>"a, b"</c>, split on commas and trimmed.</summary>
    public static List<string> SplitEntries(string? raw) =>
        (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>A level typed in, or null when the box is empty or is no whole number from 1 up.</summary>
    public static int? ParseLevel(string? raw) =>
        int.TryParse(raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var level) && level >= 1 ? level : null;

    private static Dictionary<string, object> ModifierMap(GrantModifier m)
    {
        var map = new Dictionary<string, object>(m.Extra) { ["target"] = m.Target };
        if (m.Amount.Trim().Length > 0) map["amount"] = Value(m.Amount);
        if (m.Type.Trim().Length > 0) map["type"] = m.Type.Trim();
        if (m.When.Trim().Length > 0) map["when"] = m.When.Trim();
        return map;
    }

    private static Dictionary<string, object> AddMap(GrantAdd a)
    {
        var map = new Dictionary<string, object>(a.Extra) { ["to"] = a.To };
        if (a.Entries.Count > 0) map["entries"] = a.Entries.Cast<object>().ToList();
        if (a.Level is { } level) map["level"] = (long)level;
        return map;
    }

    private static Dictionary<string, object> UsesMap(GrantUses u)
    {
        var map = new Dictionary<string, object>(u.Extra) { ["max"] = Value(u.Max) };
        if (u.RecoversOn.Count > 0) map["recoversOn"] = u.RecoversOn.Cast<object>().ToList();
        return map;
    }

    private static void Set(IDictionary<string, object> feature, string key, object? value)
    {
        if (value == null) feature.Remove(key);
        else feature[key] = value;
    }

    private static void CopyExtra(IDictionary<string, object> from, Dictionary<string, object> to, params string[] known)
    {
        foreach (var (key, value) in from)
        {
            if (!known.Contains(key, StringComparer.Ordinal)) to[key] = value;
        }
    }

    private static IEnumerable<IDictionary<string, object>> Maps(object? value) => value switch
    {
        IDictionary<string, object> one => new[] { one },
        string => Enumerable.Empty<IDictionary<string, object>>(),
        IEnumerable<object> many => many.OfType<IDictionary<string, object>>(),
        _ => Enumerable.Empty<IDictionary<string, object>>()
    };

    private static object? Get(IDictionary<string, object> map, string key) => map.TryGetValue(key, out var value) ? value : null;

    private static string Text(IDictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty : string.Empty;

    private static List<string> Texts(object? value) => value switch
    {
        null => new List<string>(),
        string text => SplitEntries(text),
        IEnumerable<object> many => many.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty).Where(v => v.Length > 0).ToList(),
        _ => new List<string> { Convert.ToString(value, CultureInfo.InvariantCulture)! }
    };

    private static int? Whole(object? value) =>
        value == null ? null : double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (int)n : null;
}
