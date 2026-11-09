using System.Globalization;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests.Reference;

public enum CheckStatus
{
    /// <summary>The host did what the expectation says.</summary>
    Pass,

    /// <summary>The host tried and got something else.</summary>
    Fail,

    /// <summary>The host has nothing that can do this yet (no rolls, no events...).</summary>
    Unsupported
}

public sealed record CheckResult(SpecExpectation Expectation, ReferenceSpec Spec, CheckStatus Status, string Detail)
{
    public string Id => Expectation.Id;
}

/// <summary>
/// What a host can be asked by a spec. The only host today is <see cref="CurrentEngineHost"/>, the rules engine as it stands;
/// as overhaul steps land it learns rolls, events and the rest, or a new host takes its place behind this interface.
/// </summary>
public interface IReferenceHost
{
    /// <summary>Loads the fixture's system and packs. Null when the system did not load (the reason in <paramref name="problem"/>).</summary>
    IReferenceSystem? Load(string fixtureRoot, out string? problem);
}

public interface IReferenceSystem
{
    string Id { get; }

    /// <summary>Values the host derives from the sheet, by key; a key the host does not produce is absent.</summary>
    CheckOr<IReadOnlyDictionary<string, double>> Values(Dictionary<string, object> sheet);

    /// <summary>The problems the host reports for the sheet, as (field, key).</summary>
    CheckOr<IReadOnlyList<(string Field, string Key)>> Violations(Dictionary<string, object> sheet);

    CheckOr<IReadOnlyList<string>> Conditions(Dictionary<string, object> sheet);
    string? CannotRoll { get; }
    string? CannotRaiseEvents { get; }
    string? CannotLayOut { get; }
    string? HasNoCampaignObjects { get; }
}

/// <summary>A result, or why the host cannot produce one.</summary>
public readonly record struct CheckOr<T>(T? Value, string? Unsupported)
{
    public static CheckOr<T> Of(T value) => new(value, null);
    public static CheckOr<T> No(string why) => new(default, why);
}

public static class ReferenceRunner
{
    public static readonly string Root = Path.Combine(TestPaths.RepoRoot, "tests", "Codex.Tests", "Fixtures", "reference");
    public static readonly string LedgerFile = Path.Combine(Root, "progress.txt");

    public static IReadOnlyList<string> FixtureRoots() =>
        Directory.GetDirectories(Root).Where(d => File.Exists(Path.Combine(d, "spec.yaml"))).Order(StringComparer.Ordinal).ToList();

    public static ReferenceSpec SpecOf(string fixtureRoot) => ReferenceSpec.Load(Path.Combine(fixtureRoot, "spec.yaml"));

    /// <summary>The ids that pass today, from progress.txt: one per line, <c>#</c> starts a comment.</summary>
    public static IReadOnlySet<string> Ledger() =>
        File.ReadAllLines(LedgerFile).Select(l => (l.IndexOf('#') is var hash and >= 0 ? l[..hash] : l).Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<CheckResult> RunAll(IReferenceHost host)
    {
        var results = new List<CheckResult>();
        foreach (var root in FixtureRoots())
        {
            var spec = SpecOf(root);
            var system = host.Load(root, out var problem);
            foreach (var expectation in spec.Expectations)
            {
                results.Add(system == null
                    ? new CheckResult(expectation, spec, CheckStatus.Fail, $"the system did not load: {problem}")
                    : Check(system, spec, expectation));
            }
        }

        // An expectation counts only alongside the ones it requires.
        var passing = results.Where(r => r.Status == CheckStatus.Pass).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        return results.Select(r => r.Status == CheckStatus.Pass && r.Expectation.Requires?.FirstOrDefault(id => !passing.Contains(id)) is { } missing
            ? r with { Status = CheckStatus.Fail, Detail = $"holds, but only counts alongside {missing}, which does not pass" }
            : r).ToList();
    }

    public static CheckResult Check(IReferenceSystem system, ReferenceSpec spec, SpecExpectation expectation)
    {
        CheckResult Result(CheckStatus status, string detail) => new(expectation, spec, status, detail);
        var expect = expectation.Expect;

        if (expectation.Roll != null && system.CannotRoll is { } noRolls) return Result(CheckStatus.Unsupported, noRolls);
        if (expectation.Event != null && system.CannotRaiseEvents is { } noEvents) return Result(CheckStatus.Unsupported, noEvents);
        if (expect.Layout != null && system.CannotLayOut is { } noLayout) return Result(CheckStatus.Unsupported, noLayout);
        if ((expectation.With?.Campaign != null || expect.After?.Campaign != null || (expectation.Sheet == null && expectation.Event != null))
            && system.HasNoCampaignObjects is { } noCampaign) return Result(CheckStatus.Unsupported, noCampaign);
        if (expectation.Roll != null || expectation.Event != null || expect.Layout != null)
        {
            return Result(CheckStatus.Unsupported, "this runner checks rolls, events and layout once a host can do them; extend ReferenceRunner.Check");
        }

        var sheet = SpecSheets.Compose(spec, expectation);
        var problems = new List<string>();

        if (expect.Values != null)
        {
            var values = system.Values(sheet);
            if (values.Unsupported is { } why) return Result(CheckStatus.Unsupported, why);
            foreach (var (key, wanted) in expect.Values)
            {
                if (!values.Value!.TryGetValue(key, out var actual)) problems.Add($"{key}: no value, expected {Show(wanted)}");
                else if (Math.Abs(actual - wanted) > 1e-9) problems.Add($"{key}: {Show(actual)}, expected {Show(wanted)}");
            }
        }

        if (expect.Violations != null)
        {
            var found = system.Violations(sheet);
            if (found.Unsupported is { } why) return Result(CheckStatus.Unsupported, why);
            var wanted = expect.Violations.Select(v => (v.Field, v.Key)).OrderBy(v => v).ToList();
            var actual = found.Value!.OrderBy(v => v).ToList();
            if (!wanted.SequenceEqual(actual))
            {
                problems.Add($"violations: [{string.Join("; ", actual.Select(v => $"{v.Field} {v.Key}"))}], expected [{string.Join("; ", wanted.Select(v => $"{v.Field} {v.Key}"))}]");
            }
        }

        if (expect.Conditions != null)
        {
            var found = system.Conditions(sheet);
            if (found.Unsupported is { } why) return Result(CheckStatus.Unsupported, why);
            var wanted = expect.Conditions.Order(StringComparer.Ordinal).ToList();
            var actual = found.Value!.Order(StringComparer.Ordinal).ToList();
            if (!wanted.SequenceEqual(actual)) problems.Add($"conditions: [{string.Join(", ", actual)}], expected [{string.Join(", ", wanted)}]");
        }

        return problems.Count == 0 ? Result(CheckStatus.Pass, "") : Result(CheckStatus.Fail, string.Join("; ", problems));
    }

    private static string Show(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// The rules engine as it is: <see cref="SheetRuleEngine.Recompute"/> over the fixture's system and packs, a value being any
/// number it leaves on the sheet or derives. It has no rolls with outcomes, events, layout or campaign objects, and says so.
/// </summary>
public sealed class CurrentEngineHost : IReferenceHost
{
    public IReferenceSystem? Load(string fixtureRoot, out string? problem)
    {
        var registry = new ContentRegistry();
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(registry));
        loader.LoadAndInitializeAsync(fixtureRoot).GetAwaiter().GetResult();
        var id = loader.LoadedSystemIds.SingleOrDefault();
        if (id == null)
        {
            problem = string.Join(" ", loader.FailedSystems.SelectMany(f => f.Errors).DefaultIfEmpty("no system found"));
            return null;
        }

        problem = null;
        return new System(loader.GetDefinition(id)!, registry);
    }

    private sealed class System(SystemDefinition definition, ContentRegistry registry) : IReferenceSystem, IEntryResolver
    {
        public string Id => definition.Id;

        public string? CannotRoll => "the dice parser rolls expressions; there are no roll definitions with inputs and outcomes (step 4)";
        public string? CannotRaiseEvents => "the table hard-codes damage, death saves and rests; there are no event handlers (step 5)";
        public string? CannotLayOut => "the sheet page composes fixed panels; there is no layout (step 6)";
        public string? HasNoCampaignObjects => "there are no clocks or campaign tables (step 7)";

        public CheckOr<IReadOnlyDictionary<string, double>> Values(Dictionary<string, object> sheet)
        {
            var (result, evaluation) = Evaluate(sheet);
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (key, value) in result) if (Number(value) is { } n) values[key] = n;
            foreach (var (key, value) in evaluation.Derived) if (Number(value) is { } n) values[key] = n;
            return CheckOr<IReadOnlyDictionary<string, double>>.Of(values);
        }

        public CheckOr<IReadOnlyList<(string Field, string Key)>> Violations(Dictionary<string, object> sheet)
        {
            var (_, evaluation) = Evaluate(sheet);
            return CheckOr<IReadOnlyList<(string, string)>>.Of(evaluation.Violations.Select(v => (v.FieldKey, v.Key ?? "")).ToList());
        }

        public CheckOr<IReadOnlyList<string>> Conditions(Dictionary<string, object> sheet)
        {
            // A system with sheet kinds has tracks whose thresholds and bands bring conditions (SheetModel); one without has
            // only the combat console's.
            var (_, evaluation) = Evaluate(sheet);
            return evaluation.Model is { } model
                ? CheckOr<IReadOnlyList<string>>.Of(model.Conditions)
                : CheckOr<IReadOnlyList<string>>.No("conditions come only from the combat console: the system has no sheet kinds, so no tracks with thresholds or bands (step 3)");
        }

        private (Dictionary<string, object> Sheet, SheetEvaluation Evaluation) Evaluate(Dictionary<string, object> input)
        {
            var sheet = new Dictionary<string, object>(input);
            var evaluation = new SheetRuleEngine(definition, this).Recompute(sheet);
            return (sheet, evaluation);
        }

        private static double? Number(object? value) => value switch
        {
            long l => l,
            int i => i,
            double d => d,
            _ => null
        };

        public ContentEntry? Resolve(string type, string id) => registry.GetEntries(definition.Id, type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries(definition.Id, type);
    }
}
