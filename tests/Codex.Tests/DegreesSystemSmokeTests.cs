using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// Loads the real <c>content/</c> folder and checks the Four Degrees system (step P7 of the hybrid-system plan) and its homebrew pack: the
/// system loads with no warning, its name avoids any publisher's trademark, the ORC licence block is there (with the notice text still a
/// marked placeholder), the pack has the counts its author wrote and every entry is tagged homebrew, and a character builds to level 3.
/// </summary>
public class DegreesSystemSmokeTests : IAsyncLifetime
{
    private const string PackId = "degrees-homebrew-core";
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { PackId };

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;

    public DegreesSystemSmokeTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition("degrees")!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class PackResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries("degrees", type, Packs).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("degrees", type, Packs);
    }

    private IEnumerable<ContentEntry> Entries(string type) => _registry.GetEntries("degrees", type, Packs);

    [Fact]
    public void LoadsWithoutWarnings()
    {
        Assert.Null(_loader.LoadException);
        Assert.Empty(_loader.LoadWarnings);
        Assert.Contains("degrees", _loader.LoadedSystemIds);
        Assert.NotNull(_system);
    }

    [Fact]
    public void TheNameAndTheContentUseNoPublishersWords()
    {
        Assert.Equal("Four Degrees", _system.Name);
        var words = new[] { "Pathfinder", "Paizo", "Golarion" };
        var pack = _registry.GetPack(PackId)!;
        var everything = Entries("ancestry").Concat(Entries("background")).Concat(Entries("class")).Concat(Entries("weapon")).Concat(Entries("armor")).Concat(Entries("creature"))
            .SelectMany(e => new[] { e.Id, e.Name, e.Description ?? "" }).Concat(new[] { _system.Name, _system.Description ?? "", pack.Name });
        foreach (var word in words) Assert.DoesNotContain(everything, text => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheManifestsCarryTheOrcLicenceBlock_WithTheNoticeStillAPlaceholder()
    {
        foreach (var license in new[] { _system.License, _registry.GetPack(PackId)!.License })
        {
            Assert.NotNull(license);
            Assert.Equal("ORC", license!.Name);
            Assert.Equal("https://paizo.com/orclicense", license.Url);
            // The ORC Notice is quoted from the licence's own required notice, which the repository does not hold yet. Until it does, the
            // field says so; this assertion goes when the real text is pasted in.
            Assert.Contains("PLACEHOLDER", license.Attribution);
        }
    }

    [Theory]
    [InlineData("ancestry", 2)]
    [InlineData("background", 2)]
    [InlineData("class", 2)]
    [InlineData("weapon", 4)]
    [InlineData("armor", 3)]
    [InlineData("creature", 3)]
    public void ThePackHasItsCounts_AndEveryEntryIsTaggedHomebrew(string type, int expected)
    {
        var entries = Entries(type).ToList();
        Assert.Equal(expected, entries.Count);
        Assert.All(entries, e => Assert.Contains("homebrew", e.Tags));
    }

    [Fact]
    public void TheSystemDeclaresItsMomentsAndTheFaithShape()
    {
        Assert.Equal(new[] { "dailyPrep", "refocus", "sessionStart" }, _system.Moments.Select(m => m.Key).ToArray());
        Assert.Contains(_system.ActorFields, f => f.Key == "faith" && f.TargetEntityType == "faith");
        Assert.Contains(_system.ActorFields, f => f.Key == "faithEdicts");
        Assert.Contains(_system.ActorFields, f => f.Key == "faithTaboos");
    }

    [Fact]
    public void EveryCreatureHasItsPrintedNumbers_AndAtLeastOneStrike()
    {
        foreach (var creature in Entries("creature"))
        {
            foreach (var key in new[] { "armorClass", "hitPoints", "saveCon", "saveDex", "saveWis", "perceptionBonus", "classDcValue" })
                Assert.True(creature.Properties.ContainsKey(key), $"{creature.Id} has no {key}");
            Assert.NotEmpty((IEnumerable<object>)creature.Properties["strikes"]);
        }
    }

    private Dictionary<string, object> Character(string ancestry, string background, string cls, int level)
    {
        var sheet = new Dictionary<string, object>();
        new SheetRuleEngine(_system, new PackResolver(_registry)).InitializeSheet(sheet);
        sheet["ancestry"] = ancestry;
        sheet["background"] = background;
        sheet["class"] = cls;
        sheet["level"] = (long)level;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + key] = 14L;
        return sheet;
    }

    [Theory]
    [InlineData("marshkin", "ferry-hand", "warden")]
    [InlineData("cliffborn", "lamp-keeper", "lorekeeper")]
    public void ACharacterBuildsToLevel3_WithNoFeatureError(string ancestry, string background, string cls)
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        for (var level = 1; level <= 3; level++)
        {
            var evaluation = engine.Recompute(Character(ancestry, background, cls, level));
            var problems = evaluation.Warnings.Concat(evaluation.Violations).Where(v => v.Key != null && v.Key.StartsWith("Actor.Rule.Feature")).Select(v => $"{v.Key}: {v.Message}").ToList();
            Assert.Empty(problems);
        }
    }

    [Fact]
    public void ALevel3Warden_HasItsRanksFeaturesAndHitPoints()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        var evaluation = engine.Recompute(Character("marshkin", "ferry-hand", "warden", 3));

        // Trained in Perception: level 3 + 2, plus Wisdom 14 (+2).
        Assert.Equal(7, Convert.ToInt32(evaluation.Derived["perception"]));
        // Class DC: 10 + trained (5) + the key attribute, Strength 14 (+2).
        Assert.Equal(17, Convert.ToInt32(evaluation.Derived["classDc"]));
        // Hit points: the ancestry's 6 once, then 9 per level plus Constitution (+2): 6 + 3 * (9 + 2).
        Assert.Equal(39, evaluation.HitPoints!.Max);
        var features = evaluation.Features.Select(f => f.Name).ToList();
        Assert.Contains("Lucky break", features);
        Assert.Contains("Plant your feet", features);
        Assert.Contains("Hardy", features);
    }

    [Fact]
    public void ALevel1Lorekeeper_DoesNotYetHaveLevel3Features()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        var evaluation = engine.Recompute(Character("cliffborn", "lamp-keeper", "lorekeeper", 1));

        var features = evaluation.Features.Select(f => f.Name).ToList();
        Assert.Contains("Prepared trick", features);
        Assert.DoesNotContain("Keen eye", features);
        Assert.DoesNotContain("Inner reserve", features);
    }

    [Fact]
    public void APrintedCreature_ReadsItsNumbers_AndAConditionLowersThem()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        var hound = Entries("creature").Single(e => e.Id == "mire-hound");
        var sheet = new Dictionary<string, object>(hound.Properties) { [SheetRuleEngine.MethodKey] = SheetRuleEngine.PrintedMethod };

        var plain = engine.Printed(sheet);
        Assert.Equal(15, plain.Single(n => n.Target == "armorClass").Total);
        Assert.Equal(7, plain.Single(n => n.Target == "perception").Total);
        Assert.Equal(13, plain.Single(n => n.Target == "classDc").Total);
        Assert.Equal(7, plain.Single(n => n.Label == "Bite").Total);

        // Shaken 1 lowers Perception, the DC and the Strike by 1, not armor class. Wobbly 1 lowers armor class.
        sheet["condition_shaken"] = 1L;
        sheet["condition_wobbly"] = 1L;
        var lowered = engine.Printed(sheet);
        Assert.Equal(14, lowered.Single(n => n.Target == "armorClass").Total);
        Assert.Equal(6, lowered.Single(n => n.Target == "perception").Total);
        Assert.Equal(12, lowered.Single(n => n.Target == "classDc").Total);
        Assert.Equal(6, lowered.Single(n => n.Label == "Bite").Total); // two status penalties of 1 keep only the worst: -1, not -2
    }
}
