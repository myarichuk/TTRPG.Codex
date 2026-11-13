using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>The system's <c>modifierTypes</c> on a feature's modifiers (sheetRules.features) on the real SRD system: a value rule
/// (<c>max</c>, <c>min</c>) keeps one amount that competes with the number's start, a bonus rule (<c>highest</c>) keeps one and
/// adds it; and the action's <c>count</c> and <c>critical</c> read the derived values those modifiers raise.</summary>
public class StackingTypesTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public StackingTypesTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, object> Modifier(string target, long amount, string? type)
    {
        var modifier = new Dictionary<string, object> { ["target"] = target, ["amount"] = amount };
        if (type != null) modifier["type"] = type;
        return modifier;
    }

    private static ContentEntry Subclass(params Dictionary<string, object>[] modifiers)
    {
        var entry = new ContentEntry { Type = "subclass", Id = "stacker", Name = "Stacker", SystemId = "srd5e", PackId = "homebrew" };
        entry.Properties["class"] = "sorcerer";
        entry.Properties["features"] = modifiers.Select((m, i) => (object)new Dictionary<string, object>
        {
            ["name"] = "Feature " + (i + 1), ["level"] = 3L, ["description"] = "Feature.", ["modifiers"] = new List<object> { m }
        }).ToList();
        return entry;
    }

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet, SystemDefinition System) Character(params Dictionary<string, object>[] modifiers)
    {
        var system = _loader.GetDefinition("srd5e")!;
        var engine = new SheetRuleEngine(system, new WithHomebrew(_registry, Subclass(modifiers)));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "sorcerer";
        sheet["subclass"] = "stacker";
        sheet["species"] = "human";
        sheet["level"] = 5L;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        sheet[SheetRuleEngine.BasePrefix + "cha"] = 16L;
        sheet[SheetRuleEngine.BasePrefix + "dex"] = 12L;
        sheet["inventory"] = new List<object> { "longsword|equipped:1" };
        return (engine, sheet, system);
    }

    [Fact]
    public void ExtraAttack2Then3Then4_IsFour_AndOnlyTheFourCounts()
    {
        var (engine, sheet, _) = Character(Modifier("attacks", 2, "highest"), Modifier("attacks", 3, "highest"), Modifier("attacks", 4, "highest"));

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(4L, evaluation.Derived["attacks"]); // from a start of 1, replaced, not 1 + 4
        Assert.Equal(new[] { 4 }, evaluation.Granted.Where(g => g.Target == "attacks" && g.Counted).Select(g => g.Amount));
        Assert.Equal(3, evaluation.Granted.Count(g => g.Target == "attacks"));
    }

    [Fact]
    public void ImprovedCritical19WithSuperiorCritical18_IsEighteen()
    {
        var (engine, sheet, _) = Character(Modifier("critFrom", 19, "lowest"), Modifier("critFrom", 18, "lowest"));

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(18L, evaluation.Derived["critFrom"]);
        Assert.Equal(new[] { 18 }, evaluation.Granted.Where(g => g.Target == "critFrom" && g.Counted).Select(g => g.Amount));
    }

    [Fact]
    public void AValueThatDoesNotBeatTheStart_DoesNotCount()
    {
        // A "lowest" (min) of 21 on a start of 20 is no ceiling; a "highest" (max) of 1 on a start of 1 is no floor.
        var (engine, sheet, _) = Character(Modifier("critFrom", 21, "lowest"), Modifier("attacks", 1, "highest"));

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(20L, evaluation.Derived["critFrom"]);
        Assert.Equal(1L, evaluation.Derived["attacks"]);
        Assert.DoesNotContain(evaluation.Granted, g => g.Counted);
    }

    [Fact]
    public void AHighestBonusType_AddsItsBestBonus_ToAStartThatIsNotZero()
    {
        // Pathfinder's status bonus: +1 and +2 status keep the +2, which adds to the +1 untyped and to the start.
        var (engine, sheet, system) = Character(Modifier("proficiencyBonus", 1, "status"), Modifier("proficiencyBonus", 2, "status"), Modifier("proficiencyBonus", 1, null));
        system.ModifierTypes.Types["status"] = "highest";

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(3 + 2 + 1, (int)(long)evaluation.Derived["proficiencyBonus"]); // level 5: +3
        Assert.Equal(new[] { false, true, true }, evaluation.Granted.Where(g => g.Target == "proficiencyBonus").Select(g => g.Counted));
    }

    [Fact]
    public void WithoutAStackingTable_EveryModifierAdds_AsBefore()
    {
        var (engine, sheet, system) = Character(Modifier("attacks", 2, "highest"), Modifier("attacks", 3, "highest"), Modifier("critFrom", -1, "lowest"), Modifier("attacks", 1, "base"));
        system.ModifierTypes.Types.Clear();

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(1L + 2 + 3 + 1, evaluation.Derived["attacks"]);
        Assert.Equal(19L, evaluation.Derived["critFrom"]);
        Assert.All(evaluation.Granted, g => Assert.True(g.Counted));
    }

    [Fact]
    public void AFloorOnAField_AndACeilingOnTheSameTarget_AreWorkedOutInTheSameHelper()
    {
        // darkvision is a Number field; a max type raises it to the best amount, as the Shadowed Blood test expects.
        var (engine, sheet, _) = Character(Modifier("darkvision", 60, "highest"), Modifier("darkvision", 120, "highest"));

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(120L, sheet["darkvision"]);
        Assert.Equal(new[] { 120 }, evaluation.Granted.Where(g => g.Target == "darkvision" && g.Counted).Select(g => g.Amount));
    }

    [Fact]
    public void TheAttackAction_CarriesItsCountAndCritical_FromTheValuesTheFormulasName()
    {
        var (engine, sheet, _) = Character(Modifier("attacks", 2, "highest"), Modifier("critFrom", 19, "lowest"));

        var sword = engine.Recompute(sheet).Actions.Single(a => a.Entry.Id == "longsword");

        Assert.Equal(2, sword.Count);
        Assert.Equal(19, sword.Critical);
    }

    [Fact]
    public void TheAttackAction_ReadsOneAttackAndATwentyByDefault()
    {
        var (engine, sheet, system) = Character(Modifier("initiative", 0, null));

        var sword = engine.Recompute(sheet).Actions.Single(a => a.Entry.Id == "longsword");

        Assert.Equal(1, sword.Count);
        Assert.Equal(20, sword.Critical);
        Assert.Equal(20, system.Dice.CheckTopFace());
        Assert.Equal(8, new DiceConventions { Check = "2d8" }.CheckTopFace());
        Assert.Null(new DiceConventions { Check = "flat" }.CheckTopFace());
    }

    [Fact]
    public void TheGuideAndTheVocabulary_NameTheSystemsStackingTypes()
    {
        var system = _loader.GetDefinition("srd5e")!;
        var vocabulary = FeatureGrantVocabulary.For(system)!;

        Assert.Equal(new[] { "base", "highest" }, vocabulary.FloorTypes);
        Assert.Equal(new[] { "lowest" }, vocabulary.CeilingTypes);
        Assert.Equal("min", vocabulary.StackingRules["lowest"]);
        var guide = FeatureGrantGuide.For(system, "subclass")!;
        Assert.Contains("\"type\": \"lowest\" (only the least amount of that type counts", guide);
        Assert.Contains("A type not listed adds", guide);
    }

    private sealed class WithHomebrew(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }
}
