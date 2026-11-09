using System.Text;
using Codex.Core;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Codex.Tests.Reference;

/// <summary>
/// The three reference systems of overhaul section 2 (tests/Codex.Tests/Fixtures/reference) are the spec the overhaul is built
/// against. progress.txt names the expectations that pass; these tests keep it honest in both directions: a named one that
/// stops passing is a regression, and one that starts passing without being named means the ledger (and the overhaul doc's
/// account of where things stand) is out of date.
/// </summary>
public class ReferenceFixtureTests(ITestOutputHelper output)
{
    private static readonly IReferenceHost Host = new CurrentEngineHost();

    private static readonly Lazy<IReadOnlyList<CheckResult>> Results = new(() => ReferenceRunner.RunAll(Host));

    [Fact]
    public void ThereAreThreeReferenceFixtures_OneForEachConstraintSystem()
    {
        Assert.Equal(new[] { "crew", "d35", "street" }, ReferenceRunner.FixtureRoots().Select(Path.GetFileName));
    }

    [Fact]
    public async Task EachFixtureLoadsToday_WithEveryKeyRead()
    {
        foreach (var root in ReferenceRunner.FixtureRoots())
        {
            var registry = new ContentRegistry();
            var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(registry));
            await loader.LoadAndInitializeAsync(root);
            var spec = ReferenceRunner.SpecOf(root);

            Assert.Equal(new[] { spec.System }, loader.LoadedSystemIds);
            Assert.Empty(loader.FailedSystems);
            // The only problems are the handlers that are scripts (step 8): the packs load, every entry is where its type says,
            // and the rolls, events, tracks, layouts and campaign objects are well formed. Since step 7 every key is read.
            var unknown = new List<string>();
            SystemDefinitionLoader.Load(Path.Combine(root, SystemDefinitionLoader.FileName), unknown);
            Assert.Empty(unknown);
            Assert.Empty(loader.ProblemsFor(spec.System).Where(p => !p.Message.Contains("is a script, which this version does not run")));
            Assert.NotEmpty(registry.GetPacks(spec.System));

            output.WriteLine($"{Path.GetFileName(root)} ({spec.System}): not read yet: {string.Join(", ", unknown)}");
        }
    }

    [Fact]
    public void EverySpecIsWellFormed()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in ReferenceRunner.FixtureRoots())
        {
            var spec = ReferenceRunner.SpecOf(root); // refuses keys the format does not have
            var prefix = Path.GetFileName(root) + ".";
            Assert.NotEmpty(spec.Expectations);
            foreach (var e in spec.Expectations)
            {
                var where = $"{spec.Fixture}: {e.Id}";
                Assert.True(ids.Add(e.Id), $"{where}: the id is used twice");
                Assert.StartsWith(prefix, e.Id);
                Assert.InRange(e.Step, 2, 10);
                Assert.False(e.Expect.IsEmpty, $"{where}: expects nothing");
                Assert.False(e.Roll != null && e.Event != null, $"{where}: a roll and an event; one action per expectation");
                if (e.Sheet != null) Assert.True(spec.Sheets.ContainsKey(e.Sheet), $"{where}: no sheet '{e.Sheet}'");
                else Assert.True(e.With?.Campaign != null || e.Event != null, $"{where}: names no sheet and is not about the campaign");
                if (e.Roll != null) Assert.True(e.Roll.Dice.Count > 0 || e.Roll.Participants is { Count: > 0 }, $"{where}: a roll with no dice");
                if (e.Expect.After != null) Assert.True(e.Roll != null || e.Event != null, $"{where}: 'after' with nothing before it");
                if (e.Expect.Intents != null) Assert.True(e.Roll != null || e.Event != null, $"{where}: intents with no roll or event");
                if (e.Expect.IsOnlyNegative) Assert.True(e.Requires is { Count: > 0 }, $"{where}: says only what must not happen; name what it counts alongside (requires)");
            }
        }

        var all = ReferenceRunner.FixtureRoots().SelectMany(r => ReferenceRunner.SpecOf(r).Expectations).ToList();
        foreach (var e in all)
        {
            foreach (var required in e.Requires ?? new List<string>()) Assert.True(ids.Contains(required) && required != e.Id, $"{e.Id}: requires unknown '{required}'");
        }
    }

    [Fact]
    public void TheLedgerNamesOnlyExpectationsThatExist()
    {
        var known = Results.Value.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(ReferenceRunner.Ledger().Where(id => !known.Contains(id)));
    }

    [Fact]
    public void EveryExpectationInTheLedgerPasses()
    {
        var ledger = ReferenceRunner.Ledger();
        var regressions = Results.Value.Where(r => ledger.Contains(r.Id) && r.Status != CheckStatus.Pass).Select(r => $"{r.Id}: {r.Detail}").ToList();
        Assert.True(regressions.Count == 0, "Listed in progress.txt but no longer passing:\n" + string.Join("\n", regressions));
    }

    [Fact]
    public void NoExpectationPassesWithoutBeingInTheLedger()
    {
        var ledger = ReferenceRunner.Ledger();
        var unlisted = Results.Value.Where(r => r.Status == CheckStatus.Pass && !ledger.Contains(r.Id)).Select(r => r.Id).ToList();
        Assert.True(unlisted.Count == 0, "Passing now; add to tests/Codex.Tests/Fixtures/reference/progress.txt (and the overhaul doc's account):\n" + string.Join("\n", unlisted));
    }

    /// <summary>Where the overhaul stands against its spec, step by step. Always passes; read its output.</summary>
    [Fact]
    public void Report()
    {
        var builder = new StringBuilder();
        builder.AppendLine("fixture  step  pass  fail  unsupported");
        foreach (var group in Results.Value.GroupBy(r => (r.Spec.Fixture, r.Expectation.Step)).OrderBy(g => g.Key.Fixture).ThenBy(g => g.Key.Step))
        {
            builder.AppendLine($"{group.Key.Fixture,-8} {group.Key.Step,4}  {group.Count(r => r.Status == CheckStatus.Pass),4}  {group.Count(r => r.Status == CheckStatus.Fail),4}  {group.Count(r => r.Status == CheckStatus.Unsupported),11}");
        }

        builder.AppendLine($"total: {Results.Value.Count(r => r.Status == CheckStatus.Pass)} of {Results.Value.Count} pass");
        foreach (var result in Results.Value.Where(r => r.Status == CheckStatus.Fail)) builder.AppendLine($"  fail  {result.Id}: {result.Detail}");
        output.WriteLine(builder.ToString());
    }
}
