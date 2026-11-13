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

    private static FeatureCounter Counter(SheetEvaluation evaluation, string key) => evaluation.Counters.Single(c => c.Key == "uses_" + key.Replace('-', '_'));

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

    [Fact]
    public void BreathWeapon_IsOneSharedPool_ChangingAncestryKeepsWhatWasSpentAndMovesTheResistance()
    {
        var sheet = Sheet("fighter", 5, species: "dragonborn", lineage: "Draconic Ancestor: Black");
        var black = Run(sheet);
        var breath = Counter(black, "breath-weapon");
        Assert.Equal("Breath Weapon", breath.Label); // the pool's name, not the row's ("Draconic Ancestor: Black")
        Assert.Equal(3, breath.Max); // the Proficiency Bonus at level 5
        Assert.Equal(new[] { "acid" }, SheetRuleEngine.RawList(sheet, "resistances"));

        sheet["uses_breath_weapon"] = 1L; // two spent
        Engine.ChangeChoice(sheet, "choice__dragonborn__lineage", new[] { "Draconic Ancestor: Red" });

        Assert.Equal(1L, sheet["uses_breath_weapon"]);
        Assert.Equal(new[] { "fire" }, SheetRuleEngine.RawList(sheet, "resistances"));
    }

    [Fact]
    public void ADrowGetsFaerieFireAtThree_AndDarknessAtFive_AndTheCantripAlways()
    {
        var two = Sheet("fighter", 2, species: "elf", lineage: "Elven Lineage: Drow");
        Run(two);
        Assert.Equal(new[] { "dancing-lights" }, SheetRuleEngine.RawList(two, "grantedSpells"));

        var three = Sheet("fighter", 3, species: "elf", lineage: "Elven Lineage: Drow");
        Run(three);
        Assert.Equal(new[] { "dancing-lights", "faerie-fire" }, SheetRuleEngine.RawList(three, "grantedSpells"));

        var five = Sheet("fighter", 5, species: "elf", lineage: "Elven Lineage: Drow");
        Run(five);
        Assert.Contains("darkness", SheetRuleEngine.RawList(five, "grantedSpells"));
    }

    [Fact]
    public void ADruidOfTheLand_GetsTheLandsSpellsByLevel_AndItsResistanceFromTen()
    {
        var sheet = Sheet("druid", 5, "circle-of-the-land");
        sheet["choice__circle-of-the-land__land"] = new List<object> { "Arid" };
        Run(sheet);
        Assert.Equal(new[] { "blur", "burning-hands", "fire-bolt", "fireball" }, SheetRuleEngine.RawList(sheet, "grantedSpells").Except(new[] { "speak-with-animals" }).ToArray());
        Assert.Empty(SheetRuleEngine.RawList(sheet, "resistances"));

        sheet["level"] = 10L;
        Run(sheet);
        Assert.Equal(new[] { "fire" }, SheetRuleEngine.RawList(sheet, "resistances"));
    }

    [Fact]
    public void ADwarfIsResistantToPoison_AndATieflingKnowsItsLineagesCantrip()
    {
        var dwarf = Sheet("fighter", 1, species: "dwarf");
        Run(dwarf);
        Assert.Equal(new[] { "poison" }, SheetRuleEngine.RawList(dwarf, "resistances"));

        var tiefling = Sheet("fighter", 1, species: "tiefling", lineage: "Fiendish Legacy: Infernal");
        Run(tiefling);
        Assert.Equal(new[] { "thaumaturgy", "fire-bolt" }, SheetRuleEngine.RawList(tiefling, "grantedSpells").OrderBy(s => s == "fire-bolt").ToArray());
        Assert.Equal(new[] { "fire" }, SheetRuleEngine.RawList(tiefling, "resistances"));
    }

    [Fact]
    public void ElementalAffinity_AsksForADamageType_AndGivesItsResistance()
    {
        var sheet = Sheet("sorcerer", 6, "draconic-sorcery");
        var pending = Run(sheet);
        Assert.Contains(pending.Warnings, w => w.Key == SheetRuleEngine.ChoiceRowPendingKey);

        sheet["choice__draconic-sorcery__elemental-affinity"] = new List<object> { "Fire" };
        Run(sheet);
        Assert.Equal(new[] { "fire" }, SheetRuleEngine.RawList(sheet, "resistances"));
    }

    [Fact]
    public void FiendishResilience_OffersEveryTypeButForce_AndMayChangeAtAnyRest()
    {
        var sheet = Sheet("warlock", 10, "fiend-patron");
        var evaluation = Run(sheet);
        var choice = evaluation.PendingChoices.Single(c => c.Key == "choice__fiend-patron__fiendish-resilience");
        Assert.Equal(12, choice.Options.Count);
        Assert.DoesNotContain(choice.Options, o => o.Key.Equals("Force", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "Short Rest", "Long Rest" }, choice.Row!.ChangesOn);
    }

    [Fact]
    public void ARogueWhoTickedExpertiseBeforeTheChoiceExisted_IsOfferedToConfirmIt()
    {
        var sheet = Sheet("rogue", 1);
        sheet["skillProficiencies"] = new List<object> { "stealth", "perception", "acrobatics", "deception" };
        sheet["skillExpertise"] = new List<object> { "stealth", "acrobatics" };

        var evaluation = Run(sheet);

        var expertise = evaluation.PendingChoices.Single(c => c.Key == "choice__rogue__expertise-1");
        Assert.True(expertise.Adopted); // nothing stored yet; the ticks cover the choice
        Assert.Equal(new[] { "stealth", "acrobatics" }.Order(), expertise.Made.Order());
        Assert.Contains(evaluation.Warnings, w => w.Key == SheetRuleEngine.ChoiceConfirmKey);
        Assert.DoesNotContain(evaluation.Warnings, w => w.Key == SheetRuleEngine.ChoicePendingKey);

        // Confirming writes the picks; the expertise holds the same, and the notice goes.
        SheetRuleEngine.SetChoice(sheet, expertise.Key, expertise.Made);
        var confirmed = Run(sheet);
        Assert.DoesNotContain(confirmed.PendingChoices, c => c.Key == expertise.Key);
        Assert.True(confirmed.Skills.Single(s => s.Key == "stealth").Expert);
    }

    [Fact]
    public void ARogueWithNoExpertiseTicks_IsAskedToChoose_AndTheSkillIsNotShownAsFromExpertiseUntilThen()
    {
        var sheet = Sheet("rogue", 1);
        sheet["skillProficiencies"] = new List<object> { "stealth", "perception", "acrobatics", "deception" };

        var evaluation = Run(sheet);

        Assert.Contains(evaluation.Warnings, w => w.Key == SheetRuleEngine.ChoicePendingKey);
        var stealth = evaluation.Skills.Single(s => s.Key == "stealth");
        Assert.False(stealth.Expert);
        Assert.Empty(stealth.Sources!.Where(s => s.Kind == RankSourceKinds.Choice));
    }

    [Fact]
    public void ASkillThePlayerTickedThemselves_IsNotShownAsFromExpertiseAfterAnAdoptedChoice()
    {
        var sheet = Sheet("rogue", 1);
        sheet["skillProficiencies"] = new List<object> { "stealth", "perception", "acrobatics", "deception" };
        sheet["skillExpertise"] = new List<object> { "stealth", "acrobatics" };
        SheetRuleEngine.SetChoice(sheet, "choice__rogue__expertise-1", new[] { "stealth", "acrobatics" });

        var evaluation = Run(sheet);

        // Proficiency is the player's own tick: only the second rank comes from Expertise.
        var perception = evaluation.Skills.Single(s => s.Key == "perception");
        Assert.Empty(perception.Sources!.Where(s => s.Kind == RankSourceKinds.Choice));
        var stealth = evaluation.Skills.Single(s => s.Key == "stealth");
        Assert.Equal(new[] { RankSourceKinds.Choice }, stealth.Sources!.Select(s => s.Kind).ToArray());
        Assert.Equal("Rogue: Expertise", stealth.Sources!.Single().Name);
    }

    [Fact]
    public void ADisciplinedSurvivorIsProficientInEverySave_AndASlipperyMindInWisdomAndCharisma()
    {
        var monk = Run(Sheet("monk", 14));
        Assert.All(monk.SavingThrows, s => Assert.True(s.Proficient, s.Key));

        var rogue = Run(Sheet("rogue", 15));
        Assert.True(rogue.SavingThrows.Single(s => s.Key == "wis").Proficient);
        Assert.True(rogue.SavingThrows.Single(s => s.Key == "cha").Proficient);
        Assert.True(rogue.SavingThrows.Single(s => s.Key == "dex").Proficient); // the class's own
        Assert.False(rogue.SavingThrows.Single(s => s.Key == "str").Proficient);
    }
}
