using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>What features add to the rolls of a sheet-rules system (docs/ROLL_BONUSES_DESIGN.md, RB1b), on the real SRD system: the roll targets an
/// attack, a damage roll, a save and a check read; the always-on numbers that land in the attack bonus, the damage, the skill, save and check
/// numbers; the dice that wait for the roll; and the opt-ins a roll site offers.</summary>
public class RollSiteBonusTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public RollSiteBonusTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

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
        entry.Properties["features"] = Items(new Dictionary<string, object>
        {
            ["name"] = feature,
            ["level"] = 3L,
            ["description"] = feature + ".",
            ["modifiers"] = modifiers.Cast<object>().ToList()
        });
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

    private static void NoWarnings(SheetEvaluation evaluation) => Assert.DoesNotContain(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError" || w.Key == "Actor.Rule.FormulaError");

    private SheetEvaluation Baseline() => Evaluate(Build("Nothing"));

    private static SheetEvaluation Evaluate((SheetRuleEngine Engine, Dictionary<string, object> Sheet) built) => built.Engine.Recompute(built.Sheet);

    [Fact]
    public void ARangedWeaponAttackBonus_IsAddedToTheLongbow_AndNothingElse()
    {
        var plain = Baseline();
        var built = Build("Archery", Mod("attack.weapon.ranged", 2L));

        var evaluation = Evaluate(built);

        var longbow = Action(evaluation, "longbow");
        Assert.Equal(Action(plain, "longbow").AttackBonus + 2, longbow.AttackBonus);
        Assert.Equal(Action(plain, "longsword").AttackBonus, Action(evaluation, "longsword").AttackBonus);
        Assert.Equal(Action(plain, "fire-bolt").AttackBonus, Action(evaluation, "fire-bolt").AttackBonus); // a ranged spell attack is not a ranged weapon attack
        Assert.Equal(new ActionBonus(SheetAction.AttackRoll, "Archery", 2), Assert.Single(longbow.Bonuses));
        Assert.Empty(Action(evaluation, "longsword").Bonuses);
        Assert.Equal(Action(plain, "longbow").Damage, longbow.Damage);
        NoWarnings(evaluation);

        // The roll site tells what read it: attack, weapon, ranged, both, and the entry.
        Assert.Equal(new[] { "attack", "attack.weapon", "attack.ranged", "attack.weapon.ranged", "attack.entry.longbow" }, built.Engine.AttackSite(longbow).Targets);
        Assert.Equal(new[] { "attack", "attack.spell", "attack.ranged", "attack.spell.ranged", "attack.entry.fire-bolt" }, built.Engine.AttackSite(Action(evaluation, "fire-bolt")).Targets);
    }

    [Fact]
    public void AnEntrysOwnAttackTarget_AndTheMeleeRange_AreReadByThatActionOnly()
    {
        var plain = Baseline();
        var evaluation = Evaluate(Build("Sword training", Mod("attack.entry.longsword", 1L), Mod("attack.melee", 1L)));

        Assert.Equal(Action(plain, "longsword").AttackBonus + 2, Action(evaluation, "longsword").AttackBonus);
        Assert.Equal(Action(plain, "longbow").AttackBonus, Action(evaluation, "longbow").AttackBonus);
        Assert.Equal(Action(plain, "fire-bolt").AttackBonus, Action(evaluation, "fire-bolt").AttackBonus);
        NoWarnings(evaluation);
    }

    [Fact]
    public void ADamageTarget_IsAddedToAWeaponsDamage_AndToEachSlotsDamage_ButNotToTheOtherKind()
    {
        var plain = Baseline();
        Assert.Equal("1d8", Action(plain, "longbow").Damage); // every modifier is 0: the dice as they are
        var evaluation = Evaluate(Build("Heavy arrows", Mod("damage.weapon", 1L), Mod("damage.entry.burning-hands", 2L)));

        Assert.Equal("1d8+1", Action(evaluation, "longbow").Damage);
        Assert.Equal("1d8+1", Action(evaluation, "longsword").Damage);
        Assert.Equal(Action(plain, "fire-bolt").Damage, Action(evaluation, "fire-bolt").Damage);
        var hands = Action(evaluation, "burning-hands");
        Assert.Equal(Action(plain, "burning-hands").Costs.Select(c => c.Damage + "+2"), hands.Costs.Select(c => c.Damage)); // each slot level carries it
        Assert.Contains(Action(evaluation, "longbow").Bonuses, b => b == new ActionBonus(SheetAction.DamageRoll, "Heavy arrows", 1));
        NoWarnings(evaluation);
    }

    [Fact]
    public void ASkillCheckBonus_RaisesThatSkillOnly_ASaveBonus_ThatSaveOnly()
    {
        var plain = Baseline();

        var stealth = Evaluate(Build("Shadow", Mod("check.skill.stealth", 1L)));
        Assert.Equal(plain.Skills.Single(s => s.Key == "stealth").Bonus + 1, stealth.Skills.Single(s => s.Key == "stealth").Bonus);
        Assert.Equal(1, stealth.Skills.Single(s => s.Key == "stealth").Extra);
        foreach (var skill in plain.Skills.Where(s => s.Key != "stealth")) Assert.Equal(skill.Bonus, stealth.Skills.Single(s => s.Key == skill.Key).Bonus);
        Assert.Equal(plain.SavingThrows.Select(s => s.Bonus), stealth.SavingThrows.Select(s => s.Bonus));
        Assert.Empty(stealth.CheckBonuses);
        Assert.Equal("Shadow", Assert.Single(stealth.Skills.Single(s => s.Key == "stealth").Bonuses).Source);
        NoWarnings(stealth);

        var save = Evaluate(Build("Nimble", Mod("save.dex", 1L)));
        Assert.Equal(plain.SavingThrows.Single(s => s.Key == "dex").Bonus + 1, save.SavingThrows.Single(s => s.Key == "dex").Bonus);
        foreach (var other in plain.SavingThrows.Where(s => s.Key != "dex")) Assert.Equal(other.Bonus, save.SavingThrows.Single(s => s.Key == other.Key).Bonus);
        Assert.Equal(plain.Skills.Select(s => s.Bonus), save.Skills.Select(s => s.Bonus));
        NoWarnings(save);
    }

    [Fact]
    public void AnAttributeCheckBonus_ReachesItsSkills_AndTheAttributesOwnCheck_AndAPlainCheckBonusReachesAll()
    {
        var plain = Baseline();

        var dex = Evaluate(Build("Poise", Mod("check.dex", 1L)));
        Assert.Equal(1, dex.CheckBonuses["dex"]);
        Assert.Single(dex.CheckBonuses);
        foreach (var skill in plain.Skills)
        {
            Assert.Equal(skill.Bonus + (skill.Attribute == "dex" ? 1 : 0), dex.Skills.Single(s => s.Key == skill.Key).Bonus);
        }

        Assert.Equal(plain.SavingThrows.Select(s => s.Bonus), dex.SavingThrows.Select(s => s.Bonus)); // a save is not a check

        var all = Evaluate(Build("Focus", Mod("check", 2L)));
        Assert.Equal(plain.Skills.Select(s => s.Bonus + 2), all.Skills.Select(s => s.Bonus));
        Assert.Equal(plain.SavingThrows.Select(s => s.Bonus), all.SavingThrows.Select(s => s.Bonus));
        Assert.All(all.CheckBonuses, c => Assert.Equal(2, c.Value));
        Assert.Equal(6, all.CheckBonuses.Count);
        NoWarnings(all);
    }

    [Fact]
    public void ADiceAmountOnAttack_IsExposedForTheAttackSite_AndIsNotInTheAttackBonus()
    {
        var plain = Baseline();
        var built = Build("Blessed blade", Mod("attack", "1d4"), Mod("check", "-1d4"));

        var evaluation = Evaluate(built);

        var longbow = Action(evaluation, "longbow");
        Assert.Equal(Action(plain, "longbow").AttackBonus, longbow.AttackBonus);
        Assert.Empty(longbow.Bonuses);
        var attack = built.Engine.Bonuses(built.Sheet, built.Engine.AttackSite(longbow));
        Assert.Equal(new RollDice("Blessed blade", "+1d4"), Assert.Single(attack.Dice));
        Assert.Equal(0, attack.Number);
        Assert.Equal(new RollDice("Blessed blade", "+1d4"), Assert.Single(built.Engine.Bonuses(built.Sheet, built.Engine.SpellAttackSite()).Dice));
        Assert.Empty(built.Engine.Bonuses(built.Sheet, built.Engine.DamageSite(longbow)).Dice);
        Assert.Empty(built.Engine.Bonuses(built.Sheet, built.Engine.SaveSite("dex")).Dice);

        // A penalty die keeps its sign; the skill's number is untouched.
        Assert.Equal(new RollDice("Blessed blade", "-1d4"), Assert.Single(built.Engine.Bonuses(built.Sheet, built.Engine.SkillSite("stealth")).Dice));
        Assert.Equal(plain.Skills.Select(s => s.Bonus), evaluation.Skills.Select(s => s.Bonus));
        NoWarnings(evaluation); // a dice amount is no formula to fail
    }

    [Fact]
    public void AnOptInOnCheck_IsOfferedForASkillCheck_NotForASave_AndNeverCountsOnTheSheet()
    {
        var plain = Baseline();
        var built = Build("Lucky charm", OptIn("check", "1d6", "Lucky"), OptIn("attack.weapon", 1L, "Sacred"));

        var evaluation = Evaluate(built);

        var stealth = Assert.Single(built.Engine.OptIns(built.Sheet, built.Engine.SkillSite("stealth")));
        Assert.Equal(new RollOptIn("tester#0", "Lucky", "+1d6", "Lucky charm", null, true, null), stealth);
        Assert.Single(built.Engine.OptIns(built.Sheet, built.Engine.CheckSite("dex")));
        Assert.Empty(built.Engine.OptIns(built.Sheet, built.Engine.SaveSite("dex")));

        // The second modifier of the entry is id #1, whatever else is listed; it is offered on weapon attacks only.
        var longbow = Action(evaluation, "longbow");
        Assert.Equal("tester#1", Assert.Single(built.Engine.OptIns(built.Sheet, built.Engine.AttackSite(longbow))).Id);
        Assert.Empty(built.Engine.OptIns(built.Sheet, built.Engine.AttackSite(Action(evaluation, "fire-bolt"))));

        // Opt-ins never count in the always-on numbers, or in the dice that wait for a roll.
        Assert.Equal(Action(plain, "longbow").AttackBonus, longbow.AttackBonus);
        Assert.Empty(longbow.Bonuses);
        Assert.Equal(plain.Skills.Select(s => s.Bonus), evaluation.Skills.Select(s => s.Bonus));
        Assert.Empty(evaluation.CheckBonuses);
        Assert.Empty(built.Engine.Bonuses(built.Sheet, built.Engine.SkillSite("stealth")).Dice);
        NoWarnings(evaluation);
    }

    [Fact]
    public void AnOptInThatSpends_IsOfferedAsPayableOnlyWhileThePoolHoldsEnough()
    {
        var built = Build("Lucky charm", OptIn("check", "1d6", "Lucky", Map(("key", "lucky"), ("amount", 1L))));
        var site = built.Engine.SkillSite("stealth");

        Assert.False(Assert.Single(built.Engine.OptIns(built.Sheet, site)).CanPay); // the sheet has no such pool

        built.Sheet["uses_lucky"] = 1L;
        var offered = Assert.Single(built.Engine.OptIns(built.Sheet, site));
        Assert.True(offered.CanPay);
        Assert.Equal(new RollOptInSpend("uses_lucky", 1), offered.Spends);

        built.Sheet["uses_lucky"] = 0L;
        Assert.False(Assert.Single(built.Engine.OptIns(built.Sheet, site)).CanPay);
    }

    [Fact]
    public void TheRollTargetNames_AreKnown_AndOnlyTheNamesTheSystemHasAre()
    {
        var system = _loader.GetDefinition("srd5e")!;
        var names = new[]
        {
            "attack", "attack.weapon", "attack.spell", "attack.melee", "attack.ranged", "attack.weapon.melee", "attack.weapon.ranged", "attack.spell.melee", "attack.spell.ranged",
            "attack.entry.longbow", "damage", "damage.weapon", "damage.spell", "damage.melee", "damage.ranged", "damage.entry.fire-bolt", "save", "save.dex", "check", "check.int",
            "check.skill.stealth"
        };
        Assert.All(names, name => Assert.True(RollSiteTargets.IsRollTarget(system, name), name));
        Assert.All(new[] { "save.nope", "check.nope", "check.skill.nope", "attack.entry", "attack.entry.", "attack.nope", "darkvision", "" }, name => Assert.False(RollSiteTargets.IsRollTarget(system, name), name));

        var built = Build("Everything", names.Select(n => Mod(n, 1L)).ToArray());
        NoWarnings(Evaluate(built)); // none is an unknown target

        var unknown = Evaluate(Build("Odd", Mod("check.skill.nope", 1L)));
        Assert.Single(unknown.Warnings, w => w.Key == "Actor.Rule.FeatureError");

        var targets = FeatureGrantVocabulary.For(system)!.Targets.Select(t => t.Key).ToList();
        Assert.Equal(targets.Count, targets.Distinct().Count());
        Assert.Subset(targets.ToHashSet(), new[] { "attack.weapon.ranged", "damage.spell", "save.dex", "check.dex", "check.skill.stealth", "darkvision" }.ToHashSet());
        Assert.DoesNotContain(targets, t => t.Contains(".entry."));
    }

    [Fact]
    public void TheListOfWhatCounted_LeavesOutTheTypesAValueLeavesOut_AsItsTotalDoes()
    {
        var system = SystemDefinitionLoader.Parse("""
            id: left-types
            name: Left types
            sheetKinds:
              - kind: hero
                label: Hero
                modifiers:
                  - { target: '@x', amount: 1, type: armor, source: Plate }
                  - { target: '@x', amount: 2, source: Training }
            """);
        var model = new SheetModel(system, NullEntryResolver.Instance, new Dictionary<string, object> { ["kind"] = "hero" });

        var all = model.Bonuses(new[] { "@x" });
        Assert.Equal(new[] { "Plate", "Training" }, all.Select(b => b.Source));

        // The total of a value that leaves out the armor type does not count Plate, so the list may not say it did.
        var without = model.Bonuses(new[] { "@x" }, new List<object> { "armor" });
        Assert.Equal("Training", Assert.Single(without).Source);
    }

    private sealed class WithHomebrew(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }
}
