using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Codex.SystemCheck;

/// <summary>
/// A reference fixture's <c>spec.yaml</c> (tests/Codex.Tests/Fixtures/reference/README.md): the sheets, the campaign state, and
/// what a host must make of them. Plain data; <see cref="ReferenceRunner"/> checks it against a host.
/// </summary>
public sealed class ReferenceSpec
{
    public string System { get; set; } = "";
    public Dictionary<string, SpecSheet> Sheets { get; set; } = new();
    public Dictionary<string, object>? Campaign { get; set; }
    public List<SpecExpectation> Expectations { get; set; } = new();

    /// <summary>The fixture's folder name: d35, street, crew.</summary>
    public string Fixture { get; set; } = "";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    /// <summary>Reads a spec, refusing any key the model has no property for: a spec with a typo would otherwise check nothing.</summary>
    public static ReferenceSpec Load(string path)
    {
        var yaml = File.ReadAllText(path);
        var unknown = UnknownYamlKeys.Find(yaml, typeof(ReferenceSpec), CamelCaseNamingConvention.Instance);
        if (unknown.Count > 0) throw new InvalidDataException($"{path}: keys the spec format does not have: {string.Join(", ", unknown)}");
        var spec = Deserializer.Deserialize<ReferenceSpec>(yaml);
        spec.Fixture = Path.GetFileName(Path.GetDirectoryName(path))!;
        return spec;
    }
}

/// <summary>A sheet: entered values, list picks (a list of maps, or a map by key for a keyed list), track values and
/// conditions. In an expectation's <c>with</c>, laid over the named sheet: maps merge key by key, lists replace.</summary>
public sealed class SpecSheet
{
    public string? Kind { get; set; }
    public Dictionary<string, object> Values { get; set; } = new();
    public Dictionary<string, object> Lists { get; set; } = new();
    public Dictionary<string, object> Tracks { get; set; } = new();
    public List<string>? Conditions { get; set; }

    /// <summary>Only in <c>with</c>: campaign state laid over the spec's.</summary>
    public Dictionary<string, object>? Campaign { get; set; }
}

public sealed class SpecExpectation
{
    public string Id { get; set; } = "";

    /// <summary>The overhaul step (docs/SYSTEM_IMPLEMENTATION_OVERHAUL.md, section 7) meant to make this pass.</summary>
    public int Step { get; set; }

    public string? Sheet { get; set; }
    public string? Why { get; set; }
    public SpecSheet? With { get; set; }
    public SpecRoll? Roll { get; set; }

    /// <summary>An event raised on the sheet (or the campaign): <c>type</c> plus its own fields.</summary>
    public Dictionary<string, object>? Event { get; set; }

    /// <summary>For an event: the faces its handler's dice are handed, in order (a script's <c>ctx.d</c>).</summary>
    public List<int>? Dice { get; set; }

    public SpecExpect Expect { get; set; } = new();

    /// <summary>Expectations this one counts only alongside. "No problems" passes vacuously on a host that checks nothing, so an
    /// expectation that only says what must not happen names the ones that show the check exists.</summary>
    public List<string>? Requires { get; set; }
}

public sealed class SpecRoll
{
    public string Definition { get; set; } = "";
    public Dictionary<string, object> Inputs { get; set; } = new();

    /// <summary>The faces the seeded dice source hands out, in order.</summary>
    public List<int> Dice { get; set; } = new();

    /// <summary>For a roll several actors join: each one's faces, by sheet id.</summary>
    public Dictionary<string, List<int>>? Participants { get; set; }
}

public sealed class SpecExpect
{
    public Dictionary<string, double>? Values { get; set; }

    /// <summary>Exactly these problems, as (field, key) pairs, in any order; empty: none.</summary>
    public List<SpecViolation>? Violations { get; set; }

    /// <summary>Exactly these warnings for the GM (a formula, script or compiled function that failed), as (field, key) pairs, in
    /// any order; empty: none.</summary>
    public List<SpecViolation>? Warnings { get; set; }

    /// <summary>Exactly these conditions hold, from thresholds and bands, with no event raised.</summary>
    public List<string>? Conditions { get; set; }

    public string? Outcome { get; set; }
    public double? Total { get; set; }

    /// <summary>One total per roll made, the first roll first (a confirmation roll follows its attack).</summary>
    public List<double>? Totals { get; set; }

    public List<int>? Kept { get; set; }
    public int? DiceUsed { get; set; }
    public Dictionary<string, object>? Carries { get; set; }

    /// <summary>Exactly these intents, in order, as the handler returns them.</summary>
    public List<Dictionary<string, object>>? Intents { get; set; }

    public SpecAfter? After { get; set; }

    /// <summary>The sheet kind's layout, as its section names; empty: the generic layout.</summary>
    public List<string>? Layout { get; set; }

    /// <summary>Says only what must not happen: no problems, no conditions, no layout of its own.</summary>
    public bool IsOnlyNegative =>
        (Violations is { Count: 0 } || Warnings is { Count: 0 } || Conditions is { Count: 0 } || Layout is { Count: 0 })
        && Violations is null or { Count: 0 } && Conditions is null or { Count: 0 } && Layout is null or { Count: 0 }
        && Values == null && Outcome == null && Total == null && Totals == null && Kept == null && DiceUsed == null && Carries == null
        && Intents == null && After == null && Warnings is null or { Count: 0 };

    public bool IsEmpty => Values == null && Violations == null && Warnings == null && Conditions == null && Outcome == null && Total == null && Totals == null
        && Kept == null && DiceUsed == null && Carries == null && Intents == null && After == null && Layout == null;
}

/// <summary>State after the roll or event and the intents it produced were applied. Only what is named is checked.</summary>
public sealed class SpecAfter
{
    public Dictionary<string, object>? Tracks { get; set; }
    public List<string>? Conditions { get; set; }
    public Dictionary<string, double>? Values { get; set; }
    public Dictionary<string, object>? Campaign { get; set; }
}

public sealed class SpecViolation
{
    public string Field { get; set; } = "";
    public string Key { get; set; } = "";

    public override string ToString() => $"{Field} {Key}";
}

/// <summary>Builds the plain sheet a host reads from a spec sheet with an expectation's <c>with</c> laid over it.</summary>
public static class SpecSheets
{
    public static Dictionary<string, object> Compose(ReferenceSpec spec, SpecExpectation expectation)
    {
        var sheet = new Dictionary<string, object>();
        if (expectation.Sheet != null && spec.Sheets.TryGetValue(expectation.Sheet, out var named)) Lay(sheet, named);
        if (expectation.With != null) Lay(sheet, expectation.With);
        return sheet;
    }

    private static void Lay(Dictionary<string, object> sheet, SpecSheet part)
    {
        if (part.Kind != null) sheet["kind"] = part.Kind;
        foreach (var source in new[] { part.Values, part.Lists, part.Tracks })
        {
            foreach (var (key, value) in YamlValues.NormalizeMap(source)) sheet[key] = Merge(sheet.GetValueOrDefault(key), value);
        }

        if (part.Conditions != null) sheet["conditions"] = part.Conditions.Cast<object>().ToList();
    }

    /// <summary>The campaign's objects for an expectation: the spec's, with the expectation's <c>with.campaign</c> over them
    /// (each key's list replaces the spec's).</summary>
    public static Dictionary<string, object> Campaign(ReferenceSpec spec, SpecExpectation expectation)
    {
        var campaign = CampaignObjects.Copy(YamlValues.NormalizeMap(spec.Campaign));
        foreach (var (key, value) in YamlValues.NormalizeMap(expectation.With?.Campaign)) campaign[key] = value;
        return campaign;
    }

    /// <summary>Maps merge key by key, anything else replaces.</summary>
    public static object Merge(object? under, object over)
    {
        if (under is not Dictionary<string, object> left || over is not Dictionary<string, object> right) return over;
        var merged = new Dictionary<string, object>(left);
        foreach (var (key, value) in right) merged[key] = Merge(merged.GetValueOrDefault(key), value);
        return merged;
    }
}
