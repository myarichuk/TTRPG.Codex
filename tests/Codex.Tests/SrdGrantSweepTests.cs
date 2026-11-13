using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>What the generated SRD pack must pass (docs/SRD_GRANTS_IMPORT_DESIGN.md, section 10): every entry's grants fit the system's
/// vocabulary, and every class at every level with each subclass, and every species with each lineage, evaluates without a feature error, a
/// shared counter key, a spender with no pool or a stale choice.</summary>
public class SrdGrantSweepTests : IAsyncLifetime
{
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "srd-5.2.1" };
    private static readonly string[] Unwanted =
    {
        "Actor.Rule.FeatureError", "Actor.Rule.CounterKeyShared", "Actor.Rule.SpendNoPool", SheetRuleEngine.ChoiceStaleKey
    };

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;

    public SrdGrantSweepTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition("srd5e")!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class PackResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries("srd5e", type, Packs).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type, Packs);
    }

    private IEnumerable<ContentEntry> Entries(string type) => _registry.GetEntries("srd5e", type, Packs);

    [Fact]
    public void EveryEntrysGrants_PassTheGrantCheck()
    {
        var problems = new[] { "class", "subclass", "species", "feat" }
            .SelectMany(type => Entries(type))
            .SelectMany(entry => FeatureGrantCheck.Problems(_system, entry, requireId: true).Select(p => $"{entry.Type}/{entry.Id}: {p}"))
            .ToList();

        Assert.Empty(problems);
    }

    private List<string> Unwelcome(SheetEvaluation evaluation) =>
        evaluation.Warnings.Concat(evaluation.Violations).Where(v => v.Key != null && Unwanted.Contains(v.Key)).Select(v => $"{v.Key}: {v.Message}").ToList();

    private Dictionary<string, object> Sheet(string cls, int level)
    {
        var sheet = new Dictionary<string, object>();
        new SheetRuleEngine(_system, new PackResolver(_registry)).InitializeSheet(sheet);
        sheet["class"] = cls;
        sheet["level"] = (long)level;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + key] = 14L;
        return sheet;
    }

    [Fact]
    public void EveryClassAtEveryLevel_WithItsSubclasses_EvaluatesCleanly()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        var problems = new List<string>();
        foreach (var cls in Entries("class"))
        {
            var subclasses = Entries("subclass").Where(s => s.Properties.GetValueOrDefault("class")?.ToString() == cls.Id).Select(s => s.Id).Prepend(null).ToList();
            foreach (var subclass in subclasses)
            {
                for (var level = 1; level <= 20; level++)
                {
                    var sheet = Sheet(cls.Id, level);
                    if (subclass != null) sheet["subclass"] = subclass;
                    problems.AddRange(Unwelcome(engine.Recompute(sheet)).Select(p => $"{cls.Id} {level} {subclass}: {p}"));
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void EverySpeciesWithEachLineage_EvaluatesCleanly_AtEveryLevel()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        var problems = new List<string>();
        foreach (var species in Entries("species"))
        {
            var lineages = (species.Properties.GetValueOrDefault("lineages") as IEnumerable<object> ?? Enumerable.Empty<object>())
                .OfType<IDictionary<string, object>>().Select(r => r["name"].ToString()!).Prepend(null).ToList();
            foreach (var lineage in lineages)
            {
                for (var level = 1; level <= 20; level += 2)
                {
                    var sheet = Sheet("fighter", level);
                    sheet["species"] = species.Id;
                    if (lineage != null) sheet[$"choice__{species.Id}__lineage"] = new List<object> { lineage };
                    problems.AddRange(Unwelcome(engine.Recompute(sheet)).Select(p => $"{species.Id} {lineage} {level}: {p}"));
                }
            }
        }

        Assert.Empty(problems);
    }
}
