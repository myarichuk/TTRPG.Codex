using System.Globalization;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// An imported class (the generated <c>rules-core</c> pack, chapter 8a of the book import) on a Four Degrees character sheet: a fighter and a rogue with the thief racket, built from an imported ancestry, background and class. Every
/// number the sheet is expected to show is worked out from the property values the pack file holds (read from the loaded entries in the test) and the ranks the book's initial proficiencies give, never from the importer. Rank 1
/// is trained, 2 expert, 3 master, 4 legendary (the system's order). A level 7 build checks the proficiency increases the class features grant.
/// </summary>
public class FourDegreesClassTests : IAsyncLifetime
{
    private const string SystemId = "four-degrees";
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "four-degrees-core", "four-degrees-rules" };

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;

    public FourDegreesClassTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition(SystemId)!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class PackResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries(SystemId, type, Packs).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries(SystemId, type, Packs);
    }

    private SheetRuleEngine Engine() => new(_system, new PackResolver(_registry));

    private ContentEntry Entry(string type, string id) => _registry.GetEntries(SystemId, type, Packs).Single(e => e.Id == id);

    private static long Number(ContentEntry entry, string key) => Convert.ToInt64(entry.Properties[key], CultureInfo.InvariantCulture);

    private static List<string> Problems(SheetEvaluation evaluation) =>
        evaluation.Violations.Select(v => $"violation {v.Key}: {v.Message}").Concat(evaluation.Warnings.Select(v => $"warning {v.Key}: {v.Message}")).ToList();

    private Dictionary<string, object> Character(string cls, string? subclass, int level, string classBoost)
    {
        var sheet = new Dictionary<string, object>();
        Engine().InitializeSheet(sheet);
        sheet["ancestry"] = "dwarf-ancestry";
        sheet["background"] = "acolyte";
        sheet["class"] = cls;
        if (subclass != null) sheet["subclass"] = subclass;
        sheet["level"] = (long)level;
        sheet[SheetRuleEngine.MethodKey] = "boosts";
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet.Remove(SheetRuleEngine.BasePrefix + key);

        // The boosts of every source, each to a different modifier within it: the ancestry's one free boost, the background's two (the one of two it names and a free one), the class's (when it is a choice), and the four free
        // boosts that finish the 1st-level build.
        sheet["boost__ancestry__dwarf-ancestry.b1"] = new List<object> { "dex" };
        sheet["boost__background__acolyte.b1"] = new List<object> { "int" };
        sheet["boost__background__acolyte.b2"] = new List<object> { "con" };
        if (classBoost.Length > 0) sheet[$"boost__class__{cls}.b1"] = new List<object> { classBoost };
        foreach (var (slot, key) in new[] { ("m1", "str"), ("m2", "dex"), ("m3", "wis"), ("m4", "cha") }) sheet[$"boost__lvl1__{slot}"] = new List<object> { key };
        return sheet;
    }

    private static int Rank(SheetEvaluation evaluation, string list, string key) => evaluation.Ranked[list].Single(s => s.Key == key).Rank;

    private static int Save(SheetEvaluation evaluation, string attribute) => evaluation.SavingThrows.Single(s => s.Key == attribute).Rank;

    [Fact]
    public void ALevel1Fighter_HasTheClassesHitPoints_Boost_AndInitialProficiencies()
    {
        var fighter = Entry("class", "fighter-class");
        var dwarf = Entry("ancestry", "dwarf-ancestry");
        Assert.Equal("str|dex", (string)fighter.Properties["keyAttribute"]);

        var sheet = Character("fighter-class", null, 1, "str");
        var evaluation = Engine().Recompute(sheet);

        Assert.Empty(Problems(evaluation));
        // The class's boost goes to the attribute chosen of its two; Strength also takes the free one of the level, so +2 (the ancestry's and background's boosts are to others).
        Assert.Equal(2L, sheet["str"]);
        // Hit points: the class's number a level, plus the Constitution modifier (the background's free boost and the ancestry's fixed one: +2), plus the ancestry's number once.
        var expected = Number(fighter, "hp") + 2 + Number(dwarf, "hp");
        Assert.Equal(expected, Convert.ToInt64(sheet["HP_Max"], CultureInfo.InvariantCulture));

        // Perception expert; the saves: Fortitude and Reflex expert, Will trained.
        Assert.Equal(2, Rank(evaluation, "perception", "perception"));
        Assert.Equal(new[] { 2, 2, 1 }, new[] { Save(evaluation, "con"), Save(evaluation, "dex"), Save(evaluation, "wis") });
        // Attacks: simple, martial and unarmed expert, advanced trained. Defenses: all armor and unarmored trained.
        Assert.Equal(new[] { 2, 2, 1, 2 }, new[] { "simple", "martial", "advanced", "unarmed" }.Select(k => Rank(evaluation, "weapons", k)));
        Assert.Equal(new[] { 1, 1, 1, 1 }, new[] { "unarmored", "light", "medium", "heavy" }.Select(k => Rank(evaluation, "armor", k)));
        Assert.Equal(1, Rank(evaluation, "classDc", "classDc"));
    }

    [Fact]
    public void TheClassDcOfAClassWithAChoiceOfKeyAttribute_TakesTheHigherModifier()
    {
        // Two builds that differ in the class's one boost. With the boost to Strength: Strength +2, Dexterity +2 (the ancestry's and a free boost). With it to Dexterity: Strength +1 (a free boost), Dexterity +3.
        // The class DC is 10 + level + 2 + the higher modifier: +2 in the first build and +3 in the second. Taking the first of the two attributes would give Strength's +2 and +1, so only the second build tells "higher" from "first".
        var strong = Character("fighter-class", null, 1, "str");
        var strongEvaluation = Engine().Recompute(strong);
        var dexterous = Character("fighter-class", null, 1, "dex");
        var dexterousEvaluation = Engine().Recompute(dexterous);

        Assert.Empty(Problems(strongEvaluation));
        Assert.Empty(Problems(dexterousEvaluation));
        var strongMods = new[] { Convert.ToInt64(strong["str"], CultureInfo.InvariantCulture), Convert.ToInt64(strong["dex"], CultureInfo.InvariantCulture) };
        var dexterousMods = new[] { Convert.ToInt64(dexterous["str"], CultureInfo.InvariantCulture), Convert.ToInt64(dexterous["dex"], CultureInfo.InvariantCulture) };
        Assert.Equal(10 + 1 + 2 + strongMods.Max(), strongEvaluation.Ranked["classDc"].Single().Bonus);
        Assert.Equal(10 + 1 + 2 + dexterousMods.Max(), dexterousEvaluation.Ranked["classDc"].Single().Bonus);
        Assert.NotEqual(strongMods, dexterousMods);
    }

    [Fact]
    public void ALevel1RogueWithTheThiefRacket_HasTheInitialProficiencies_AndTheRacketsSkill()
    {
        var rogue = Entry("class", "rogue-class");
        var thief = Entry("subclass", "thief");
        Assert.Equal("rogue-class", (string)thief.Properties["class"]);

        var sheet = Character("rogue-class", "thief", 1, string.Empty);
        var evaluation = Engine().Recompute(sheet);

        Assert.Empty(Problems(evaluation));
        Assert.DoesNotContain(evaluation.AllowedOptions["subclass"], e => e.Properties.TryGetValue("class", out var c) && (string)c != "rogue-class");
        // The class's boost is to Dexterity: the ancestry's free boost and a free one make +3.
        Assert.Equal(3L, sheet["dex"]);
        Assert.Equal(Number(rogue, "hp") + 2 + Number(Entry("ancestry", "dwarf-ancestry"), "hp"), Convert.ToInt64(sheet["HP_Max"], CultureInfo.InvariantCulture));

        // Perception expert; Fortitude trained, Reflex and Will expert; light armor and unarmored trained, the rest untrained; simple, martial and unarmed weapons trained, advanced untrained.
        Assert.Equal(2, Rank(evaluation, "perception", "perception"));
        Assert.Equal(new[] { 1, 2, 2 }, new[] { Save(evaluation, "con"), Save(evaluation, "dex"), Save(evaluation, "wis") });
        Assert.Equal(new[] { 1, 1, 0, 0 }, new[] { "unarmored", "light", "medium", "heavy" }.Select(k => Rank(evaluation, "armor", k)));
        Assert.Equal(new[] { 1, 1, 0, 1 }, new[] { "simple", "martial", "advanced", "unarmed" }.Select(k => Rank(evaluation, "weapons", k)));
        Assert.Equal(1, Rank(evaluation, "classDc", "classDc"));
        // Stealth from the class, Thievery from the racket.
        Assert.Equal(1, evaluation.Skills.Single(s => s.Key == "stealth").Rank);
        Assert.Equal(1, evaluation.Skills.Single(s => s.Key == "thievery").Rank);
        Assert.Equal(0, evaluation.Skills.Single(s => s.Key == "athletics").Rank);

        // A racket of another class is a warning (the choice is limited to the chosen class's); for the rogue it is the four rackets.
        Assert.Equal(new[] { "mastermind", "ruffian", "scoundrel", "thief" }, evaluation.AllowedOptions["subclass"].Select(e => e.Id).Order());
        var wrong = Engine().Recompute(Character("fighter-class", "thief", 1, "str"));
        Assert.Contains(Problems(wrong), p => p.Contains("belongs to another class", StringComparison.Ordinal));
    }

    [Fact]
    public void ARuffian_IsTrainedInMediumArmor_AndGainsItsLaterRanksWithTheClassesLightArmor()
    {
        var at1 = Engine().Recompute(Character("rogue-class", "ruffian", 1, string.Empty));
        var at13 = Engine().Recompute(Character("rogue-class", "ruffian", 13, string.Empty));
        var at19 = Engine().Recompute(Character("rogue-class", "ruffian", 19, string.Empty));

        Assert.Equal(new[] { 1, 1 }, new[] { Rank(at1, "armor", "light"), Rank(at1, "armor", "medium") });
        Assert.Equal(new[] { 2, 2 }, new[] { Rank(at13, "armor", "light"), Rank(at13, "armor", "medium") });
        Assert.Equal(new[] { 3, 3 }, new[] { Rank(at19, "armor", "light"), Rank(at19, "armor", "medium") });
        Assert.Equal(1, at1.Skills.Single(s => s.Key == "intimidation").Rank);
    }

    [Fact]
    public void ALevel7Fighter_HasTheProficiencyIncreasesOfItsClassFeatures()
    {
        // Battlefield Surveyor (7th): Perception master. Bravery (3rd): Will expert. Before level 9 Fortitude stays expert; Battle Hardened (9th) makes it master.
        var at7 = Engine().Recompute(Character("fighter-class", null, 7, "str"));
        var at9 = Engine().Recompute(Character("fighter-class", null, 9, "str"));
        var at3 = Engine().Recompute(Character("fighter-class", null, 3, "str"));

        Assert.Equal(3, Rank(at7, "perception", "perception"));
        Assert.Equal(2, Rank(at3, "perception", "perception"));
        Assert.Equal(new[] { 2, 2, 2 }, new[] { Save(at7, "con"), Save(at7, "dex"), Save(at7, "wis") });
        Assert.Equal(1, Save(Engine().Recompute(Character("fighter-class", null, 2, "str")), "wis"));
        Assert.Equal(3, Save(at9, "con"));
    }

    [Fact]
    public void ALevel7Rogue_HasTheProficiencyIncreasesOfItsClassFeatures()
    {
        // Evasive Reflexes and Perception Mastery (7th): Reflex and Perception master; Weapon Tricks (5th): the weapons expert.
        var at7 = Engine().Recompute(Character("rogue-class", "thief", 7, string.Empty));
        var at4 = Engine().Recompute(Character("rogue-class", "thief", 4, string.Empty));

        Assert.Equal(3, Save(at7, "dex"));
        Assert.Equal(2, Save(at4, "dex"));
        Assert.Equal(3, Rank(at7, "perception", "perception"));
        Assert.Equal(new[] { 2, 2, 2 }, new[] { "simple", "martial", "unarmed" }.Select(k => Rank(at7, "weapons", k)));
        Assert.Equal(new[] { 1, 1, 1 }, new[] { "simple", "martial", "unarmed" }.Select(k => Rank(at4, "weapons", k)));
    }

    // ---- The spellcasters (chapter 8b): the bard, the cleric and the druid ----

    private static double Bonus(SheetEvaluation evaluation, string list) => evaluation.Ranked[list].Single().Bonus;

    private static IReadOnlyDictionary<int, int> Slots(SheetEvaluation evaluation) => evaluation.SlotPools.Single(p => p.Rule.Key == "slots").Max;

    [Theory]
    [InlineData("bard-class", "enigma", "cha", "occult", "spontaneous", 2, 1, 1)]
    [InlineData("cleric-class", "cloistered-cleric", "wis", "divine", "prepared", 1, 1, 3)]
    [InlineData("druid-class", "leaf", "wis", "primal", "prepared", 1, 1, 3)]
    public void ALevel1Spellcaster_HasTheClassesNumbers_TheSpellRanks_AndTheCounts(string cls, string subclass, string key, string tradition, string casting, int perception, int fortitude, int modifier)
    {
        var entry = Entry("class", cls);
        Assert.Equal(key, (string)entry.Properties["keyAttribute"]);
        Assert.Equal(tradition, (string)entry.Properties["tradition"]);
        Assert.Equal(casting, (string)entry.Properties["casting"]);

        var sheet = Character(cls, subclass, 1, string.Empty);
        var evaluation = Engine().Recompute(sheet);

        Assert.Empty(Problems(evaluation));
        // The class's boost goes to its key attribute, which also takes the free boost of the level (+2). The dwarf's own boosts are Constitution and Wisdom (so Wisdom is +3), and its flaw is Charisma (so the bard's Charisma is +1).
        Assert.Equal((long)modifier, sheet[key]);
        Assert.Equal(Number(entry, "hp") + 2 + Number(Entry("ancestry", "dwarf-ancestry"), "hp"), Convert.ToInt64(sheet["HP_Max"], CultureInfo.InvariantCulture));
        // Perception (expert for the bard, trained for the other two), Fortitude and Reflex trained, Will expert.
        Assert.Equal(perception, Rank(evaluation, "perception", "perception"));
        Assert.Equal(new[] { fortitude, 1, 2 }, new[] { Save(evaluation, "con"), Save(evaluation, "dex"), Save(evaluation, "wis") });
        // The spell attack modifier and the spell DC share one rank (trained), on the key attribute: 10 + level + 2 + the modifier for the DC, level + 2 + the modifier for the attack.
        Assert.Equal(1, Rank(evaluation, "spellAttack", "spellAttack"));
        Assert.Equal(1, Rank(evaluation, "spellDc", "spellDc"));
        Assert.Equal(1d + 2 + modifier, Bonus(evaluation, "spellAttack"));
        Assert.Equal(10d + 1 + 2 + modifier, Bonus(evaluation, "spellDc"));
        Assert.Equal(1, Rank(evaluation, "classDc", "classDc"));
        // Five cantrips and two 1st-rank spells, and two slots of the 1st rank.
        Assert.Equal(5, evaluation.ListLimits["cantrips"]);
        Assert.Equal(2, evaluation.ListLimits["spells"]);
        Assert.Equal(new Dictionary<int, int> { [1] = 2 }, Slots(evaluation).Where(s => s.Value > 0).ToDictionary(s => s.Key, s => s.Value));
        Assert.All(evaluation.AllowedOptions["subclass"], o => Assert.Equal(cls, (string)o.Properties["class"]));
    }

    [Fact]
    public void ABardAtLevels5And7_HasTheLaterSlotCounts_AndTheLaterIncreases()
    {
        var at5 = Engine().Recompute(Character("bard-class", "maestro", 5, string.Empty));
        var at7 = Engine().Recompute(Character("bard-class", "maestro", 7, string.Empty));
        var at6 = Engine().Recompute(Character("bard-class", "maestro", 6, string.Empty));

        // The table: three 1st-rank and three 2nd-rank slots and two 3rd-rank at level 5, then 4th rank at level 7. A spell for each slot is in the repertoire (8 and 11).
        Assert.Equal(new Dictionary<int, int> { [1] = 3, [2] = 3, [3] = 2 }, Slots(at5).Where(s => s.Value > 0).ToDictionary(s => s.Key, s => s.Value));
        Assert.Equal(new Dictionary<int, int> { [1] = 3, [2] = 3, [3] = 3, [4] = 2 }, Slots(at7).Where(s => s.Value > 0).ToDictionary(s => s.Key, s => s.Value));
        Assert.Equal(new[] { 8, 11 }, new[] { at5.ListLimits["spells"]!.Value, at7.ListLimits["spells"]!.Value });
        Assert.Equal(new[] { 5, 5 }, new[] { at5.ListLimits["cantrips"]!.Value, at7.ListLimits["cantrips"]!.Value });
        // Reflex Expertise (3rd): Reflex expert. Expert Spellcaster (7th): the spell attack modifier and the spell DC expert (so the DC is 2 higher at the same level and attribute: 10 + 7 + 4 + 2).
        Assert.Equal(2, Save(at5, "dex"));
        Assert.Equal(1, Rank(at6, "spellDc", "spellDc"));
        Assert.Equal(new[] { 2, 2 }, new[] { Rank(at7, "spellAttack", "spellAttack"), Rank(at7, "spellDc", "spellDc") });
        // The bard's Charisma is +1 here (the dwarf's flaw), so the DC is 10 + 6 + 2 + 1 = 19 as a trained 6th-level character and 10 + 7 + 4 + 1 = 22 as an expert 7th-level one.
        Assert.Equal(new[] { 19d, 22d }, new[] { Bonus(at6, "spellDc"), Bonus(at7, "spellDc") });
    }

    [Fact]
    public void ADruidAtLevel7_HasExpertSpellcasting_AndTheEarlierExpertise()
    {
        var at2 = Engine().Recompute(Character("druid-class", "storm", 2, string.Empty));
        var at7 = Engine().Recompute(Character("druid-class", "storm", 7, string.Empty));

        // Perception and Fortitude expert (3rd), Reflex expert (5th), the spell ranks expert (7th); not before.
        Assert.Equal(new[] { 1, 1, 1 }, new[] { Rank(at2, "perception", "perception"), Rank(at2, "spellAttack", "spellAttack"), Save(at2, "con") });
        Assert.Equal(new[] { 2, 2, 2, 2, 2 }, new[] { Rank(at7, "perception", "perception"), Rank(at7, "spellDc", "spellDc"), Save(at7, "con"), Save(at7, "dex"), Rank(at7, "spellAttack", "spellAttack") });
        // The order's trained skill is a grant of the order.
        Assert.Equal(1, at2.Skills.Single(s => s.Key == "acrobatics").Rank);
        Assert.Equal(1, at2.Skills.Single(s => s.Key == "nature").Rank);
    }

    [Fact]
    public void AClericsDoctrine_GivesItsLaterRanks_AtItsOwnLevels()
    {
        var cloistered3 = Engine().Recompute(Character("cleric-class", "cloistered-cleric", 3, string.Empty));
        var cloistered2 = Engine().Recompute(Character("cleric-class", "cloistered-cleric", 2, string.Empty));
        var cloistered7 = Engine().Recompute(Character("cleric-class", "cloistered-cleric", 7, string.Empty));
        var warpriest1 = Engine().Recompute(Character("cleric-class", "warpriest", 1, string.Empty));
        var warpriest3 = Engine().Recompute(Character("cleric-class", "warpriest", 3, string.Empty));
        var warpriest7 = Engine().Recompute(Character("cleric-class", "warpriest", 7, string.Empty));
        var warpriest11 = Engine().Recompute(Character("cleric-class", "warpriest", 11, string.Empty));

        // Cloistered cleric: Fortitude expert from the 2nd doctrine (3rd level), the spell ranks expert from the 3rd doctrine (7th).
        Assert.Equal(new[] { 1, 2 }, new[] { Save(cloistered2, "con"), Save(cloistered3, "con") });
        Assert.Equal(new[] { 1, 2 }, new[] { Rank(cloistered3, "spellDc", "spellDc"), Rank(cloistered7, "spellDc", "spellDc") });
        // Warpriest: Fortitude expert and light and medium armor trained at the 1st level; martial weapons trained from the 2nd doctrine; the spell ranks expert only from the 4th doctrine (11th level).
        Assert.Equal(2, Save(warpriest1, "con"));
        Assert.Equal(new[] { 1, 1, 0 }, new[] { Rank(warpriest1, "armor", "light"), Rank(warpriest1, "armor", "medium"), Rank(warpriest1, "armor", "heavy") });
        Assert.Equal(new[] { 0, 1, 2 }, new[] { Rank(warpriest1, "weapons", "martial"), Rank(warpriest3, "weapons", "martial"), Rank(warpriest7, "weapons", "martial") });
        Assert.Equal(new[] { 1, 2 }, new[] { Rank(warpriest1, "weapons", "simple"), Rank(warpriest7, "weapons", "simple") }); // expert from the 3rd doctrine (7th level)
        Assert.Equal(new[] { 1, 2 }, new[] { Rank(warpriest7, "spellDc", "spellDc"), Rank(warpriest11, "spellDc", "spellDc") });
    }

    private static bool Says(ContentEntry spell, string tradition) =>
        spell.Properties.TryGetValue("traditions", out var value) && value is IEnumerable<object> list && list.Any(t => string.Equals(t?.ToString(), tradition, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void TheSpellLists_AreFilteredByTheClassesTradition_AndByTheHighestRankItHasASlotFor()
    {
        var spells = _registry.GetEntries(SystemId, "spell", Packs).ToList();
        var occult = spells.First(s => (string)s.Properties["kind"] == "spell" && Says(s, "occult") && Convert.ToInt32(s.Properties["rank"], CultureInfo.InvariantCulture) == 1);
        var divineOnly = spells.First(s => (string)s.Properties["kind"] == "spell" && Says(s, "divine") && !Says(s, "occult") && Convert.ToInt32(s.Properties["rank"], CultureInfo.InvariantCulture) == 1);
        var tooHigh = spells.First(s => (string)s.Properties["kind"] == "spell" && Says(s, "occult") && Convert.ToInt32(s.Properties["rank"], CultureInfo.InvariantCulture) == 3);
        var occultCantrip = spells.First(s => (string)s.Properties["kind"] == "cantrip" && Says(s, "occult"));
        var divineCantrip = spells.First(s => (string)s.Properties["kind"] == "cantrip" && Says(s, "divine") && !Says(s, "occult"));

        // What the lists offer (before anything is picked): only the bard's tradition, in the right kind, and (for the spells) no higher than the 1st rank at level 1.
        var offered = Engine().Recompute(Character("bard-class", "enigma", 1, string.Empty));
        Assert.Contains(offered.ListOptions["spells"], s => s.Id == occult.Id);
        Assert.DoesNotContain(offered.ListOptions["spells"], s => s.Id == divineOnly.Id || s.Id == tooHigh.Id || s.Id == occultCantrip.Id);
        Assert.All(offered.ListOptions["spells"], s => Assert.True(Says(s, "occult") && Convert.ToInt32(s.Properties["rank"], CultureInfo.InvariantCulture) <= 1 && (string)s.Properties["kind"] == "spell"));
        Assert.Contains(offered.ListOptions["cantrips"], s => s.Id == occultCantrip.Id);
        Assert.DoesNotContain(offered.ListOptions["cantrips"], s => s.Id == divineCantrip.Id || s.Id == occult.Id);

        var sheet = Character("bard-class", "enigma", 1, string.Empty);
        sheet["spells"] = new List<object> { occult.Id, divineOnly.Id, tooHigh.Id, occultCantrip.Id };
        sheet["cantrips"] = new List<object> { occultCantrip.Id, divineCantrip.Id, occult.Id };
        var evaluation = Engine().Recompute(sheet);

        // The picks that break them are flagged: a spell of another tradition, a spell above the highest rank, a cantrip among the spells; and in the cantrips a cantrip of another tradition and a spell.
        var problems = Problems(evaluation);
        Assert.Contains(problems, p => p.Contains($"Spells: {divineOnly.Name} - not on your class's spell list", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains($"Spells: {tooHigh.Name} - above the highest spell rank", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains($"Spells: {occultCantrip.Name} - cantrips go in the cantrips list", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains($"Cantrips: {divineCantrip.Name} - not on your class's spell list", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains($"Cantrips: {occult.Name} - that is not a cantrip", StringComparison.Ordinal));
        // The picks that are fine are not flagged.
        Assert.DoesNotContain(problems, p => p.Contains($"Spells: {occult.Name} -", StringComparison.Ordinal) || p.Contains($"Cantrips: {occultCantrip.Name} -", StringComparison.Ordinal));
    }

    private sealed class FaithResolver(IEntryResolver inner, ContentEntry faith) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => type == "faith" && id == faith.Id ? faith : inner.Resolve(type, id);
        public IEnumerable<ContentEntry> Candidates(string type) => type == "faith" ? new[] { faith } : inner.Candidates(type);
    }

    [Fact]
    public void ATablesOwnFaith_GivesTheClericItsTrainedSkill_AndShowsItsOtherFields()
    {
        // The pack holds no faith. A table's own, with a trained skill as a feature row, is read by the sheet like a class's rows; the font, the sanctification, the favored weapon and the domains are fields beside it.
        var faith = new ContentEntry
        {
            Id = "table-faith", Name = "Table Faith", Type = "faith", SystemId = SystemId, PackId = "table",
            Properties =
            {
                ["font"] = "heal",
                ["sanctification"] = "either",
                ["domains"] = new List<object> { "knowledge", "protection" },
                ["features"] = new List<object>
                {
                    new Dictionary<string, object> { ["name"] = "Faith skill", ["level"] = 1L, ["description"] = "The faith trains one skill.", ["adds"] = new List<object> { new Dictionary<string, object> { ["to"] = "skills", ["entries"] = new List<object> { "medicine" }, ["rank"] = "trained" } } },
                },
            },
        };
        var engine = new SheetRuleEngine(_system, new FaithResolver(new PackResolver(_registry), faith));
        var sheet = Character("cleric-class", "cloistered-cleric", 1, string.Empty);
        sheet["faith"] = "table-faith";
        var evaluation = engine.Recompute(sheet);

        Assert.Equal(1, evaluation.Skills.Single(s => s.Key == "medicine").Rank);
        Assert.Equal(1, evaluation.Skills.Single(s => s.Key == "religion").Rank);
        Assert.Equal(0, Engine().Recompute(Character("cleric-class", "cloistered-cleric", 1, string.Empty)).Skills.Single(s => s.Key == "medicine").Rank);
    }

    [Fact]
    public void AClassThatCastsNoSpells_StillHasNoSpellRank_AndOffersNoSpells()
    {
        var evaluation = Engine().Recompute(Character("fighter-class", null, 1, "str"));

        Assert.Empty(Problems(evaluation));
        Assert.Equal(0, Rank(evaluation, "spellAttack", "spellAttack"));
        Assert.Equal(0, Rank(evaluation, "spellDc", "spellDc"));
        Assert.Empty(evaluation.ListOptions["spells"]);
        Assert.Empty(evaluation.ListOptions["cantrips"]);
        Assert.Empty(evaluation.SlotPools.SelectMany(p => p.Max.Values).Where(v => v > 0));
    }

    [Fact]
    public void AStupefiedSpellcaster_TakesThePenaltyOnTheSpellAttackModifierAndTheSpellDc()
    {
        var sheet = Character("bard-class", "enigma", 1, string.Empty);
        var plain = Engine().Recompute(sheet);
        sheet["condition_stupefied"] = 2L;
        var stupefied = Engine().Recompute(sheet);

        // The published scores are the ones a condition reaches; the class DC is not on the text of Stupefied.
        double Published(SheetEvaluation e, string key) => Convert.ToDouble(e.Derived[key], CultureInfo.InvariantCulture);
        Assert.Equal(Published(plain, "spellAttack") - 2, Published(stupefied, "spellAttack"));
        Assert.Equal(Published(plain, "spellDc") - 2, Published(stupefied, "spellDc"));
        Assert.Equal(Published(plain, "classDc"), Published(stupefied, "classDc"));
    }

    [Fact]
    public void TheContainsPropertyOfFieldCondition_ReadsAPropertyOfTheChosenEntry_AndFailsWhileNothingIsChosen()
    {
        // The engine's one small change of chapter 8b: a list property of the candidate holds a property of the entry a sheet field names (a spell's traditions hold the tradition of the chosen class).
        var condition = new RuleCondition { Property = "traditions", ContainsPropertyOfField = new FieldProperty { Field = "class", Property = "tradition" } };
        var occult = new ContentEntry { Id = "a", Name = "A", Type = "spell", Properties = { ["traditions"] = new List<object> { "Arcane", "OCCULT" } } };
        var single = new ContentEntry { Id = "b", Name = "B", Type = "spell", Properties = { ["traditions"] = "primal" } };
        var none = new ContentEntry { Id = "c", Name = "C", Type = "spell" };
        var bard = new Dictionary<string, object> { ["class"] = "bard-class" };
        var druid = new Dictionary<string, object> { ["class"] = "druid-class" };
        var fighter = new Dictionary<string, object> { ["class"] = "fighter-class" }; // a class with no tradition
        var engine = Engine();

        Assert.False(engine.Holds(condition, occult, new Dictionary<string, object>()));
        Assert.True(engine.Holds(condition, occult, bard));
        Assert.False(engine.Holds(condition, occult, druid));
        Assert.True(engine.Holds(condition, single, druid));
        Assert.False(engine.Holds(condition, single, bard));
        Assert.False(engine.Holds(condition, none, bard));
        Assert.False(engine.Holds(condition, occult, fighter));
    }

    [Fact]
    public void TheContainsPropertyOfFieldFallback_ReadsTheSecondFieldOnlyWhenTheFirstEntryHasNoValue()
    {
        // The engine's one small change of chapter 8c: a class that has no tradition of its own (a witch's is her patron's) says where else to read it (`or`). A class that has one never reads the fallback.
        var condition = new RuleCondition
        {
            Property = "traditions",
            ContainsPropertyOfField = new FieldProperty { Field = "class", Property = "tradition", Or = new FieldProperty { Field = "subclass", Property = "tradition" } },
        };
        var divine = new ContentEntry { Id = "a", Name = "A", Type = "spell", Properties = { ["traditions"] = new List<object> { "divine" } } };
        var occult = new ContentEntry { Id = "b", Name = "B", Type = "spell", Properties = { ["traditions"] = new List<object> { "occult" } } };
        var witch = new Dictionary<string, object> { ["class"] = "witch-class", ["subclass"] = "patron-of-faith" }; // a divine patron
        var witchWithoutPatron = new Dictionary<string, object> { ["class"] = "witch-class" };
        var bard = new Dictionary<string, object> { ["class"] = "bard-class", ["subclass"] = "patron-of-faith" }; // an occult class; a subclass of another class does not change it
        var fighter = new Dictionary<string, object> { ["class"] = "fighter-class" };
        var engine = Engine();

        Assert.True(engine.Holds(condition, divine, witch));
        Assert.False(engine.Holds(condition, occult, witch));
        Assert.False(engine.Holds(condition, divine, witchWithoutPatron));
        Assert.False(engine.Holds(condition, occult, witchWithoutPatron));
        Assert.True(engine.Holds(condition, occult, bard));
        Assert.False(engine.Holds(condition, divine, bard));
        Assert.False(engine.Holds(condition, divine, fighter));
    }

    // ---- The ranger, the witch and the wizard (chapter 8c) ----

    [Fact]
    public void ALevel1Ranger_HasTheClassesNumbers_AndCastsNoSpells()
    {
        var ranger = Entry("class", "ranger-class");
        Assert.Equal("str|dex", (string)ranger.Properties["keyAttribute"]);
        Assert.False(ranger.Properties.ContainsKey("tradition"));

        var sheet = Character("ranger-class", null, 1, "dex");
        var evaluation = Engine().Recompute(sheet);

        Assert.Empty(Problems(evaluation));
        // The class's boost goes to Dexterity (the ancestry's free boost and a free one: +3); Strength has the one free boost of the level (+1).
        Assert.Equal(3L, sheet["dex"]);
        Assert.Equal(Number(ranger, "hp") + 2 + Number(Entry("ancestry", "dwarf-ancestry"), "hp"), Convert.ToInt64(sheet["HP_Max"], CultureInfo.InvariantCulture));
        // Perception expert; Fortitude and Reflex expert, Will trained; nature and survival trained.
        Assert.Equal(2, Rank(evaluation, "perception", "perception"));
        Assert.Equal(new[] { 2, 2, 1 }, new[] { Save(evaluation, "con"), Save(evaluation, "dex"), Save(evaluation, "wis") });
        Assert.Equal(new[] { 1, 1, 0 }, new[] { "nature", "survival", "athletics" }.Select(k => evaluation.Skills.Single(s => s.Key == k).Rank));
        // Simple, martial and unarmed weapons trained, advanced untrained; light, medium and unarmored trained, heavy untrained; the class DC trained, on the higher of Strength and Dexterity.
        Assert.Equal(new[] { 1, 1, 0, 1 }, new[] { "simple", "martial", "advanced", "unarmed" }.Select(k => Rank(evaluation, "weapons", k)));
        Assert.Equal(new[] { 1, 1, 1, 0 }, new[] { "unarmored", "light", "medium", "heavy" }.Select(k => Rank(evaluation, "armor", k)));
        Assert.Equal(1, Rank(evaluation, "classDc", "classDc"));
        Assert.Equal(10d + 1 + 2 + 3, Bonus(evaluation, "classDc"));
        // No spell ranks, no spells, no slots: warden spells come by a feat, and the focus spells list is the sheet's.
        Assert.Equal(new[] { 0, 0 }, new[] { Rank(evaluation, "spellAttack", "spellAttack"), Rank(evaluation, "spellDc", "spellDc") });
        Assert.Empty(evaluation.ListOptions["spells"]);
        Assert.Empty(evaluation.ListOptions["cantrips"]);
        Assert.Contains(evaluation.ListOptions["focusSpells"], s => (string)s.Properties["kind"] == "focus");
    }

    [Fact]
    public void ARangerAtLevels7And9And11_HasTheProficiencyIncreasesOfItsClassFeatures()
    {
        var at3 = Engine().Recompute(Character("ranger-class", null, 3, "dex"));
        var at7 = Engine().Recompute(Character("ranger-class", null, 7, "dex"));
        var at9 = Engine().Recompute(Character("ranger-class", null, 9, "dex"));
        var at11 = Engine().Recompute(Character("ranger-class", null, 11, "dex"));

        // Will Expertise (3rd): Will expert. Ranger Weapon Expertise (5th): the weapons expert. Natural Reflexes (7th): Reflex master. Perception Mastery (7th): Perception master.
        Assert.Equal(new[] { 2, 2, 2, 3, 3 }, new[] { Save(at7, "wis"), Save(at7, "con"), Rank(at7, "weapons", "martial"), Save(at7, "dex"), Rank(at7, "perception", "perception") });
        Assert.Equal(new[] { 1, 2 }, new[] { Rank(at3, "weapons", "martial"), Save(at3, "wis") }); // the weapons are still trained at the 3rd level, and Will is expert already
        // Ranger Expertise (9th): the class DC expert (and only the class DC: the spell ranks stay, the text of the feature says they wait on warden spells). Warden's Endurance and Medium Armor Expertise (11th).
        Assert.Equal(new[] { 1, 2, 0 }, new[] { Rank(at7, "classDc", "classDc"), Rank(at9, "classDc", "classDc"), Rank(at9, "spellDc", "spellDc") });
        Assert.Equal(new[] { 2, 3 }, new[] { Save(at9, "con"), Save(at11, "con") });
        Assert.Equal(new[] { 1, 2, 2 }, new[] { Rank(at9, "armor", "medium"), Rank(at11, "armor", "medium"), Rank(at11, "armor", "unarmored") });
    }

    [Theory]
    [InlineData("witch-class", "patron-of-faith", "divine", "religion")]
    [InlineData("witch-class", "patron-of-night", "occult", "occultism")]
    [InlineData("wizard-class", "school-of-battle-magic", "arcane", "arcana")]
    public void ALevel1WitchOrWizard_HasTheClassesNumbers_TheSpellRanks_TheCounts_AndTheTradition(string cls, string subclass, string tradition, string skill)
    {
        var entry = Entry("class", cls);
        Assert.Equal("int", (string)entry.Properties["keyAttribute"]);
        Assert.Equal("prepared", (string)entry.Properties["casting"]);
        // A witch's class holds no tradition (her patron's is hers); a wizard's holds arcane.
        Assert.Equal(cls == "wizard-class", entry.Properties.ContainsKey("tradition"));

        var sheet = Character(cls, subclass, 1, string.Empty);
        var evaluation = Engine().Recompute(sheet);

        Assert.Empty(Problems(evaluation));
        // The class's boost goes to Intelligence, which the background's boost also takes: +2.
        Assert.Equal(2L, sheet["int"]);
        Assert.Equal(Number(entry, "hp") + 2 + Number(Entry("ancestry", "dwarf-ancestry"), "hp"), Convert.ToInt64(sheet["HP_Max"], CultureInfo.InvariantCulture));
        // Perception, Fortitude and Reflex trained, Will expert; the skill of the patron or the class trained.
        Assert.Equal(1, Rank(evaluation, "perception", "perception"));
        Assert.Equal(new[] { 1, 1, 2 }, new[] { Save(evaluation, "con"), Save(evaluation, "dex"), Save(evaluation, "wis") });
        Assert.Equal(1, evaluation.Skills.Single(s => s.Key == skill).Rank);
        // Simple weapons and unarmed attacks trained; unarmored trained and every armor untrained; the class DC, the spell attack modifier and the spell DC trained (on Intelligence +2).
        Assert.Equal(new[] { 1, 0, 0, 1 }, new[] { "simple", "martial", "advanced", "unarmed" }.Select(k => Rank(evaluation, "weapons", k)));
        Assert.Equal(new[] { 1, 0, 0, 0 }, new[] { "unarmored", "light", "medium", "heavy" }.Select(k => Rank(evaluation, "armor", k)));
        Assert.Equal(new[] { 1, 1, 1 }, new[] { Rank(evaluation, "classDc", "classDc"), Rank(evaluation, "spellAttack", "spellAttack"), Rank(evaluation, "spellDc", "spellDc") });
        Assert.Equal(new[] { 1d + 2 + 2, 10d + 1 + 2 + 2 }, new[] { Bonus(evaluation, "spellAttack"), Bonus(evaluation, "spellDc") });
        // Five cantrips and two 1st-rank spells, and two slots of the 1st rank.
        Assert.Equal(5, evaluation.ListLimits["cantrips"]);
        Assert.Equal(2, evaluation.ListLimits["spells"]);
        Assert.Equal(new Dictionary<int, int> { [1] = 2 }, Slots(evaluation).Where(s => s.Value > 0).ToDictionary(s => s.Key, s => s.Value));
        Assert.All(evaluation.AllowedOptions["subclass"], o => Assert.Equal(cls, (string)o.Properties["class"]));
        // The lists offer the spells of the tradition: the witch's patron's, the wizard's arcane; nothing else.
        Assert.All(evaluation.ListOptions["spells"], s => Assert.True(Says(s, tradition) && Convert.ToInt32(s.Properties["rank"], CultureInfo.InvariantCulture) <= 1));
        Assert.All(evaluation.ListOptions["cantrips"], s => Assert.True(Says(s, tradition) && (string)s.Properties["kind"] == "cantrip"));
        Assert.NotEmpty(evaluation.ListOptions["spells"]);
        Assert.NotEmpty(evaluation.ListOptions["cantrips"]);
    }

    [Fact]
    public void AWitchWithNoPatron_HasNoTradition_SoNoSpellsAreOffered_AndTheOtherPatronsOfTheOtherTraditionsDoNotMix()
    {
        var none = Engine().Recompute(Character("witch-class", null, 1, string.Empty));
        Assert.Empty(none.ListOptions["spells"]);
        Assert.Empty(none.ListOptions["cantrips"]);

        // Two patrons of two traditions offer disjoint lists of the spells that name only one of them; a patron of another class is a warning.
        var spells = _registry.GetEntries(SystemId, "spell", Packs).ToList();
        var divineOnly = spells.First(s => (string)s.Properties["kind"] == "spell" && Says(s, "divine") && !Says(s, "occult") && Convert.ToInt32(s.Properties["rank"], CultureInfo.InvariantCulture) == 1);
        var occultOnly = spells.First(s => (string)s.Properties["kind"] == "spell" && Says(s, "occult") && !Says(s, "divine") && Convert.ToInt32(s.Properties["rank"], CultureInfo.InvariantCulture) == 1);
        var faith = Engine().Recompute(Character("witch-class", "patron-of-faith", 1, string.Empty));
        var night = Engine().Recompute(Character("witch-class", "patron-of-night", 1, string.Empty));
        Assert.Contains(faith.ListOptions["spells"], s => s.Id == divineOnly.Id);
        Assert.DoesNotContain(faith.ListOptions["spells"], s => s.Id == occultOnly.Id);
        Assert.Contains(night.ListOptions["spells"], s => s.Id == occultOnly.Id);
        Assert.DoesNotContain(night.ListOptions["spells"], s => s.Id == divineOnly.Id);

        var sheet = Character("witch-class", "patron-of-night", 1, string.Empty);
        sheet["spells"] = new List<object> { occultOnly.Id, divineOnly.Id };
        var problems = Problems(Engine().Recompute(sheet));
        Assert.Contains(problems, p => p.Contains($"Spells: {divineOnly.Name} - not on your class's spell list", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.Contains($"Spells: {occultOnly.Name} -", StringComparison.Ordinal));
        Assert.Contains(Problems(Engine().Recompute(Character("wizard-class", "patron-of-night", 1, string.Empty))), p => p.Contains("belongs to another class", StringComparison.Ordinal));
        Assert.Equal(new[] { "patron-of-curses", "patron-of-faith", "patron-of-fate", "patron-of-glyphs", "patron-of-night", "patron-of-the-wilds", "patron-of-winter" }, night.AllowedOptions["subclass"].Select(e => e.Id).Order());
    }

    [Fact]
    public void AWitchAtLevels5And7And19_HasTheLaterSlotCounts_AndTheLaterIncreases()
    {
        var at5 = Engine().Recompute(Character("witch-class", "patron-of-faith", 5, string.Empty));
        var at7 = Engine().Recompute(Character("witch-class", "patron-of-faith", 7, string.Empty));
        var at6 = Engine().Recompute(Character("witch-class", "patron-of-faith", 6, string.Empty));
        var at17 = Engine().Recompute(Character("witch-class", "patron-of-faith", 17, string.Empty));
        var at19 = Engine().Recompute(Character("witch-class", "patron-of-faith", 19, string.Empty));

        Assert.Equal(new Dictionary<int, int> { [1] = 3, [2] = 3, [3] = 2 }, Slots(at5).Where(s => s.Value > 0).ToDictionary(s => s.Key, s => s.Value));
        Assert.Equal(new Dictionary<int, int> { [1] = 3, [2] = 3, [3] = 3, [4] = 2 }, Slots(at7).Where(s => s.Value > 0).ToDictionary(s => s.Key, s => s.Value));
        Assert.Equal(new[] { 8, 11 }, new[] { at5.ListLimits["spells"]!.Value, at7.ListLimits["spells"]!.Value });
        // Magical Fortitude (5th): Fortitude expert. Expert Spellcaster (7th): the spell ranks expert. Will of the Pupil (17th): Will master. Legendary Spellcaster (19th) and the one 10th-rank slot of the Patron's Gift.
        Assert.Equal(new[] { 1, 2 }, new[] { Save(Engine().Recompute(Character("witch-class", "patron-of-faith", 4, string.Empty)), "con"), Save(at5, "con") });
        Assert.Equal(new[] { 1, 2 }, new[] { Rank(at6, "spellDc", "spellDc"), Rank(at7, "spellDc", "spellDc") });
        Assert.Equal(new[] { 2, 3 }, new[] { Save(at6, "wis"), Save(at17, "wis") });
        Assert.Equal(new[] { 3, 4 }, new[] { Rank(at17, "spellAttack", "spellAttack"), Rank(at19, "spellAttack", "spellAttack") });
        Assert.Equal(new Dictionary<int, int> { [1] = 3, [2] = 3, [3] = 3, [4] = 3, [5] = 3, [6] = 3, [7] = 3, [8] = 3, [9] = 3, [10] = 1 }, Slots(at19).Where(s => s.Value > 0).ToDictionary(s => s.Key, s => s.Value));
        Assert.Equal(28, at19.ListLimits["spells"]);
    }

    [Fact]
    public void AWizardAtLevels5And7And13_HasTheLaterSlotCounts_AndTheLaterIncreases()
    {
        var at4 = Engine().Recompute(Character("wizard-class", "school-of-mentalism", 4, string.Empty));
        var at5 = Engine().Recompute(Character("wizard-class", "school-of-mentalism", 5, string.Empty));
        var at7 = Engine().Recompute(Character("wizard-class", "school-of-mentalism", 7, string.Empty));
        var at13 = Engine().Recompute(Character("wizard-class", "school-of-mentalism", 13, string.Empty));

        Assert.Equal(new Dictionary<int, int> { [1] = 3, [2] = 3, [3] = 2 }, Slots(at5).Where(s => s.Value > 0).ToDictionary(s => s.Key, s => s.Value));
        Assert.Equal(new[] { 8, 11 }, new[] { at5.ListLimits["spells"]!.Value, at7.ListLimits["spells"]!.Value });
        // Reflex Expertise (5th): Reflex expert. Expert Spellcaster (7th): the spell ranks expert. Defensive Robes (13th): unarmored defense expert.
        Assert.Equal(new[] { 1, 2 }, new[] { Save(at4, "dex"), Save(at5, "dex") });
        Assert.Equal(new[] { 1, 2 }, new[] { Rank(at5, "spellAttack", "spellAttack"), Rank(at7, "spellAttack", "spellAttack") });
        Assert.Equal(new[] { 1, 2 }, new[] { Rank(at7, "armor", "unarmored"), Rank(at13, "armor", "unarmored") });
        Assert.Equal(0, Rank(at13, "armor", "light")); // the wizard is trained in no armor
        // The school's initial focus spell is a grant by id (a focus spell of the pack).
        var school = Entry("subclass", "school-of-mentalism");
        var grant = ((IEnumerable<object>)school.Properties["features"]).Cast<IDictionary<string, object>>().SelectMany(r => ((IEnumerable<object>)r["adds"]).Cast<IDictionary<string, object>>()).Single(g => (string)g["to"] == "focusSpells");
        Assert.Equal("focus", (string)Entry("spell", ((IEnumerable<object>)grant["entries"]).Cast<string>().Single()).Properties["kind"]);
    }
}
