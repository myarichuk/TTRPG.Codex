using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// Loads the real <c>content/</c> folder and checks the Four Degrees system (step P7 of the hybrid-system plan) and its pack: it loads with no
/// warning, no publisher, setting or deity word is anywhere in its files, the ORC licence block is there, the pack has its counts and required
/// fields, a character builds to level 3 in both attribute methods, and a printed creature's numbers read the conditions that hold.
/// </summary>
public class DegreesSystemSmokeTests : IAsyncLifetime
{
    private const string SystemId = "four-degrees";
    private const string PackId = "four-degrees-core";
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
        _system = _loader.GetDefinition(SystemId)!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class PackResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries(SystemId, type, Packs).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries(SystemId, type, Packs);
    }

    private IEnumerable<ContentEntry> Entries(string type) => _registry.GetEntries(SystemId, type, Packs);

    private static string Folder => Path.Combine(TestPaths.ContentRoot, "systems", SystemId);

    private static IEnumerable<string> Files() => Directory.EnumerateFiles(Folder, "*.*", SearchOption.AllDirectories);

    [Fact]
    public void LoadsWithoutWarnings()
    {
        Assert.Null(_loader.LoadException);
        Assert.Empty(_loader.LoadWarnings);
        Assert.Contains(SystemId, _loader.LoadedSystemIds);
        Assert.NotNull(_system);
    }

    /// <summary>The names no file may say. The fifth is not matched inside the word of one spell's name (two letters before it), but it is in camelCase and snake_case identifiers and after a prefix.</summary>
    internal const string DeniedPattern = "pathfinder|paizo|golarion|pf2e|(?<!lo)remaster|starfinder|dungeons|d&d|wizards of the coast";

    [Theory]
    [InlineData("isRemaster", true)]
    [InlineData("is_remaster_flag", true)]
    [InlineData("Remastered", true)]
    [InlineData("the remaster", true)]
    [InlineData("Loremaster's Etude", false)]
    public void TheDeniedPatternCatchesTheFifthNameInIdentifiers_ButNotTheSpellNameThatContainsIt(string text, bool caught)
    {
        Assert.Equal(caught, new Regex(DeniedPattern, RegexOptions.IgnoreCase).IsMatch(text));
    }

    [Fact]
    public void NoFileOfTheSystemNamesAPublisherASettingOrAGame()
    {
        // Every line of every file, the overlays and comments included. The licence lines are the one place the upstream publisher and
        // product are named (the ORC requires the credit), so lines naming the licence url and the attribution line are skipped.
        var denied = new Regex(DeniedPattern, RegexOptions.IgnoreCase);
        var hits = Files()
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(Folder, f), Line: i + 1, Text: line)))
            .Where(l => !l.Text.Contains("orclicense", StringComparison.Ordinal)
                && !l.Text.TrimStart().StartsWith("attribution:", StringComparison.Ordinal)
                && denied.IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.Empty(hits);
    }

    [Fact]
    public void TheManifestsCarryTheOrcLicenceBlock_AsAnAdaptation()
    {
        foreach (var license in new[] { _system.License, _registry.GetPack(PackId)!.License })
        {
            Assert.NotNull(license);
            Assert.Equal("ORC", license!.Name);
            Assert.False(string.IsNullOrWhiteSpace(license.Attribution));
            Assert.True(license.Modified);
            Assert.True(license.Notices.Count >= 3);
            Assert.DoesNotContain(license.Notices.Concat(new[] { license.Attribution! }), n => n.Contains("original homebrew", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The ORC Notice, the upstream attribution and the Reserved Material statement are copied from the licence and the upstream notice, never
    /// written from memory. While any is a PLACEHOLDER the system must stay in unreleased.txt, and it must be a test failure to ship it without them.</summary>
    [Fact]
    public void ASystemWithAPlaceholderIsKeptOutOfReleases()
    {
        var hasPlaceholder = Files().Where(f => f.EndsWith(".yaml")).Any(f => File.ReadLines(f).Any(l => l.Contains("PLACEHOLDER", StringComparison.Ordinal) && !l.TrimStart().StartsWith('#')));
        var unreleased = File.ReadAllLines(Path.Combine(TestPaths.ContentRoot, "systems", "unreleased.txt")).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));
        var bundled = File.ReadAllLines(Path.Combine(TestPaths.ContentRoot, "systems", "bundled.txt")).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));

        Assert.DoesNotContain(SystemId, bundled);
        if (hasPlaceholder) Assert.Contains(SystemId, unreleased);
    }

    [Fact]
    public void AShippedSystemHasNoPlaceholder()
    {
        var offenders = Files().Where(f => f.EndsWith(".yaml")).Where(f => File.ReadAllText(f).Contains("PLACEHOLDER", StringComparison.Ordinal)).Select(f => Path.GetRelativePath(Folder, f)).ToList();
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("ancestry", 2)]
    [InlineData("background", 2)]
    [InlineData("class", 2)]
    [InlineData("weapon", 4)]
    [InlineData("armor", 3)]
    [InlineData("creature", 3)]
    public void ThePackHasItsCounts_RequiredFields_AndEveryEntryIsTaggedHomebrew(string type, int expected)
    {
        var entries = Entries(type).ToList();
        Assert.Equal(expected, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Contains("homebrew", e.Tags);
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
            if (type is "ancestry" or "background" or "class" or "creature") Assert.False(string.IsNullOrWhiteSpace(e.Description), e.Id);
            if (type == "weapon") Assert.True(((IDictionary<string, object>)e.Properties["use"]).ContainsKey("damage"), e.Id);
        });
    }

    [Fact]
    public void TheSystemDeclaresItsMomentsAndTheFaithShape()
    {
        Assert.Equal(new[] { "dailyPrep", "catchBreath", "sessionStart", "heroicRecovery" }, _system.Moments.Select(m => m.Key).ToArray());
        Assert.Equal(new[] { "heroicRecovery" }, _system.Moments.Where(m => m.IsActorScoped).Select(m => m.Key).ToArray());
        Assert.Contains(_system.ActorFields, f => f.Key == "faith" && f.TargetEntityType == "faith");
        Assert.Contains(_system.ActorFields, f => f.Key == "faithVows");
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
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + key] = 2L;
        return sheet;
    }

    private static List<string> Problems(SheetEvaluation evaluation) =>
        evaluation.Violations.Select(v => $"violation {v.Key}: {v.Message}").Concat(evaluation.Warnings.Select(v => $"warning {v.Key}: {v.Message}")).ToList();

    [Theory]
    [InlineData("marshkin", "ferry-hand", "warden")]
    [InlineData("cliffborn", "lamp-keeper", "lorekeeper")]
    public void ACharacterBuildsToLevel3_WithNoViolationAndNoWarning(string ancestry, string background, string cls)
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        for (var level = 1; level <= 3; level++)
            Assert.Empty(Problems(engine.Recompute(Character(ancestry, background, cls, level))));
    }

    [Fact]
    public void ABoostBuildToLevel15_RunsOnTheModifierScale_WithAPendingPartialBoostAtPlusFour()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        var sheet = Character("marshkin", "ferry-hand", "warden", 15);
        sheet[SheetRuleEngine.MethodKey] = "boosts";
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet.Remove(SheetRuleEngine.BasePrefix + key);
        sheet["boost__ancestry__marshkin.b1"] = new List<object> { "str" }; // the "str|con" choice
        sheet["boost__ancestry__marshkin.b2"] = new List<object> { "con" }; // the free boost
        // The four free boosts that finish the 1st-level build, each to a different modifier.
        foreach (var (slot, key) in new[] { ("m1", "int"), ("m2", "cha"), ("m3", "con"), ("m4", "wis") }) sheet[$"boost__lvl1__{slot}"] = new List<object> { key };
        foreach (var level in new[] { 5, 10, 15 })
        {
            foreach (var (slot, key) in new[] { ("m1", "str"), ("m2", "dex"), ("m3", "wis"), ("m4", "con") }) sheet[$"boost__lvl{level}__{slot}"] = new List<object> { key };
        }

        var evaluation = engine.Recompute(sheet);

        // Ancestry: +1 Dexterity and Wisdom (fixed), Strength and Constitution (picked), -1 Charisma; class: +1 Strength; the 1st-level free boosts: Intelligence, Charisma,
        // Constitution, Wisdom; then +1 on four attributes at each of levels 5, 10 and 15.
        Assert.Equal(0L, sheet["cha"]);
        Assert.Equal(4L, sheet["str"]);
        Assert.Equal(4L, sheet["dex"]);
        Assert.Equal(4L, sheet["con"]);
        Assert.Equal(4L, sheet["wis"]);
        Assert.Equal(1L, sheet["int"]);
        // Strength, Constitution and Wisdom reached +4 at level 10, so their level-15 boosts are the pending partial ones (Dexterity got there at level 15).
        Assert.Equal(new[] { "con", "str", "wis" }, evaluation.Attributes!.Boosts!.Halves.OrderBy(h => h, StringComparer.Ordinal).ToArray());
        Assert.Empty(evaluation.Violations);
    }

    [Fact]
    public void ACharacterBuiltFromBoosts_HasNoViolation_AndTheBoostsLand()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        var sheet = Character("marshkin", "ferry-hand", "warden", 1);
        sheet[SheetRuleEngine.MethodKey] = "boosts";
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet.Remove(SheetRuleEngine.BasePrefix + key);
        sheet["boost__ancestry__marshkin.b1"] = new List<object> { "str" }; // the "str|con" choice
        sheet["boost__ancestry__marshkin.b2"] = new List<object> { "con" }; // the free boost
        foreach (var (slot, key) in new[] { ("m1", "str"), ("m2", "dex"), ("m3", "int"), ("m4", "cha") }) sheet[$"boost__lvl1__{slot}"] = new List<object> { key }; // the 1st-level free boosts

        var evaluation = engine.Recompute(sheet);

        Assert.Empty(Problems(evaluation));
        // Wisdom 10 gets the ancestry's fixed boost (+1 to the modifier); trained Perception at level 1 is 1 + 2.
        Assert.Equal(4, Convert.ToInt32(evaluation.Derived["perception"]));
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
        // Hardy makes the Constitution save expert: 3 + 4 + 2. The Wisdom save stays trained: 3 + 2 + 2. The background trains Athletics: 3 + 2 + 2.
        Assert.Equal(9, evaluation.SavingThrows.Single(s => s.Key == "con").Bonus);
        Assert.Equal(7, evaluation.SavingThrows.Single(s => s.Key == "wis").Bonus);
        Assert.Equal(7, evaluation.Skills.Single(s => s.Key == "athletics").Bonus);
    }

    [Fact]
    public void ALevel3Lorekeeper_HasExpertPerception_AndTheBackgroundTrainsCrafting()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        var evaluation = engine.Recompute(Character("cliffborn", "lamp-keeper", "lorekeeper", 3));

        Assert.Equal(9, Convert.ToInt32(evaluation.Derived["perception"])); // 3 + 4 + Wisdom 14 (+2)
        Assert.Equal(7, evaluation.Skills.Single(s => s.Key == "crafting").Bonus); // 3 + 2 + Intelligence (+2)
        Assert.Contains("Sharp watch", evaluation.Features.Select(f => f.Name).ToList());
    }

    [Fact]
    public void ALevel1Lorekeeper_DoesNotYetHaveLevel2Or3Features()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        var evaluation = engine.Recompute(Character("cliffborn", "lamp-keeper", "lorekeeper", 1));

        var features = evaluation.Features.Select(f => f.Name).ToList();
        Assert.Contains("Prepared trick", features);
        Assert.DoesNotContain("Sharp watch", features);
        Assert.DoesNotContain("Inner reserve", features);
    }

    [Fact]
    public void AWardenInMediumArmor_AddsItsBase_ItsCappedDexterity_AndItsRank()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        var sheet = Character("marshkin", "ferry-hand", "warden", 3);
        sheet["inventory"] = new List<object> { "lacquered-scale|equipped:1" };

        var armorClass = engine.Recompute(sheet).ArmorClass!;

        // Base 15, Dexterity 14 (+2) capped at 1, trained in medium armor at level 3 (3 + 2).
        Assert.Equal(21, armorClass.Total);
    }

    [Fact]
    public void Encumbered_BringsClumsy1_AndHeartenedDoesNotStackWithItself()
    {
        var sheet = new Dictionary<string, object> { [SheetModel.ConditionsKey] = new List<object> { "Encumbered" } };

        var held = SheetConditions.Holding(_system, sheet);

        Assert.Equal(1, held.Single(h => h.Name == "Clumsy").Value);
        Assert.False(held.Single(h => h.Name == "Clumsy").Listed);
        // Status bonuses of one type keep the highest, so a second copy of Heartened adds nothing (the rule is declared in modifierTypes).
        Assert.Equal("highest", _system.ModifierTypes!.Types["status"]);
    }

    [Theory]
    [InlineData("Lesser cover", 1)]
    [InlineData("Standard cover", 2)]
    [InlineData("Greater cover", 4)]
    public void Cover_RaisesArmorClassByItsType_AndTwoCoversDoNotAdd(string cover, int bonus)
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        int ArmorClass(params string[] names)
        {
            var sheet = Character("marshkin", "ferry-hand", "warden", 3);
            sheet[SheetModel.ConditionsKey] = names.Cast<object>().ToList();
            return engine.Recompute(sheet).ArmorClass!.Total;
        }
        var plain = ArmorClass();

        // The circumstance numbers of the rules entry `cover`: +1, +2 and +4 to armor class.
        Assert.Equal(plain + bonus, ArmorClass(cover));
        Assert.Equal(plain + bonus, ArmorClass(cover, "Lesser cover"));
        Assert.Equal(plain + 4, ArmorClass("Standard cover", "Greater cover"));
    }

    [Fact]
    public void StandardCover_OffersTheReflexSaveAndStealthBonusesAsTickBoxes_ButLesserCoverOffersNone()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        List<string> Offered(string cover)
        {
            var sheet = Character("marshkin", "ferry-hand", "warden", 3);
            sheet[SheetModel.ConditionsKey] = new List<object> { cover };
            return engine.AllOptIns(sheet).Select(o => o.Label).ToList();
        }

        Assert.Equal(new[] { "Against an area effect", "To Hide, Sneak or avoid detection" }, Offered("Standard cover").OrderBy(x => x));
        Assert.Equal(new[] { "Against an area effect", "To Hide, Sneak or avoid detection" }, Offered("Greater cover").OrderBy(x => x));
        Assert.Empty(Offered("Lesser cover"));
    }

    [Fact]
    public void APrintedCreature_ReadsItsNumbers_AndAConditionLowersThem()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        var hound = Entries("creature").Single(e => e.Id == "mire-hound");
        var sheet = new Dictionary<string, object>(hound.Properties) { [SheetRuleEngine.MethodKey] = SheetRuleEngine.PrintedMethod };

        var plain = engine.Printed(sheet);
        Assert.Equal(16, plain.Single(n => n.Target == "armorClass").Total);
        Assert.Equal(6, plain.Single(n => n.Target == "perception").Total);
        Assert.Equal(14, plain.Single(n => n.Target == "classDc").Total);
        Assert.Equal(8, plain.Single(n => n.Label == "Bite").Total);

        // Frightened 1 lowers armor class, Perception, the DC and the Strike by 1; Clumsy 1 lowers armor class by 1 as well (not the melee Bite).
        sheet["condition_frightened"] = 1L;
        sheet["condition_clumsy"] = 1L;
        var lowered = engine.Printed(sheet);
        Assert.Equal(15, lowered.Single(n => n.Target == "armorClass").Total);
        Assert.Equal(5, lowered.Single(n => n.Target == "perception").Total);
        Assert.Equal(13, lowered.Single(n => n.Target == "classDc").Total);
        Assert.Equal(7, lowered.Single(n => n.Label == "Bite").Total);
        // Two status penalties of 1 on armor class keep only the worst: -1, not -2 (15, not 14, above).
    }

    [Fact]
    public void Clumsy_AloneLowersTheDexteritySave_TheRangedStrike_AndArmorClass_ButNotTheMeleeOne()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        var wisp = Entries("creature").Single(e => e.Id == "veil-wisp");
        var sheet = new Dictionary<string, object>(wisp.Properties) { [SheetRuleEngine.MethodKey] = SheetRuleEngine.PrintedMethod, ["condition_clumsy"] = 2L };

        var lowered = engine.Printed(sheet);

        Assert.Equal(15, lowered.Single(n => n.Target == "armorClass").Total);
        Assert.Equal(8, lowered.Single(n => n.Target == "save.dex").Total);
        Assert.Equal(7, lowered.Single(n => n.Label == "Lure spark").Total);
        Assert.Equal(9, lowered.Single(n => n.Label == "Chilling touch").Total);
        // Constitution saves are not Dexterity-based.
        Assert.Equal(5, lowered.Single(n => n.Target == "save.con").Total);
    }

    [Fact]
    public void Drained_LowersTheConstitutionSaveAndTheMaximumHitPoints_ByItsValueTimesTheLevel()
    {
        var engine = new SheetRuleEngine(_system, new PackResolver(_registry));
        var sheet = Character("marshkin", "ferry-hand", "warden", 3);
        var before = engine.Recompute(sheet);
        sheet["condition_drained"] = 2L;

        var after = engine.Recompute(sheet);

        Assert.Equal(before.HitPoints!.Max - 6, after.HitPoints!.Max);
        Assert.Equal(before.SavingThrows.Single(s => s.Key == "con").Bonus - 2, after.SavingThrows.Single(s => s.Key == "con").Bonus);
    }

    [Fact]
    public void DyingTopsOutAtFour_AndDoomedAndDrainedDropAtDailyPreparations()
    {
        Assert.Equal("4", _system.ConditionEffects["Dying"].Value!.Max);
        foreach (var name in new[] { "Doomed", "Drained" })
        {
            var decrease = _system.ConditionEffects[name].Value!.Decrease!;
            Assert.Equal(("dailyPrep", 1), (decrease.On, decrease.By));
        }
    }
}
