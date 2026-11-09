using System.Globalization;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.SystemCheck;

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

    /// <summary>The warnings the host gives the GM for the sheet (what could not be worked out, and why), as (field, key).</summary>
    CheckOr<IReadOnlyList<(string Field, string Key)>> Warnings(Dictionary<string, object> sheet);

    CheckOr<IReadOnlyList<string>> Conditions(Dictionary<string, object> sheet);

    /// <summary>Makes a roll on the sheet with the spec's faces, and applies what it asks of the roller's sheet. For a group
    /// roll, <paramref name="participants"/> are the sheets joining it, by spec sheet id.</summary>
    CheckOr<RollCheck> Roll(Dictionary<string, object> sheet, string? sheetId, SpecRoll roll, IReadOnlyDictionary<string, Dictionary<string, object>> participants);

    /// <summary>Raises the spec's event on the sheet and applies what it asks of that sheet and of the campaign's objects
    /// (<paramref name="campaign"/>, changed in place).</summary>
    /// <param name="dice">The faces a handler's dice (<c>ctx.d</c>, a compiled handler's <c>D</c>) are handed, in order; null rolls at random.</param>
    CheckOr<EventCheck> Raise(Dictionary<string, object> sheet, string? sheetId, IReadOnlyDictionary<string, object> ev, Dictionary<string, object> campaign, IReadOnlyList<int>? dice = null);

    /// <summary>The problems the host reports for the campaign's objects, as (field, key).</summary>
    CheckOr<IReadOnlyList<(string Field, string Key)>> CampaignViolations(Dictionary<string, object> campaign);

    /// <summary>The sheet kind's declared layout, as its section names; empty when the generic one is used.</summary>
    CheckOr<IReadOnlyList<string>> Layout(Dictionary<string, object> sheet);

    string? CannotRoll { get; }
    string? CannotRaiseEvents { get; }
    string? CannotLayOut { get; }
    string? HasNoCampaignObjects { get; }
}

/// <summary>What a roll came to, as a spec reads it: <see cref="After"/> is the roller's sheet once the roll's intents were applied.</summary>
public sealed record RollCheck(string? Outcome, IReadOnlyList<double> Totals, IReadOnlyList<int> Kept, int DiceUsed,
    IReadOnlyDictionary<string, object?> Carries, Dictionary<string, object> After);

/// <summary>What an event came to, as a spec reads it: the intents, each <c>{ kind: { actor?, fields } }</c>, and the sheet once
/// those for it were applied.</summary>
public sealed record EventCheck(IReadOnlyList<Dictionary<string, object?>> Intents, Dictionary<string, object> After, string? Failed = null);

/// <summary>A result, or why the host cannot produce one.</summary>
public readonly record struct CheckOr<T>(T? Value, string? Unsupported)
{
    public static CheckOr<T> Of(T value) => new(value, null);
    public static CheckOr<T> No(string why) => new(default, why);
}

/// <summary>Runs a system's <c>spec.yaml</c> (tests/Codex.Tests/Fixtures/reference/README.md has the format) against a host:
/// the reference fixtures in the tests, and any system's own spec through <c>Codex.SystemCheck</c>.</summary>
public static class ReferenceRunner
{
    public const string SpecFile = "spec.yaml";

    public static ReferenceSpec SpecOf(string fixtureRoot) => ReferenceSpec.Load(Path.Combine(fixtureRoot, SpecFile));

    /// <summary>Every expectation of each folder's spec (a folder holding a system.yaml and a spec.yaml).</summary>
    public static IReadOnlyList<CheckResult> RunAll(IReferenceHost host, IEnumerable<string> roots)
    {
        var results = new List<CheckResult>();
        foreach (var root in roots)
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
        var sheet = SpecSheets.Compose(spec, expectation);
        var campaign = SpecSheets.Campaign(spec, expectation);
        var problems = new List<string>();

        if (expect.Layout is { } layout)
        {
            var laidOut = system.Layout(sheet);
            if (laidOut.Unsupported is { } why) return Result(CheckStatus.Unsupported, why);
            if (!laidOut.Value!.SequenceEqual(layout, StringComparer.Ordinal))
            {
                problems.Add($"layout: [{string.Join(", ", laidOut.Value!)}], expected [{string.Join(", ", layout)}]");
            }
        }

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

        if (expectation.Event is { } ev)
        {
            var raised = system.Raise(sheet, expectation.Sheet, ev, campaign, expectation.Dice);
            if (raised.Unsupported is { } why) return Result(CheckStatus.Unsupported, why);
            if (raised.Value!.Failed is { } failed) return Result(CheckStatus.Fail, $"the event failed: {failed}");
            if (expect.Intents is { } wanted)
            {
                var actual = Describe(raised.Value!.Intents);
                var expected = Describe(wanted.Select(YamlValues.Normalize).ToList());
                if (actual != expected) problems.Add($"intents: {actual}, expected {expected}");
            }

            sheet = raised.Value!.After;
        }
        else if (expect.Intents != null)
        {
            return Result(CheckStatus.Unsupported, "intents are checked for events; extend ReferenceRunner.Check for a roll's");
        }

        if (expect.After is { } after)
        {
            if (expectation.Roll == null && expectation.Event == null) return Result(CheckStatus.Fail, "after needs a roll or an event");
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

            // Each named object is found by name in the campaign's list, and only the fields named are compared.
            foreach (var (key, wanted) in YamlValues.NormalizeMap(after.Campaign))
            {
                var actual = (YamlValues.Normalize(campaign.GetValueOrDefault(key)) as System.Collections.IEnumerable)?.Cast<object?>().OfType<IDictionary<string, object>>().ToList()
                    ?? new List<IDictionary<string, object>>();
                foreach (var entry in (wanted as System.Collections.IEnumerable)?.Cast<object?>().OfType<IDictionary<string, object>>() ?? Enumerable.Empty<IDictionary<string, object>>())
                {
                    var name = entry.TryGetValue("name", out var n) ? n?.ToString() : null;
                    var found = actual.FirstOrDefault(a => string.Equals(a.TryGetValue("name", out var m) ? m?.ToString() : null, name, StringComparison.Ordinal));
                    if (found == null)
                    {
                        problems.Add($"after, campaign {key}: no {name}");
                        continue;
                    }

                    foreach (var (field, value) in entry.Where(f => f.Key != "name"))
                    {
                        var has = found.TryGetValue(field, out var got) ? got : null;
                        if (!Same(has, value)) problems.Add($"after, campaign {key} {name} {field}: {Describe(has)}, expected {Describe(value)}");
                    }
                }
            }
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
            // The sheet's problems when the expectation is about a sheet, and the campaign's always: the spec's own campaign
            // breaks no rule, so only what the expectation lays over it can.
            var found = expectation.Sheet != null ? system.Violations(sheet) : CheckOr<IReadOnlyList<(string Field, string Key)>>.Of(Array.Empty<(string, string)>());
            if (found.Unsupported is { } why) return Result(CheckStatus.Unsupported, why);
            var onCampaign = system.CampaignViolations(campaign);
            if (onCampaign.Unsupported is { } whyNot) return Result(CheckStatus.Unsupported, whyNot);
            var wanted = expect.Violations.Select(v => (v.Field, v.Key)).OrderBy(v => v).ToList();
            var actual = found.Value!.Concat(onCampaign.Value!).OrderBy(v => v).ToList();
            if (!wanted.SequenceEqual(actual))
            {
                problems.Add($"violations: [{string.Join("; ", actual.Select(v => $"{v.Field} {v.Key}"))}], expected [{string.Join("; ", wanted.Select(v => $"{v.Field} {v.Key}"))}]");
            }
        }

        if (expect.Warnings != null)
        {
            var found = system.Warnings(sheet);
            if (found.Unsupported is { } why) return Result(CheckStatus.Unsupported, why);
            var wanted = expect.Warnings.Select(v => (v.Field, v.Key)).OrderBy(v => v).ToList();
            var actual = found.Value!.OrderBy(v => v).ToList();
            if (!wanted.SequenceEqual(actual))
            {
                problems.Add($"warnings: [{string.Join("; ", actual.Select(v => $"{v.Field} {v.Key}"))}], expected [{string.Join("; ", wanted.Select(v => $"{v.Field} {v.Key}"))}]");
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
/// number it leaves on the sheet or derives, and <see cref="CampaignObjects"/> for the campaign's.
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
        public string? CannotRaiseEvents => null;
        public string? CannotLayOut => null;

        public CheckOr<IReadOnlyList<string>> Layout(Dictionary<string, object> sheet) =>
            CheckOr<IReadOnlyList<string>>.Of(SheetLayout.Declared(definition, sheet).Select(s => s.Section).ToList());
        public string? HasNoCampaignObjects => null;

        public CheckOr<IReadOnlyList<(string Field, string Key)>> CampaignViolations(Dictionary<string, object> campaign) =>
            CheckOr<IReadOnlyList<(string, string)>>.Of(CampaignObjects.Problems(definition, campaign).Select(v => (v.FieldKey, v.Key ?? "")).ToList());

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

        public CheckOr<IReadOnlyList<(string Field, string Key)>> Warnings(Dictionary<string, object> sheet)
        {
            var (_, evaluation) = Evaluate(sheet);
            return CheckOr<IReadOnlyList<(string, string)>>.Of(evaluation.Warnings.Select(v => (v.FieldKey, v.Key ?? "")).ToList());
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
            SheetIntents.Apply(definition, this, after, result.AllIntents(), sheetId);
            return CheckOr<RollCheck>.Of(new RollCheck(result.Outcome, result.Totals().ToList(), result.Kept, dice.Used + joining.Sum(p => p.Source.Used), result.Carries, after));
        }

        public CheckOr<EventCheck> Raise(Dictionary<string, object> sheet, string? sheetId, IReadOnlyDictionary<string, object> ev, Dictionary<string, object> campaign, IReadOnlyList<int>? dice = null)
        {
            var fields = YamlValues.NormalizeMap(ev.Where(f => f.Key != "type").ToDictionary(f => f.Key, f => f.Value))
                .ToDictionary(f => f.Key, f => (object?)f.Value, StringComparer.Ordinal);
            var after = new Dictionary<string, object>(sheet);
            try
            {
                var result = SheetEvents.Raise(definition, this, after, new SheetEvent(ev.GetValueOrDefault("type")?.ToString() ?? "", fields), sheetId, campaign,
                    dice == null ? null : new SequenceDiceSource(dice));
                return CheckOr<EventCheck>.Of(new EventCheck(result.Intents.Select(Shape).ToList(), after));
            }
            catch (SheetEventException ex)
            {
                return CheckOr<EventCheck>.Of(new EventCheck(Array.Empty<Dictionary<string, object?>>(), sheet, ex.Message));
            }
        }

        /// <summary><c>{ kind: { actor?, fields } }</c>, as a spec writes an intent.</summary>
        private static Dictionary<string, object?> Shape(SheetIntent intent)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (intent.Actor != null) fields["actor"] = intent.Actor;
            foreach (var (key, value) in intent.Fields) fields[key] = value;
            return new Dictionary<string, object?> { [intent.Kind] = fields };
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
