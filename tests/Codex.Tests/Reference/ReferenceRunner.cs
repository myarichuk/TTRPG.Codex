using System.Globalization;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
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

    /// <summary>Makes a roll on the sheet with the spec's faces, and applies what it asks of the roller's sheet. For a group
    /// roll, <paramref name="participants"/> are the sheets joining it, by spec sheet id.</summary>
    CheckOr<RollCheck> Roll(Dictionary<string, object> sheet, string? sheetId, SpecRoll roll, IReadOnlyDictionary<string, Dictionary<string, object>> participants);

    string? CannotRoll { get; }
    string? CannotRaiseEvents { get; }
    string? CannotLayOut { get; }
    string? HasNoCampaignObjects { get; }
}

/// <summary>What a roll came to, as a spec reads it: <see cref="After"/> is the roller's sheet once the roll's intents were applied.</summary>
public sealed record RollCheck(string? Outcome, IReadOnlyList<double> Totals, IReadOnlyList<int> Kept, int DiceUsed,
    IReadOnlyDictionary<string, object?> Carries, Dictionary<string, object> After);

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
        if (expectation.Event != null || expect.Layout != null)
        {
            return Result(CheckStatus.Unsupported, "this runner checks events and layout once a host can do them; extend ReferenceRunner.Check");
        }

        var sheet = SpecSheets.Compose(spec, expectation);
        var problems = new List<string>();

        if (expectation.Roll is { } roll)
        {
            var participants = (roll.Participants?.Keys ?? Enumerable.Empty<string>())
                .ToDictionary(id => id, id => SpecSheets.Compose(spec, new SpecExpectation { Sheet = id }), StringComparer.Ordinal);
            var rolled = system.Roll(sheet, expectation.Sheet, roll, participants);
            if (rolled.Unsupported is { } why) return Result(CheckStatus.Unsupported, why);
            var made = rolled.Value!;
            if (expect.Outcome != null && expect.Outcome != made.Outcome) problems.Add($"outcome: {made.Outcome ?? "none"}, expected {expect.Outcome}");
            if (expect.Total is { } total && (made.Totals.Count == 0 || Math.Abs(made.Totals[0] - total) > 1e-9))
            {
                problems.Add($"total: {(made.Totals.Count == 0 ? "none" : Show(made.Totals[0]))}, expected {Show(total)}");
            }

            if (expect.Totals != null && !expect.Totals.SequenceEqual(made.Totals))
            {
                problems.Add($"totals: [{string.Join(", ", made.Totals.Select(Show))}], expected [{string.Join(", ", expect.Totals.Select(Show))}]");
            }

            if (expect.Kept != null && !expect.Kept.SequenceEqual(made.Kept)) problems.Add($"kept: [{string.Join(", ", made.Kept)}], expected [{string.Join(", ", expect.Kept)}]");
            if (expect.DiceUsed is { } used && used != made.DiceUsed) problems.Add($"dice used: {made.DiceUsed}, expected {used}");
            foreach (var (key, wanted) in expect.Carries ?? new Dictionary<string, object>())
            {
                var carried = made.Carries.GetValueOrDefault(key);
                if (!string.Equals(carried?.ToString(), wanted?.ToString(), StringComparison.Ordinal)) problems.Add($"carries {key}: {carried ?? "nothing"}, expected {wanted}");
            }

            sheet = made.After;
        }

        if (expect.After is { } after)
        {
            if (expectation.Roll == null) return Result(CheckStatus.Unsupported, "state after an event needs events (step 5)");
            foreach (var (key, wanted) in YamlValues.NormalizeMap(after.Tracks))
            {
                var actual = YamlValues.Normalize(sheet.GetValueOrDefault(key));
                if (!Same(actual, wanted)) problems.Add($"after, {key}: {Describe(actual)}, expected {Describe(wanted)}");
            }

            if (after.Values != null)
            {
                var values = system.Values(sheet);
                if (values.Unsupported is { } why) return Result(CheckStatus.Unsupported, why);
                foreach (var (key, wanted) in after.Values)
                {
                    if (!values.Value!.TryGetValue(key, out var actual) || Math.Abs(actual - wanted) > 1e-9) problems.Add($"after, {key}: {(values.Value.ContainsKey(key) ? Show(actual) : "no value")}, expected {Show(wanted)}");
                }
            }

            if (after.Conditions != null)
            {
                var found = system.Conditions(sheet);
                if (found.Unsupported is { } why) return Result(CheckStatus.Unsupported, why);
                if (!after.Conditions.Order(StringComparer.Ordinal).SequenceEqual(found.Value!.Order(StringComparer.Ordinal)))
                {
                    problems.Add($"after, conditions: [{string.Join(", ", found.Value!)}], expected [{string.Join(", ", after.Conditions)}]");
                }
            }

            if (after.Campaign != null) return Result(CheckStatus.Unsupported, "there are no clocks or campaign tables (step 7)");
        }

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

    /// <summary>Numbers by value (a long and a double of the same number are the same), the rest by their YAML shape.</summary>
    private static bool Same(object? actual, object? wanted) =>
        YamlValues.ToNumber(actual) is { } a && YamlValues.ToNumber(wanted) is { } w ? Math.Abs(a - w) < 1e-9 : Describe(actual) == Describe(wanted);

    private static string Describe(object? value) => value switch
    {
        null => "nothing",
        IDictionary<string, object> map => "{" + string.Join(", ", map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}: {Describe(p.Value)}")) + "}",
        System.Collections.IEnumerable list and not string => "[" + string.Join(", ", list.Cast<object?>().Select(Describe)) + "]",
        double or long or int => Show(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        _ => value.ToString() ?? ""
    };
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

        public string? CannotRoll => null;
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

        public CheckOr<RollCheck> Roll(Dictionary<string, object> sheet, string? sheetId, SpecRoll roll, IReadOnlyDictionary<string, Dictionary<string, object>> participants)
        {
            var dice = new SequenceDiceSource(roll.Dice);
            var joining = participants.Select(p => (p.Key, p.Value, Source: new SequenceDiceSource(roll.Participants![p.Key]))).ToList();
            var request = new SheetRollRequest
            {
                Definition = roll.Definition,
                Inputs = YamlValues.NormalizeMap(roll.Inputs).ToDictionary(i => i.Key, i => (object?)i.Value, StringComparer.Ordinal),
                Participants = joining.Select(p => new RollParticipant(p.Key, p.Value, p.Source)).ToList()
            };

            SheetRollResult result;
            try
            {
                result = SheetRolls.Roll(definition, this, sheet, request, dice);
            }
            catch (Exception ex) when (ex is RollException or InvalidOperationException)
            {
                return CheckOr<RollCheck>.Of(new RollCheck($"(the roll failed: {ex.Message})", Array.Empty<double>(), Array.Empty<int>(), dice.Used, new Dictionary<string, object?>(), sheet));
            }

            // Only what the roll asks of the roller's own sheet; the others' sheets are not the expectation's.
            var after = new Dictionary<string, object>(sheet);
            SheetIntents.Apply(definition, this, after, result.AllIntents().Where(i => i.Actor == null || i.Actor == sheetId));
            return CheckOr<RollCheck>.Of(new RollCheck(result.Outcome, result.Totals().ToList(), result.Kept, dice.Used + joining.Sum(p => p.Source.Used), result.Carries, after));
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
