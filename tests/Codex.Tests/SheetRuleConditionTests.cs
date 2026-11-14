using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>C2 of docs/SHEET_CONDITIONS_DESIGN.md: what the conditions that hold add to a sheet-rules sheet. Their modifiers reach the roll sites
/// (attacks, saves, checks, skills), armor class, derived values and Number fields as a feature's do, name themselves after the condition, read
/// <c>value</c>, offer their tick boxes under <see cref="OptInIds.Condition"/>, and on a system with sheet kinds as well go to one side only.</summary>
public class SheetRuleConditionTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public SheetRuleConditionTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class Pack(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type);
    }

    private static ModifierDefinition Mod(string target, string amount, OptInDefinition? optIn = null, string? source = null) =>
        new() { Target = target, Amount = amount, OptIn = optIn, Source = source };

    private static ConditionEffectDefinition Effect(params ModifierDefinition[] modifiers) => new() { Modifiers = modifiers.ToList() };

    private static ConditionEffectDefinition Valued(params ModifierDefinition[] modifiers) =>
        new() { Modifiers = modifiers.ToList(), Value = new ConditionValueDefinition { Max = "6" } };

    private const string WearyKey = "condition_weary";

    private static ConditionEffectDefinition Weary() => Valued(Mod("attack", "-2 * value"), Mod("save", "-2 * value"), Mod("check", "-2 * value"), Mod("speed", "-5 * value"));

    /// <summary>The SRD system with <paramref name="effects"/> added, and a level 5 wizard with a longbow who is not carrying any condition yet.</summary>
    private (SheetRuleEngine Engine, SystemDefinition System, Dictionary<string, object> Sheet) Character(bool withClass, params (string Name, ConditionEffectDefinition Effect)[] effects)
    {
        var system = _loader.GetDefinition("srd5e")!;
        foreach (var (name, effect) in effects) system.ConditionEffects[name] = effect;
        var engine = new SheetRuleEngine(system, new Pack(_registry));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var attribute in new[] { "str", "dex", "con", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + attribute] = 10L;
        sheet[SheetRuleEngine.BasePrefix + "int"] = 16L;
        if (!withClass) return (engine, system, sheet);

        sheet["class"] = "wizard";
        sheet["species"] = "human";
        sheet["level"] = 5L;
        sheet["inventory"] = new List<object> { "longbow|equipped:1", "longsword|equipped:1" };
        sheet["cantrips"] = new List<object> { "fire-bolt" };
        return (engine, system, sheet);
    }

    private static SheetAction Action(SheetEvaluation evaluation, string id) => evaluation.Actions.Single(a => a.Entry.Id == id);

    private static void NoWarnings(SheetEvaluation evaluation) =>
        Assert.DoesNotContain(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError" || w.Key == "Actor.Rule.FormulaError");

    private static void Holding(Dictionary<string, object> sheet, params string[] conditions) =>
        sheet[SheetModel.ConditionsKey] = conditions.Cast<object>().ToList();

    [Fact]
    public void AFlatConditionModifier_LowersAnAttackBonus_AndIsNamedAfterTheCondition()
    {
        var (engine, _, sheet) = Character(true, ("Hexed", Effect(Mod("attack", "-2"), Mod("attack.weapon", "1d4", source: "Aura"), Mod("frobnicate", "5"))));
        var plain = engine.Recompute(sheet);
        Holding(sheet, "Hexed");

        var evaluation = engine.Recompute(sheet);

        var longbow = Action(evaluation, "longbow");
        Assert.Equal(Action(plain, "longbow").AttackBonus - 2, longbow.AttackBonus);
        Assert.Equal(new ActionBonus(SheetAction.AttackRoll, "Hexed", -2), Assert.Single(longbow.Bonuses));
        Assert.Equal(Action(plain, "fire-bolt").AttackBonus - 2, Action(evaluation, "fire-bolt").AttackBonus);
        Assert.Equal(Action(plain, "longbow").Damage, longbow.Damage);

        // A dice amount waits for the roll, credited to the modifier's own source; a target this side does not know is no warning.
        var site = engine.Bonuses(sheet, engine.AttackSite(longbow));
        Assert.Equal(-2, site.Number);
        Assert.Equal(new RollDice("Aura", "+1d4"), Assert.Single(site.Dice));
        Assert.Equal("Hexed", Assert.Single(site.Bonuses).Source);
        NoWarnings(evaluation);
    }

    [Fact]
    public void AValuedCondition_ReadsItsValue_OnEveryRollSiteAndOnANumberField()
    {
        var (engine, system, sheet) = Character(true, ("Weary", Weary()));
        var plain = engine.Recompute(sheet);

        sheet[WearyKey] = 2L;
        Assert.Equal(new[] { "Weary" }, SheetConditions.Of(system, sheet));
        var evaluation = engine.Recompute(sheet);

        Assert.Equal(Action(plain, "longbow").AttackBonus - 4, Action(evaluation, "longbow").AttackBonus);
        Assert.Equal(new ActionBonus(SheetAction.AttackRoll, "Weary", -4), Assert.Single(Action(evaluation, "longbow").Bonuses));
        Assert.Equal(plain.SavingThrows.Select(s => s.Bonus - 4), evaluation.SavingThrows.Select(s => s.Bonus));
        Assert.Equal(plain.Skills.Select(s => s.Bonus - 4), evaluation.Skills.Select(s => s.Bonus));
        var stealth = evaluation.Skills.Single(s => s.Key == "stealth");
        Assert.Equal(-4, stealth.Extra);
        Assert.Equal(new RollBonus(null, "Weary", "Weary", -4, false), Assert.Single(stealth.Bonuses));
        Assert.Equal(system.Attributes.Count, evaluation.CheckBonuses.Count);
        Assert.All(evaluation.CheckBonuses, c => Assert.Equal(-4, c.Value));

        // The Number field: what the sheet holds is lowered, the working names the condition, and a second pass does not lower it again.
        Assert.Equal(20, YamlValues.GetInt(sheet, "speed"));
        var granted = Assert.Single(evaluation.Granted, g => g.Target == "speed");
        Assert.Equal(("Weary", -10, true), (granted.Feature.Name, granted.Amount, granted.Counted));
        engine.Recompute(sheet);
        Assert.Equal(20, YamlValues.GetInt(sheet, "speed"));
        NoWarnings(evaluation);

        // At 0 the condition does not hold and nothing is added; the speed comes back.
        sheet[WearyKey] = 0L;
        Assert.Empty(SheetConditions.Of(system, sheet));
        var gone = engine.Recompute(sheet);
        Assert.Equal(Action(plain, "longbow").AttackBonus, Action(gone, "longbow").AttackBonus);
        Assert.Equal(plain.Skills.Select(s => s.Bonus), gone.Skills.Select(s => s.Bonus));
        Assert.Empty(gone.CheckBonuses);
        Assert.Equal(30, YamlValues.GetInt(sheet, "speed"));
        Assert.DoesNotContain(gone.Granted, g => g.Target == "speed");
    }

    [Fact]
    public void AValuedConditionTheSheetListsAtZero_DoesNotHold_OnEitherSide()
    {
        var (engine, system, sheet) = Character(true, ("Weary", Weary()));
        var plain = engine.Recompute(sheet);
        Holding(sheet, "Weary");
        sheet[WearyKey] = 0L;

        Assert.Empty(SheetConditions.Of(system, sheet));
        var evaluation = engine.Recompute(sheet);
        Assert.Equal(plain.Skills.Select(s => s.Bonus), evaluation.Skills.Select(s => s.Bonus));
        Assert.Equal(Action(plain, "longbow").AttackBonus, Action(evaluation, "longbow").AttackBonus);
        Assert.Empty(evaluation.CheckBonuses);
        Assert.Equal(30, YamlValues.GetInt(sheet, "speed"));

        sheet.Remove(WearyKey); // unset is the same as 0
        Assert.Empty(SheetConditions.Of(system, sheet));

        var hybrid = SystemDefinitionLoader.Parse(Hybrid);
        var kindSheet = new Dictionary<string, object> { [SheetModel.ConditionsKey] = new List<object> { "Frightened" }, ["condition_frightened"] = 0L };
        Assert.Empty(SheetConditions.Of(hybrid, kindSheet));
        Assert.Empty(new SheetModel(hybrid, new NoEntries(), kindSheet).Evaluate().Conditions);
    }

    [Fact]
    public void AConditionOnArmorClass_AndOnADerivedValue_IsCountedWithItsName()
    {
        var (engine, _, sheet) = Character(true, ("Dazed", Effect(Mod("armorClass", "-2"), Mod("proficiencyBonus", "-1"))));
        var plain = engine.Recompute(sheet);
        Holding(sheet, "Dazed");

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(plain.ArmorClass!.Total - 2, evaluation.ArmorClass!.Total);
        Assert.Equal(Convert.ToInt64(plain.Derived["proficiencyBonus"]) - 1, Convert.ToInt64(evaluation.Derived["proficiencyBonus"]));
        Assert.Contains(evaluation.Granted, g => g.Target == "proficiencyBonus" && g.Feature.Name == "Dazed" && g.Amount == -1 && g.Counted);
        NoWarnings(evaluation);
    }

    [Fact]
    public void ASheetWithAConditionAndNoFeatures_StillGetsTheModifiers()
    {
        var (engine, _, sheet) = Character(false, ("Weary", Weary()));
        var plain = engine.Recompute(sheet);
        Assert.Empty(plain.Features);
        sheet[WearyKey] = 1L;

        var evaluation = engine.Recompute(sheet);

        Assert.Empty(evaluation.Features);
        Assert.Equal(plain.Skills.Select(s => s.Bonus - 2), evaluation.Skills.Select(s => s.Bonus));
        Assert.Equal(plain.SavingThrows.Select(s => s.Bonus - 2), evaluation.SavingThrows.Select(s => s.Bonus));
        Assert.NotEmpty(evaluation.CheckBonuses);
        Assert.All(evaluation.CheckBonuses, c => Assert.Equal(-2, c.Value));
        Assert.Equal("Weary", Assert.Single(evaluation.CheckSources["dex"]).Source);
        Assert.Equal(25, YamlValues.GetInt(sheet, "speed"));
    }

    [Fact]
    public void AnOptInOnACondition_IsOfferedUnderTheConditionsId_AndItsEndsReachesTheOffer()
    {
        var guidance = new OptInDefinition { Label = "Guidance", Ends = true };
        var (engine, _, sheet) = Character(true, ("Guided", Effect(Mod("attack", "1"), Mod("check", "1d4", guidance))));
        Holding(sheet, "Guided");
        var evaluation = engine.Recompute(sheet);

        var offered = Assert.Single(engine.OptIns(sheet, engine.SkillSite("stealth")));

        Assert.Equal(new RollOptIn(OptInIds.Condition("Guided", 1), "Guidance", "+1d4", "Guided", null, true, "Guided", "check"), offered);
        Assert.Empty(engine.OptIns(sheet, engine.SaveSite("dex")));
        Assert.Equal(offered, Assert.Single(engine.AllOptIns(sheet)));

        // A tick box never counts on its own: neither the dice nor a number are in the always-on bonus.
        Assert.Empty(engine.Bonuses(sheet, engine.SkillSite("stealth")).Dice);
        Assert.Equal(engine.Recompute(new Dictionary<string, object>(sheet) { [SheetModel.ConditionsKey] = new List<object>() }).Skills.Select(s => s.Bonus), evaluation.Skills.Select(s => s.Bonus));

        // Without ends the offer names no condition to end.
        var (plainEngine, _, plainSheet) = Character(true, ("Guided", Effect(Mod("check", "1d4", new OptInDefinition { Label = "Guidance" }))));
        Holding(plainSheet, "Guided");
        Assert.Null(Assert.Single(plainEngine.OptIns(plainSheet, plainEngine.CheckSite("dex"))).Ends);
    }

    [Fact]
    public void AnOptInOnAValuedCondition_ReadsTheValue()
    {
        var (engine, _, sheet) = Character(true, ("Inspired", Valued(Mod("check", "value", new OptInDefinition { Label = "Inspiration" }))));
        sheet["condition_inspired"] = 3L;
        engine.Recompute(sheet);

        var offered = Assert.Single(engine.OptIns(sheet, engine.SkillSite("stealth")));

        Assert.Equal("+3", offered.Amount);
        Assert.Equal(OptInIds.Condition("Inspired", 0), offered.Id);
    }

    private const string Hybrid = """
        id: hybrid-test
        name: Hybrid test
        attributes:
          - { key: dex, label: Dexterity, abbreviation: DEX, default: 10, modifier: half-minus-five }
        actorFields:
          - { key: level, label: Level, type: Number, defaultValue: 1 }
        conditions: [Frightened]
        conditionEffects:
          Frightened:
            value: { max: 4 }
            modifiers:
              - { target: check,    amount: "-value" }
              - { target: classDc,  amount: "-value" }
              - { target: will,     amount: "-value" }
        sheetRules:
          derived:
            - { key: proficiencyBonus, label: Proficiency, formula: "2" }
            - { key: classDc, label: Class DC, formula: "10 + mod(dex)" }
          skills:
            label: Skills
            bonusKey: proficiencyBonus
            list:
              - { key: stealth, label: Stealth, attribute: dex }
        sheetKinds:
          - kind: hero
            label: Hero
            values:
              - { key: will, formula: "10", modifiers: will }
        """;

    private sealed class NoEntries : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => null;
        public IEnumerable<ContentEntry> Candidates(string type) => Enumerable.Empty<ContentEntry>();
    }

    [Fact]
    public void OnASystemWithBothSides_ACondition_GoesToTheSideThatKnowsItsTarget_OnceOnly()
    {
        var system = SystemDefinitionLoader.Parse(Hybrid);
        var engine = new SheetRuleEngine(system, new NoEntries());
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        var plain = engine.Recompute(sheet);
        Assert.Equal(10, Convert.ToInt64(plain.Derived["classDc"]));
        Assert.Equal(10d, plain.Model!.Values["will"]);

        sheet["condition_frightened"] = 2L;
        var evaluation = engine.Recompute(sheet);

        // The sheet-rules side took the check and the derived value; the sheet kind took its own value; none took another's.
        Assert.Equal(8, Convert.ToInt64(evaluation.Derived["classDc"]));
        Assert.Equal(plain.Skills.Single().Bonus - 2, evaluation.Skills.Single().Bonus);
        Assert.Equal(-2, evaluation.CheckBonuses["dex"]);
        Assert.Equal(8d, evaluation.Model!.Values["will"]);
        Assert.Equal(new[] { "Frightened" }, evaluation.Granted.Where(g => g.Target == "classDc").Select(g => g.Feature.Name));
        NoWarnings(evaluation);
    }

    [Fact]
    public void ATargetBothSidesClaim_IsALoadError()
    {
        var yaml = Hybrid.Replace("- { key: will, formula: \"10\", modifiers: will }", "- { key: classDc, formula: \"10\", modifiers: classDc }");

        var errors = SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(yaml)));

        Assert.Contains(errors, e => e.Contains("conditionEffects.Frightened.modifiers[1]") && e.Contains("'classDc'") && e.Contains("both sheetRules and a sheet kind"));
        Assert.DoesNotContain(errors, e => e.Contains("modifiers[0]"));
    }

    [Fact]
    public void ASystemWithOnlyOneSide_HasNoRoutingToCheck()
    {
        var rulesOnly = Hybrid.Replace("""
            sheetKinds:
              - kind: hero
                label: Hero
                values:
                  - { key: will, formula: "10", modifiers: will }
            """, string.Empty);
        Assert.DoesNotContain("sheetKinds", rulesOnly);

        var system = SystemDefinitionLoader.Parse(rulesOnly);

        Assert.Single(system.ConditionEffects);
    }
}
