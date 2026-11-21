using System.Text;
using Codex.Core;
using Codex.Core.Consistency;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests.Consistency;

/// <summary>
/// The checker runs the sheet rules without the pickers' options (<see cref="SheetRuleEngine.Options"/> off). That must change nothing it reads: on
/// generated characters of the bundled systems, the engine's violations, warnings and the sheet it writes are the same with the options and without,
/// and <see cref="SheetRuleCheck"/> yields byte for byte what it did before (CheckOracles.cs). Where working the options out could throw, they are still
/// worked out, so a sheet that failed to evaluate before still does.
/// </summary>
public class SheetRuleDifferentialTests : IAsyncLifetime
{
    private readonly ContentRegistry registry = new();
    private readonly PluginLoader loader;

    public SheetRuleDifferentialTests() => loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(registry));

    public async Task InitializeAsync() => await loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class Pack(ContentRegistry registry, string systemId) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries(systemId, type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries(systemId, type);
    }

    private static string Text(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        IDictionary<string, object> map => "{" + string.Join(",", map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + Text(p.Value))) + "}",
        System.Collections.IEnumerable list => "[" + string.Join(",", list.Cast<object?>().Select(Text)) + "]",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static string Verdicts(SheetEvaluation e)
    {
        var sb = new StringBuilder();
        foreach (var v in e.Violations.Concat(new[] { new RuleViolation("--", "--") }).Concat(e.Warnings))
            sb.Append(v.FieldKey).Append('|').Append(v.Key).Append('|').Append(v.Message).Append('|').Append(Text(v.Args?.Select(a => a is ContentEntry c ? c.Id : a).ToList())).Append('\n');
        return sb.ToString();
    }

    /// <summary>A character of <paramref name="system"/> made at random from what its Library offers, with some ids that are not there.</summary>
    private static Dictionary<string, object> RandomSheet(Random r, SystemDefinition system, IEntryResolver library)
    {
        var sheet = new Dictionary<string, object>();
        new SheetRuleEngine(system, library).InitializeSheet(sheet);
        object Pick(string? type) =>
            type == null || r.Next(8) == 0 ? "no-such-entry" : library.Candidates(type).ToList() is { Count: > 0 } all ? all[r.Next(all.Count)].Id : "none";
        if (r.Next(3) > 0) sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var a in system.Attributes) sheet[SheetRuleEngine.BasePrefix + a.Key] = (long)r.Next(3, 21);
        foreach (var f in system.ActorFields)
        {
            if (r.Next(4) == 0) continue;
            sheet[f.Key] = f.Type switch
            {
                FieldType.EntityReference => Pick(f.TargetEntityType),
                FieldType.Number => (long)r.Next(0, 21),
                _ => sheet.GetValueOrDefault(f.Key) ?? "x"
            };
        }

        foreach (var list in system.SheetRules?.Lists ?? new List<ListRule>())
            sheet[list.Key] = Enumerable.Range(0, r.Next(0, 6)).Select(_ => (object)Pick(list.TargetEntityType)).ToList();
        if (system.SheetRules?.Inventory is { } inventory)
            sheet[inventory.Key] = Enumerable.Range(0, r.Next(0, 5)).Select(_ => (object)(Pick(inventory.Types.FirstOrDefault()) + (r.Next(3) == 0 ? "|equipped:1" : ""))).ToList();
        return sheet;
    }

    public static IEnumerable<object[]> Systems() => new[] { "srd5e", "four-degrees", "fitd-srd" }.SelectMany(s => new[] { 1, 2, 3, 4, 5 }.Select(seed => new object[] { s, seed }));

    [Theory]
    [MemberData(nameof(Systems))]
    public void TheEngineWithoutOptions_GivesTheSameVerdictsAndSheet(string systemId, int seed)
    {
        if (loader.GetDefinition(systemId) is not { } system) return;
        var library = new Pack(registry, systemId);
        var r = new Random(seed);
        var withOptions = new SheetRuleEngine(system, library);
        var without = new SheetRuleEngine(system, library) { Options = false };
        for (var k = 0; k < 40; k++)
        {
            var sheet = RandomSheet(r, system, library);
            var (a, b) = (new Dictionary<string, object>(sheet), new Dictionary<string, object>(sheet));
            string Run(SheetRuleEngine engine, Dictionary<string, object> s)
            {
                try { return Verdicts(engine.Recompute(s)) + Text(s); }
                catch (Exception e) { return "threw " + e.GetType().Name; }
            }

            Assert.Equal(Run(withOptions, a), Run(without, b));
        }
    }

    [Theory]
    [MemberData(nameof(Systems))]
    public void TheSheetRuleCheck_FindsWhatItFoundBefore_OnGeneratedCharacters(string systemId, int seed)
    {
        if (loader.GetDefinition(systemId) is not { } system) return;
        var library = new Pack(registry, systemId);
        var r = new Random(seed * 31);
        var actors = Enumerable.Range(0, 30).Select(i => Fx.Actor($"actors/{i}-A", "Actor " + i, (ActorRole)r.Next(3), retired: r.Next(10) == 0, sheet: RandomSheet(r, system, library))).ToList();
        var snapshot = Fx.Snap(actors: actors, system: system, library: library, enforcement: r.Next(2) == 0 ? "strict" : "warn");
        var expected = new OracleSheetRuleCheck().Run(snapshot).Select(Fx.Flat).ToList();
        Assert.Equal(expected, new SheetRuleCheck().Run(snapshot).Select(Fx.Flat).ToList());
        if (system.SheetRules != null) Assert.NotEmpty(expected);
    }

    [Fact]
    public void WhereTheOptionsWouldThrow_TheyAreStillWorkedOut_SoTheSameSheetStillFailsToEvaluate()
    {
        // A condition with no property name (a malformed system file) throws only while the options are tested: no class is chosen, so no verdict reads it.
        var system = Fx.System();
        system.SheetRules!.Constraints[0].Where[0].Property = null!;
        var library = new Fx.Entries(Fx.Entry("class", "fighter", "Fighter"));
        var snapshot = Fx.Snap(actors: new[] { Fx.Actor("actors/1-A", sheet: new Dictionary<string, object>()) }, system: system, library: library);
        var expected = new OracleSheetRuleCheck().Run(snapshot).Select(Fx.Flat).ToList();
        Assert.Contains(expected, i => i.Contains("evaluation-failed", StringComparison.Ordinal));
        Assert.Equal(expected, new SheetRuleCheck().Run(snapshot).Select(Fx.Flat).ToList());

        // A candidate without properties (outside the type's contract, but a resolver could hand one out) likewise.
        var bare = new ContentEntry { Type = "class", Id = "odd", Name = "Odd", Properties = null!, Tags = null! };
        var odd = Fx.Snap(actors: new[] { Fx.Actor("actors/1-A", sheet: new Dictionary<string, object>()) }, system: Fx.System(), library: new Fx.Entries(bare));
        Assert.Equal(new OracleSheetRuleCheck().Run(odd).Select(Fx.Flat).ToList(), new SheetRuleCheck().Run(odd).Select(Fx.Flat).ToList());
    }
}
