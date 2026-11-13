using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>A sample of what the SRD pack's mapped features do on a sheet (docs/SRD_GRANTS_IMPORT_DESIGN.md, section 13), run against the pack in the
/// repository: counters and their refills, Extra Attack and the critical range, species level gates, shared pools, Expertise.</summary>
public class SrdMappedGrantsTests : IAsyncLifetime
{
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "srd-5.2.1" };

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;

    public SrdMappedGrantsTests()
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

    private SheetRuleEngine Engine => new(_system, new PackResolver(_registry));

    /// <summary>A sheet with the first class at <paramref name="level"/>, and what else a test names.</summary>
    private Dictionary<string, object> Sheet(string cls, int level, string? subclass = null, string? species = null, string? lineage = null, params string[] otherClasses)
    {
        var sheet = new Dictionary<string, object>();
        Engine.InitializeSheet(sheet);
        sheet["class"] = cls;
        sheet["level"] = (long)(level + otherClasses.Sum(o => int.Parse(o.Split(':')[1])));
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + key] = 14L;
        if (subclass != null) sheet["subclass"] = subclass;
        if (species != null) sheet["species"] = species;
        if (lineage != null && species != null) sheet[$"choice__{species}__lineage"] = new List<object> { lineage };
        if (otherClasses.Length > 0) sheet["multiclass"] = otherClasses.Cast<object>().ToList();
        return sheet;
    }

    private SheetEvaluation Run(Dictionary<string, object> sheet) => Engine.Recompute(sheet);

    private static FeatureCounter Counter(SheetEvaluation evaluation, string key) => evaluation.Counters.Single(c => c.Key == "uses_" + key);

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 3)]
    [InlineData(6, 4)]
    [InlineData(12, 5)]
    [InlineData(17, 6)]
    public void ABarbariansRageMaximum_FollowsTheClassTable(int level, int rages)
    {
        var sheet = Sheet("barbarian", level);
        Assert.Equal(rages, Counter(Run(sheet), "rage").Max);
        Assert.Equal((long)rages, sheet["uses_rage"]); // a new counter starts full
    }

    [Fact]
    public void RageComesBackOnePerShortRestAndAllOnALongRest()
    {
        var sheet = Sheet("barbarian", 3);
        Run(sheet);
        sheet["uses_rage"] = 0L;

        var shortRest = FeatureRecovery.Apply(FeatureRecovery.For(sheet, "uses_rage", "Short Rest"), 0, 3, _ => 0);
        Assert.Equal(1, shortRest.Value);

        var longRest = FeatureRecovery.Apply(FeatureRecovery.For(sheet, "uses_rage", "Long Rest"), 0, 3, _ => 0);
        Assert.Equal(3, longRest.Value);
    }

    [Fact]
    public void BardicInspiration_ComesBackOnAShortRestOnlyFromFontOfInspiration()
    {
        var four = Sheet("bard", 4);
        Run(four);
        Assert.Empty(FeatureRecovery.For(four, "uses_bardic_inspiration", "Short Rest"));
        Assert.NotEmpty(FeatureRecovery.For(four, "uses_bardic_inspiration", "Long Rest"));

        var five = Sheet("bard", 5);
        Run(five);
        Assert.NotEmpty(FeatureRecovery.For(five, "uses_bardic_inspiration", "Short Rest"));
    }

    [Theory]
    [InlineData(5, 2)]
    [InlineData(11, 3)]
    [InlineData(20, 4)]
    public void ExtraAttack_GivesTheFightersAttacks(int level, int attacks)
    {
        var evaluation = Run(Sheet("fighter", level));
        Assert.Equal((double)attacks, Convert.ToDouble(evaluation.Derived["attacks"]));
    }

    [Fact]
    public void ExtraAttack_DoesNotStackAcrossClasses()
    {
        var evaluation = Run(Sheet("fighter", 5, otherClasses: "barbarian:5"));
        Assert.Equal(2d, Convert.ToDouble(evaluation.Derived["attacks"])); // both rows give 2; the best counts, they never add
    }

    [Theory]
    [InlineData(2, 20)]
    [InlineData(3, 19)]
    [InlineData(14, 19)]
    [InlineData(15, 18)]
    public void AChampionsCriticalRange_WidensWithTheClassFeatures(int level, int critFrom)
    {
        var evaluation = Run(Sheet("fighter", level, "champion"));
        Assert.Equal((double)critFrom, Convert.ToDouble(evaluation.Derived["critFrom"]));
    }

    [Fact]
    public void ACriticalRangeIsStillTwentyWithoutAChampion()
    {
        Assert.Equal(20d, Convert.ToDouble(Run(Sheet("fighter", 15, "battle-master")).Derived["critFrom"]));
    }

    [Fact]
    public void AlertAddsTheProficiencyBonusToInitiative_WhenItIsTheOriginFeat()
    {
        var plain = Sheet("fighter", 5);
        var withAlert = Sheet("fighter", 5);
        withAlert["originFeat"] = "alert";

        var difference = Convert.ToDouble(Run(withAlert).Derived["initiative"]) - Convert.ToDouble(Run(plain).Derived["initiative"]);

        Assert.Equal(3d, difference); // the Proficiency Bonus at level 5
    }

    [Fact]
    public void DraconicResilience_AddsHitPointsPerLevel_AndAnUnarmoredArmorClass()
    {
        var evoker = Run(Sheet("sorcerer", 5, "evoker"));
        var dragon = Run(Sheet("sorcerer", 5, "draconic-sorcery"));

        Assert.Equal(evoker.HitPoints!.Max + 5, dragon.HitPoints!.Max);
        Assert.Equal(10 + 2 + 2, dragon.ArmorClass!.Total); // 10 + Dexterity + Charisma, both 14
    }

    [Fact]
    public void AWoodElfWalksAtThirtyFive_AndADrowSeesAtOneHundredTwenty()
    {
        var wood = Sheet("fighter", 1, species: "elf", lineage: "Elven Lineage: Wood Elf");
        Run(wood);
        Assert.Equal(35L, Convert.ToInt64(wood["speed"]));

        var drow = Sheet("fighter", 1, species: "elf", lineage: "Elven Lineage: Drow");
        Run(drow);
        Assert.Equal(120L, Convert.ToInt64(drow["darkvision"]));
    }

    [Fact]
    public void ASpeciesCounterWaitsForItsLevel_DraconicFlightComesAtFive()
    {
        var four = Run(Sheet("fighter", 4, species: "dragonborn", lineage: "Draconic Ancestor: Black"));
        Assert.DoesNotContain(four.Counters, c => c.Key == "uses_draconic_flight");

        var five = Run(Sheet("fighter", 5, species: "dragonborn", lineage: "Draconic Ancestor: Black"));
        Assert.Contains(five.Counters, c => c.Key == "uses_draconic_flight");
    }
}
