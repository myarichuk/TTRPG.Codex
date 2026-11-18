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
}
