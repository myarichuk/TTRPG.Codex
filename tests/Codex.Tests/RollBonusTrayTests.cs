using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Localization;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>The sheet's "bonuses for the next roll" (docs/ROLL_BONUSES_DESIGN.md, RB2): how a roll takes the always-on dice and the ticked opt-ins into
/// its expression and label, that a ticked one applies to the next roll it fits and then unticks while one that does not fit stays ticked, what a
/// ticked spend pays, and the words that say which rolls a box fits. The tick boxes of a named roll are the engine's (<see cref="SheetRolls.OptIns"/>).</summary>
public class RollBonusTrayTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public RollBonusTrayTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static Dictionary<string, object> Mod(string target, object amount) => Map(("target", target), ("amount", amount));

    private static Dictionary<string, object> OptIn(string target, object amount, string label, Dictionary<string, object>? spends = null)
    {
        var optIn = Map(("label", label));
        if (spends != null) optIn["spends"] = spends;
        return Map(("target", target), ("amount", amount), ("optIn", optIn));
    }

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) Build(string feature, params Dictionary<string, object>[] modifiers)
    {
        var entry = new ContentEntry { Type = "subclass", Id = "tester", Name = "Tester", SystemId = "srd5e", PackId = "homebrew" };
        entry.Properties["class"] = "wizard";
        entry.Properties["features"] = new List<object>
        {
            new Dictionary<string, object>
            {
                ["name"] = feature,
                ["level"] = 3L,
                ["description"] = feature + ".",
                ["modifiers"] = modifiers.Cast<object>().ToList()
            }
        };
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, entry));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "wizard";
        sheet["subclass"] = "tester";
        sheet["species"] = "human";
        sheet["level"] = 5L;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var attribute in new[] { "str", "dex", "con", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + attribute] = 10L;
        sheet[SheetRuleEngine.BasePrefix + "int"] = 16L;
        sheet["inventory"] = new List<object> { "longbow|equipped:1", "longsword|equipped:1" };
        sheet["cantrips"] = new List<object> { "fire-bolt" };
        sheet["spells"] = new List<object> { "burning-hands" };
        return (engine, sheet);
    }

    private static SheetAction Action(SheetEvaluation evaluation, string id) => evaluation.Actions.Single(a => a.Entry.Id == id);

    [Fact]
    public void AlwaysOnDice_AreAppended_AndNamedInTheLabel()
    {
        var (engine, sheet) = Build("Blessed", Mod("check", "1d4"));
        var built = new RollBonusTray().Build(engine, sheet, engine.SkillSite("stealth"), "1d20+3", "Stealth check");

        Assert.Equal("1d20+3+1d4", built.Expression);
        Assert.Equal("Stealth check (+1d4 Blessed)", built.Label);
        Assert.Empty(built.Applied);

        // A roll the dice do not reach stays as it was.
        var save = new RollBonusTray().Build(engine, sheet, engine.SaveSite("dex"), "1d20+2", "Dexterity save");
        Assert.Equal("1d20+2", save.Expression);
        Assert.Equal("Dexterity save", save.Label);
    }

    [Fact]
    public void ATickedOptIn_IsTakenByTheNextRollItFits_ThenUnticks()
    {
        var (engine, sheet) = Build("Lucky charm", OptIn("check", "1d6", "Lucky"));
        var tray = new RollBonusTray();
        var id = Assert.Single(engine.AllOptIns(sheet)).Id;

        // Not ticked: nothing is added.
        Assert.Equal("1d20+3", tray.Build(engine, sheet, engine.SkillSite("stealth"), "1d20+3", "Stealth check").Expression);

        tray.Set(id, true);
        var built = tray.Build(engine, sheet, engine.SkillSite("stealth"), "1d20+3", "Stealth check");
        Assert.Equal("1d20+3+1d6", built.Expression);
        Assert.Equal("Stealth check (+1d6 Lucky)", built.Label);
        Assert.Equal(id, Assert.Single(built.Applied).Id);
        Assert.True(tray.IsTicked(id)); // until the roll is made

        tray.Used(built.Applied);
        Assert.False(tray.IsTicked(id));
        Assert.Equal("1d20+3", tray.Build(engine, sheet, engine.SkillSite("stealth"), "1d20+3", "Stealth check").Expression);
    }

    [Fact]
    public void ATickedOptIn_ThatDoesNotFitTheRoll_StaysTicked_ForTheNextOneThatDoes()
    {
        var (engine, sheet) = Build("Lucky charm", OptIn("check", "1d6", "Lucky"));
        var tray = new RollBonusTray();
        var id = Assert.Single(engine.AllOptIns(sheet)).Id;
        tray.Set(id, true);

        var save = tray.Build(engine, sheet, engine.SaveSite("dex"), "1d20+2", "Dexterity save");
        Assert.Equal("1d20+2", save.Expression);
        Assert.Empty(save.Applied);
        tray.Used(save.Applied);
        Assert.True(tray.IsTicked(id));

        var check = tray.Build(engine, sheet, engine.CheckSite("int"), "1d20+3", "Intelligence check");
        Assert.Equal("1d20+3+1d6", check.Expression);
        tray.Used(check.Applied);
        Assert.False(tray.IsTicked(id));
    }

    [Fact]
    public void ANumberOptIn_IsAddedAsANumber_OnWeaponAttacksOnly()
    {
        var (engine, sheet) = Build("Sacred Weapon", OptIn("attack.weapon", 2L, "Sacred Weapon"));
        var evaluation = engine.Recompute(sheet);
        var tray = new RollBonusTray();
        tray.Set(Assert.Single(engine.AllOptIns(sheet)).Id, true);

        var longbow = tray.Build(engine, sheet, engine.AttackSite(Action(evaluation, "longbow")), "1d20+5", "Attack with Longbow");
        Assert.Equal("1d20+5+2", longbow.Expression);
        Assert.Equal("Attack with Longbow (+2 Sacred Weapon)", longbow.Label);

        var fireBolt = tray.Build(engine, sheet, engine.AttackSite(Action(evaluation, "fire-bolt")), "1d20+5", "Attack with Fire Bolt");
        Assert.Equal("1d20+5", fireBolt.Expression);
        Assert.Empty(fireBolt.Applied);
    }

    [Fact]
    public void AnOptInThatSpends_IsTakenOnlyWhileThePoolCanPay_AndIsPaidThroughTheHostsSpend()
    {
        var (engine, sheet) = Build("Lucky charm", OptIn("check", "1d6", "Lucky", Map(("key", "lucky"), ("amount", 1L))));
        var tray = new RollBonusTray();
        var id = Assert.Single(engine.AllOptIns(sheet)).Id;
        tray.Set(id, true);

        Assert.Empty(tray.Build(engine, sheet, engine.SkillSite("stealth"), "1d20", "Stealth check").Applied); // no pool: not payable

        sheet["uses_lucky"] = 1L;
        var built = tray.Build(engine, sheet, engine.SkillSite("stealth"), "1d20", "Stealth check");
        Assert.Single(built.Applied);

        var spent = new List<(string Key, int Delta, string Label)>();
        Assert.True(RollBonusTray.PayAsync(built.Applied, change => { spent.Add(change); return Task.FromResult(true); }).Result);
        Assert.Equal(new[] { ("uses_lucky", -1, "Lucky") }, spent);

        // A spend the owner refuses stops the roll.
        Assert.False(RollBonusTray.PayAsync(built.Applied, _ => Task.FromResult(false)).Result);
    }

    [Fact]
    public void TheSheetsOwnSpellAttack_TakesTheAlwaysOnNumbersOfItsTargets_OnlyWhenAsked()
    {
        var (engine, sheet) = Build("Focus", Mod("attack.spell", 1L));
        var site = engine.SpellAttackSite();

        var withNumber = new RollBonusTray().Build(engine, sheet, site, "1d20+7", "Spell attack", addNumber: true);
        Assert.Equal("1d20+7+1", withNumber.Expression);
        Assert.Equal("Spell attack (+1 Focus)", withNumber.Label);

        Assert.Equal("1d20+7", new RollBonusTray().Build(engine, sheet, site, "1d20+7", "Spell attack").Expression);
    }

    [Fact]
    public void WithoutAnEngineOrASite_TheRollIsUnchanged()
    {
        var (engine, sheet) = Build("Blessed", Mod("check", "1d4"));
        var tray = new RollBonusTray();
        Assert.Equal("1d20", tray.Build(null, sheet, engine.SkillSite("stealth"), "1d20", "A roll").Expression);
        Assert.Equal("1d20", tray.Build(engine, sheet, null, "1d20", "A roll").Expression);
        Assert.Equal("1d20", tray.Build(engine, null, engine.SkillSite("stealth"), "1d20", "A roll").Expression);
    }

    [Fact]
    public void TheSheetsOptIns_AreListedAcrossRolls_EachWithTheTargetItFits_AndKeepDropsWhatWentAway()
    {
        var (engine, sheet) = Build("Tricks", OptIn("check", "1d6", "Lucky"), OptIn("attack.weapon.ranged", 1L, "Steady"), Mod("damage", 1L));

        var all = engine.AllOptIns(sheet);
        Assert.Equal(new[] { "check", "attack.weapon.ranged" }, all.Select(o => o.Target));

        var tray = new RollBonusTray();
        tray.Set(all[0].Id, true);
        tray.Set("gone#0", true);
        tray.Keep(all);
        Assert.True(tray.IsTicked(all[0].Id));
        Assert.False(tray.IsTicked("gone#0"));
    }

    [Fact]
    public void ATickThatCannotBePaid_IsNotKept_AndHealingReadsNoDamageSite()
    {
        var (engine, sheet) = Build("Lucky charm", OptIn("check", "1d6", "Lucky", Map(("key", "lucky"), ("amount", 1L))));
        var tray = new RollBonusTray();
        var id = Assert.Single(engine.AllOptIns(sheet)).Id;
        tray.Set(id, true);
        tray.Keep(engine.AllOptIns(sheet)); // no pool: the box is disabled, so a tick on it is dropped
        Assert.False(tray.IsTicked(id));

        sheet["spells"] = new List<object> { "cure-wounds" };
        var healing = Action(engine.Recompute(sheet), "cure-wounds");
        Assert.True(healing.Heals);
        Assert.Null(RollBonusTray.DamageKind(healing));
        Assert.Equal(RollKind.Damage, RollBonusTray.DamageKind(Action(engine.Recompute(Build("Nothing").Sheet), "longbow")));
    }

    [Fact]
    public void ATableCard_OffersOnlyTheBoxesItsAttackAndDamageRollsCanTake()
    {
        var (engine, sheet) = Build("Tricks", OptIn("check", "1d6", "Lucky"), OptIn("attack.weapon.ranged", 1L, "Steady"), OptIn("damage", 1L, "Brutal"), OptIn("save.dex", 1L, "Nimble"));
        Assert.Equal(new[] { "attack.weapon.ranged", "damage" }, engine.AllOptIns(sheet).Where(RollBonusTray.FitsCard).Select(o => o.Target));
    }

    [Fact]
    public void TheRollOfARequest_ComesFromItsKindAndKey()
    {
        var (engine, sheet) = Build("Nothing");
        var evaluation = engine.Recompute(sheet);
        var longbow = Action(evaluation, "longbow");

        Assert.Equal(RollKind.WeaponAttack, RollBonusTray.SiteOf(engine, RollKind.WeaponAttack, null, longbow)!.Kind);
        Assert.Equal(RollKind.Damage, RollBonusTray.SiteOf(engine, RollKind.Damage, null, longbow)!.Kind);
        Assert.True(RollBonusTray.SiteOf(engine, RollKind.SpellAttack, null, null)!.Reads("attack.spell"));
        Assert.True(RollBonusTray.SiteOf(engine, RollKind.Save, "dex", null)!.Reads("save.dex"));
        Assert.True(RollBonusTray.SiteOf(engine, RollKind.Check, "int", null)!.Reads("check.int"));
        Assert.True(RollBonusTray.SiteOf(engine, RollKind.SkillCheck, "stealth", null)!.Reads("check.skill.stealth"));
        Assert.Null(RollBonusTray.SiteOf(engine, null, null, null));
        Assert.Null(RollBonusTray.SiteOf(engine, RollKind.Save, null, null));
        Assert.Null(RollBonusTray.SiteOf(null, RollKind.Save, "dex", null));
    }

    [Theory]
    [InlineData("attack", "Actor.Bonus.On.Attack", null)]
    [InlineData("attack.weapon.ranged", "Actor.Bonus.On.AttackWeaponRanged", null)]
    [InlineData("attack.spell.melee", "Actor.Bonus.On.AttackSpellMelee", null)]
    [InlineData("damage.ranged", "Actor.Bonus.On.DamageRanged", null)]
    [InlineData("save", "Actor.Bonus.On.Save", null)]
    [InlineData("check", "Actor.Bonus.On.Check", null)]
    [InlineData("save.dex", "Actor.Bonus.On.SaveOf", "dex")]
    [InlineData("check.int", "Actor.Bonus.On.CheckOf", "int")]
    [InlineData("check.skill.stealth", "Actor.Bonus.On.CheckSkill", "stealth")]
    [InlineData("attack.entry.longbow", "Actor.Bonus.On.AttackEntry", "longbow")]
    [InlineData("damage.entry.fire-bolt", "Actor.Bonus.On.DamageEntry", "fire-bolt")]
    public void AWordForEachRollTarget(string target, string key, string? argument)
    {
        Assert.Equal((key, argument), RollBonusTray.AppliesTo(target));
    }

    [Fact]
    public void EveryRollTargetsWord_IsInEveryLanguage()
    {
        var system = _loader.GetDefinition("srd5e")!;
        var targets = RollSiteTargets.Listed(system, system.SheetRules!).Select(t => t.Key)
            .Concat(new[] { "attack.entry.x", "damage.entry.x", "check.skill.x" }).ToList();
        foreach (var language in SupportedCultures.Tags)
        {
            var text = Localizer.For(language);
            foreach (var target in targets)
            {
                var (key, _) = RollBonusTray.AppliesTo(target);
                Assert.True(text.Get(key) != key, $"{language}: {target} has no word ({key})");
            }
        }
    }

    [Fact]
    public void ALabelSaysTheBonusesTheRollerChose_AndNotTheAlwaysOnOnes()
    {
        var bonuses = new[]
        {
            new RollBonus(null, "Harm", "Harm", -1, false),
            new RollBonus("ambush#0", "Ambush", "Attacking from hiding", 1, true)
        };

        Assert.Equal("Prowl (+1 Attacking from hiding)", RollBonusLabel.Of("Prowl", bonuses, pool: false));
        Assert.Equal("Prowl (+1d Attacking from hiding)", RollBonusLabel.Of("Prowl", bonuses, pool: true));
        Assert.Equal("Prowl", RollBonusLabel.Of("Prowl", bonuses.Take(1), pool: true));
    }

    private sealed class WithHomebrew(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }
}

/// <summary>What a named roll shows before it is rolled (docs/ROLL_BONUSES_DESIGN.md, RB2): the bonuses that always count, so the player sees why the
/// pool is what it is, next to the tick boxes the roll offers. On the real Forged in the Dark system.</summary>
public class NamedRollBonusListTests
{
    private sealed class Entries(params ContentEntry[] entries) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => entries.Where(e => e.Type == type);
    }

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static ContentEntry Ability(string id, params Dictionary<string, object>[] modifiers)
    {
        var entry = new ContentEntry { Type = "special-ability", Id = id, Name = id, SystemId = "fitd-srd", PackId = "test", Description = id };
        entry.Properties["modifiers"] = modifiers.Cast<object>().ToList();
        return entry;
    }

    private static readonly SystemDefinition Fitd = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "fitd-srd", "system.yaml"));

    [Fact]
    public void TheAlwaysOnBonuses_AreListedWithTheirSources_AndTheTickBoxesAreNotInThem()
    {
        var always = Map(("target", "@resistance"), ("amount", 1L), ("when", "input.attribute == 'resolve'"));
        var chosen = Map(("target", "@resistance"), ("amount", 1L), ("optIn", Map(("label", "Against a supernatural threat"))));
        var resolver = new Entries(Ability("iron-will", always), Ability("anointed", chosen));
        var sheet = new Dictionary<string, object> { ["kind"] = "scoundrel", ["abilities"] = new List<object> { "iron-will", "anointed" } };

        var resolve = SheetRolls.Bonuses(Fitd, resolver, sheet, "resistance", new Dictionary<string, object?> { ["attribute"] = "resolve" });
        var counted = Assert.Single(resolve);
        Assert.Equal("iron-will", counted.Source);
        Assert.False(counted.Ticked);
        Assert.Null(counted.Id);

        // The tick box is offered beside it, and counted by neither.
        Assert.Equal("anointed#0", Assert.Single(SheetRolls.OptIns(Fitd, resolver, sheet, "resistance", new Dictionary<string, object?> { ["attribute"] = "resolve" })).Id);

        // Another input: the always-on bonus's when fails, so the list changes with the inputs.
        Assert.Empty(SheetRolls.Bonuses(Fitd, resolver, sheet, "resistance", new Dictionary<string, object?> { ["attribute"] = "insight" }));
        Assert.Empty(SheetRolls.Bonuses(Fitd, resolver, sheet, "group", new Dictionary<string, object?>()));
    }
}
