using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>C5 of docs/SHEET_CONDITIONS_DESIGN.md: the srd5e system's own condition data, on the real shipped system. Exhaustion (SRD 5.2.1: -2 times the
/// level to a D20 Test, -5 feet of Speed times it, one level off per Long Rest), and the spell effects Bless, Bane and Guided.</summary>
public class Srd5eConditionDataTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public Srd5eConditionDataTests()
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

    private (SheetRuleEngine Engine, SystemDefinition System, Dictionary<string, object> Sheet) Character()
    {
        var system = _loader.GetDefinition("srd5e")!;
        var engine = new SheetRuleEngine(system, new Pack(_registry));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var attribute in new[] { "str", "dex", "con", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + attribute] = 10L;
        sheet[SheetRuleEngine.BasePrefix + "int"] = 16L;
        sheet["class"] = "wizard";
        sheet["species"] = "human";
        sheet["level"] = 5L;
        sheet["inventory"] = new List<object> { "longbow|equipped:1" };
        sheet["cantrips"] = new List<object> { "fire-bolt" };
        return (engine, system, sheet);
    }

    private static SheetAction Longbow(SheetEvaluation evaluation) => evaluation.Actions.Single(a => a.Entry.Id == "longbow");

    private static void Holding(Dictionary<string, object> sheet, params string[] conditions) =>
        sheet[SheetModel.ConditionsKey] = conditions.Cast<object>().ToList();

    private static void NoWarnings(SheetEvaluation evaluation) =>
        Assert.DoesNotContain(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError" || w.Key == "Actor.Rule.FormulaError");

    // ---- Loading ----

    [Theory]
    [InlineData("")]
    [InlineData("ru")]
    [InlineData("he")]
    public void TheSystemLoadsWithNoErrors_InEveryLanguage(string culture)
    {
        var path = Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml");
        var system = SystemDefinitionLoader.Load(path); // throws on any load error, the condition checks among them
        Assert.Empty(SystemDefinitionLoader.Validate(system));
        Assert.Equal(new[] { "Bless", "Bane", "Guided" }, system.Effects);
        Assert.All(new[] { "Exhaustion", "Bless", "Bane", "Guided" }, name => Assert.True(system.ConditionEffects.ContainsKey(name), name));

        var loaded = _loader.GetDefinition("srd5e")!;
        Assert.Equal(new[] { "Bless", "Bane", "Guided" }, loaded.Effects);
        Assert.Empty(SystemDefinitionLoader.Validate(loaded.Localize(culture)));
    }

    [Theory]
    [InlineData("ru", "Благословение", "Порча", "Под Указанием", "Истощение")]
    [InlineData("he", "ברכה", "מארה", "מונחה", "תשישות")]
    public void TheSpellEffects_AreTranslatedLikeConditions(string culture, string bless, string bane, string guided, string exhaustion)
    {
        var system = _loader.GetDefinition("srd5e")!;
        Assert.Equal(bless, system.DisplayCondition(culture, "Bless"));
        Assert.Equal(bane, system.DisplayCondition(culture, "Bane"));
        Assert.Equal(guided, system.DisplayCondition(culture, "Guided"));
        Assert.Equal("Bless", system.DisplayCondition("en", "Bless"));

        // A roll names them in the reader's language too: the condition names and the tick box's label.
        var names = RollBonusNames.For(_registry, system, culture);
        Assert.Equal(bless, names.Source("Bless"));
        Assert.Equal(guided, names.Source("Guided"));
        Assert.Equal(exhaustion, names.Source("Exhaustion"));
        Assert.NotEqual("Guidance (the chosen skill)", names.Label("Guidance (the chosen skill)", "Guided"));
        Assert.Equal("Guidance (the chosen skill)", RollBonusNames.English.Label("Guidance (the chosen skill)", "Guided"));
    }

    [Fact]
    public void TheLongRest_NoLongerLowersExhaustionItself_TheEffectDoes()
    {
        var system = _loader.GetDefinition("srd5e")!;
        Assert.All(system.Vitals!.Rests, rest => Assert.Equal(0, rest.ExhaustionDelta));
        var decrease = system.ConditionEffects["Exhaustion"].Value!.Decrease!;
        Assert.Equal(("Long Rest", 1), (decrease.On, decrease.By));
        Assert.Equal("Exhaustion", system.ConditionEffects["Exhaustion"].Value!.Key);
        Assert.Equal("6", system.ConditionEffects["Exhaustion"].Value!.Max);
    }

    // ---- Exhaustion ----

    [Fact]
    public void ExhaustionLevel2_LowersEveryD20TestAndInitiativeByFour_AndSpeedByTen()
    {
        var (engine, system, sheet) = Character();
        var plain = engine.Recompute(sheet);
        var plainSpeed = YamlValues.GetInt(sheet, "speed");
        sheet["Exhaustion"] = 2L;
        Assert.Equal(new[] { "Exhaustion" }, SheetConditions.Of(system, sheet));

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(Longbow(plain).AttackBonus - 4, Longbow(evaluation).AttackBonus);
        Assert.Equal(new ActionBonus(SheetAction.AttackRoll, "Exhaustion", -4), Assert.Single(Longbow(evaluation).Bonuses));
        Assert.Equal(plain.SavingThrows.Select(s => s.Bonus - 4), evaluation.SavingThrows.Select(s => s.Bonus));
        Assert.Equal(plain.Skills.Select(s => s.Bonus - 4), evaluation.Skills.Select(s => s.Bonus));
        Assert.Equal(system.Attributes.Count, evaluation.CheckBonuses.Count);
        Assert.All(evaluation.CheckBonuses, c => Assert.Equal(-4, c.Value)); // an ability check
        Assert.Equal(Convert.ToInt64(plain.Derived["initiative"]) - 4, Convert.ToInt64(evaluation.Derived["initiative"]));
        Assert.Contains(evaluation.Granted, g => g.Target == "initiative" && g.Feature.Name == "Exhaustion" && g.Amount == -4 && g.Counted);
        Assert.Equal(plainSpeed - 10, YamlValues.GetInt(sheet, "speed"));
        NoWarnings(evaluation);

        // The roll sites say it as the roll will: a skill check and an ability check read the same number, named.
        Assert.Equal(-4, engine.Bonuses(sheet, engine.SkillSite("stealth")).Number);
        Assert.Equal("Exhaustion", Assert.Single(engine.Bonuses(sheet, engine.CheckSite("dex")).Bonuses).Source);
        Assert.Equal(-4, engine.Bonuses(sheet, engine.SaveSite("con")).Number);

        // A second pass does not lower the speed again, and the level coming off gives it back.
        engine.Recompute(sheet);
        Assert.Equal(plainSpeed - 10, YamlValues.GetInt(sheet, "speed"));
        sheet["Exhaustion"] = 0L;
        var gone = engine.Recompute(sheet);
        Assert.Equal(plainSpeed, YamlValues.GetInt(sheet, "speed"));
        Assert.Equal(Longbow(plain).AttackBonus, Longbow(gone).AttackBonus);
        Assert.Empty(gone.CheckBonuses);
    }

    [Fact]
    public void Initiative_IsTheDerivedValue_NotARollTarget()
    {
        var (engine, system, _) = Character();
        Assert.False(RollSiteTargets.IsRollTarget(system, "initiative"));
        Assert.Equal(SheetRuleEngine.TargetKind.Derived, SheetRuleEngine.KindOf(system, "initiative"));
        Assert.Equal(SheetRuleEngine.TargetKind.Field, SheetRuleEngine.KindOf(system, "speed"));
        Assert.NotNull(engine);
    }

    // ---- Bless, Bane, Guided ----

    [Fact]
    public void Bless_AddsADieToAnAttackAndASave_AndIsNamedOnTheRollCard()
    {
        var (engine, _, sheet) = Character();
        var plain = engine.Recompute(sheet);
        Holding(sheet, "Bless");
        var evaluation = engine.Recompute(sheet);
        var attack = engine.AttackSite(Longbow(evaluation));

        // The die waits for the roll: no number moved, and it names Bless.
        Assert.Equal(Longbow(plain).AttackBonus, Longbow(evaluation).AttackBonus);
        Assert.Equal(new RollDice("Bless", "+1d4"), Assert.Single(engine.Bonuses(sheet, attack).Dice));
        Assert.Equal(new RollDice("Bless", "+1d4"), Assert.Single(engine.Bonuses(sheet, engine.SaveSite("wis")).Dice));
        Assert.Empty(engine.Bonuses(sheet, engine.SkillSite("stealth")).Dice); // not a check
        NoWarnings(evaluation);

        // As the host builds the roll: the die is on the d20's expression, and the label says where it came from.
        var built = new RollBonusTray().Build(engine, sheet, attack, "1d20+5", "Longbow attack");
        Assert.Equal("1d20+5+1d4", built.Expression);
        Assert.Equal("Longbow attack (+1d4 Bless)", built.Label);
        var save = new RollBonusTray().Build(engine, sheet, engine.SaveSite("wis"), "1d20", "Wisdom save");
        Assert.Equal("1d20+1d4", save.Expression);
        Assert.Equal("Wisdom save (+1d4 Bless)", save.Label);
    }

    [Fact]
    public void Bane_SubtractsADieFromAnAttackAndASave()
    {
        var (engine, _, sheet) = Character();
        Holding(sheet, "Bane");
        var evaluation = engine.Recompute(sheet);
        var attack = engine.AttackSite(Longbow(evaluation));

        Assert.Equal(new RollDice("Bane", "-1d4"), Assert.Single(engine.Bonuses(sheet, attack).Dice));
        Assert.Equal(new RollDice("Bane", "-1d4"), Assert.Single(engine.Bonuses(sheet, engine.SaveSite("dex")).Dice));
        Assert.Equal("1d20+5-1d4", new RollBonusTray().Build(engine, sheet, attack, "1d20+5", "Longbow attack").Expression);
        Assert.Equal("Longbow attack (-1d4 Bane)", new RollBonusTray().Build(engine, sheet, attack, "1d20+5", "Longbow attack").Label);
    }

    [Fact]
    public void BlessAndBaneTogether_AddTheirDiceSeparately()
    {
        var (engine, _, sheet) = Character();
        Holding(sheet, "Bless", "Bane");
        var evaluation = engine.Recompute(sheet);

        var dice = engine.Bonuses(sheet, engine.AttackSite(Longbow(evaluation))).Dice;

        Assert.Equal(new[] { "+1d4", "-1d4" }, dice.Select(d => d.Dice));
    }

    [Fact]
    public void Guided_IsATickBoxOnEveryCheck_AddsItsDieWhenTicked_AndIsNotUsedUpByTheRoll()
    {
        var (engine, system, sheet) = Character();
        Holding(sheet, "Guided");
        engine.Recompute(sheet);

        var offered = Assert.Single(engine.OptIns(sheet, engine.SkillSite("stealth")));
        Assert.Equal(OptInIds.Condition("Guided", 0), offered.Id);
        Assert.Equal("Guidance (the chosen skill)", offered.Label);
        Assert.Equal("+1d4", offered.Amount);
        Assert.Equal("Guided", offered.Source);
        Assert.Null(offered.Ends); // a roll does not use Guidance up
        Assert.Single(engine.OptIns(sheet, engine.CheckSite("dex")));
        Assert.Empty(engine.OptIns(sheet, engine.SaveSite("dex")));
        Assert.Empty(engine.OptIns(sheet, engine.AttackSite(Longbow(engine.Recompute(sheet)))));

        // Unticked it adds nothing; ticked it adds the die to the check, and rolling does not take Guided off.
        var tray = new RollBonusTray();
        var stealth = engine.SkillSite("stealth");
        Assert.Equal("1d20+3", tray.Build(engine, sheet, stealth, "1d20+3", "Stealth check").Expression);
        tray.Set(offered.Id, true);
        var built = tray.Build(engine, sheet, stealth, "1d20+3", "Stealth check");
        Assert.Equal("1d20+3+1d4", built.Expression);
        Assert.Equal("Stealth check (+1d4 Guidance (the chosen skill))", built.Label);
        Assert.Null(Assert.Single(built.Applied).Ends);
        tray.Used(built.Applied);
        Assert.Equal(new[] { "Guided" }, SheetConditions.Of(system, sheet));
        Assert.Single(engine.OptIns(sheet, stealth)); // still on offer for the next check
    }

    // ---- The `ends` path stays reachable ----

    [Fact]
    public void TheEndsPathOfAnOptIn_IsStillCoveredByItsOwnFixture()
    {
        // srd5e's Guided has no `ends` (SRD 5.2.1 Guidance lasts for the whole spell). The path itself is covered on the Forged in the Dark system by
        // RollOptInTests.ATickedEnds_RemovesTheCondition_ThroughAnIntent, and on a sheet-rules sheet by
        // SheetRuleConditionTests.AnOptInOnACondition_IsOfferedUnderTheConditionsId_AndItsEndsReachesTheOffer. Here: an `ends` written on srd5e is not refused.
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        system.ConditionEffects["Guided"].Modifiers[0].OptIn!.Ends = true;
        Assert.Empty(SystemDefinitionLoader.Validate(system));
    }
}

/// <summary>The real srd5e Exhaustion at the table (docs/SHEET_CONDITIONS_DESIGN.md, "Hosts"): a character's table card attack bonus drops while exhausted,
/// a Long Rest takes one level off (not two), and Bless as a table status adds its die. On the shipped system, with nothing injected.</summary>
[Collection("RavenDb")]
public class Srd5eConditionTableTests : IClassFixture<RavenDbFixture>, IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly ComponentRegistry _components;
    private SystemDefinition _system = null!;
    private SheetRuleEngine _engine = null!;

    public Srd5eConditionTableTests(RavenDbFixture fixture)
    {
        _components = new ComponentRegistry();
        _components.Register<ResourcePoolComponent>();
        _components.Register<ActiveEffectsComponent>();
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
        _dbService = new RavenDbService(fixture.DbPath, "Srd5eConditions_" + Guid.NewGuid(), runInMemory: true);
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition("srd5e")!;
        _engine = new SheetRuleEngine(_system, new Pack(_registry));
    }

    public Task DisposeAsync()
    {
        _dbService.Dispose();
        return Task.CompletedTask;
    }

    private sealed class Pack(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type);
    }

    private async Task<(CampaignRuntime Runtime, ActorDocument Hero)> TableAsync()
    {
        var campaignId = Guid.NewGuid().ToString();
        var sheet = new Dictionary<string, object>();
        _engine.InitializeSheet(sheet);
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var attribute in new[] { "str", "dex", "con", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + attribute] = 10L;
        sheet[SheetRuleEngine.BasePrefix + "int"] = 16L;
        sheet["class"] = "wizard";
        sheet["species"] = "human";
        sheet["level"] = 5L;
        sheet["inventory"] = new List<object> { "longbow|equipped:1" };
        _engine.Recompute(sheet);

        var hero = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Vex", Kind = ActorKind.PlayerCharacter, Sheet = sheet };
        ActorSheetSync.ApplySheetToState(hero, _system, _components);
        await _actors.SaveAsync(hero);

        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _components, NullLogger.Instance, _sessions, session, system: _system) { Resolver = new Pack(_registry) };
        runtime.Hydrate(new[] { hero });
        return (runtime, hero);
    }

    private int LongbowAttack(IReadOnlyDictionary<string, object> sheet) => _engine.Evaluate(sheet).Actions.Single(a => a.Entry.Id == "longbow").AttackBonus!.Value;

    private Dictionary<string, object> WebView(CampaignRuntime runtime, ActorDocument hero)
    {
        var state = runtime.Snapshot.Actor(hero.Id)!;
        return SheetConditions.LiveView(_system, hero.Sheet, state.Pools, state.Effects.Select(e => e.EffectId));
    }

    [Fact]
    public async Task ATableCardsAttackBonus_DropsWhileTheCharacterIsExhausted_Async()
    {
        var (runtime, hero) = await TableAsync();
        await using var _ = runtime;
        var plain = LongbowAttack(hero.Sheet);

        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Exhaustion", 2));

        Assert.Equal(plain - 4, LongbowAttack(WebView(runtime, hero)));
        Assert.Equal(plain - 4, LongbowAttack(runtime.LiveSheetOf(hero)));
        Assert.Equal(plain, LongbowAttack(hero.Sheet)); // the stored sheet is not touched

        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Exhaustion", -2));
        Assert.Equal(plain, LongbowAttack(runtime.LiveSheetOf(hero)));
    }

    [Fact]
    public async Task OneLongRest_LowersExhaustionByExactlyOne_Async()
    {
        var (runtime, hero) = await TableAsync();
        await using var _ = runtime;
        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Exhaustion", 3));

        await runtime.EnqueueAsync(new RestCommand(hero.Id, "Short Rest", Array.Empty<string>()));
        Assert.Equal(3, runtime.Snapshot.Actor(hero.Id)!.Pool("Exhaustion"));
        await runtime.EnqueueAsync(new RestCommand(hero.Id, "Long Rest", Array.Empty<string>()));

        Assert.Equal(2, runtime.Snapshot.Actor(hero.Id)!.Pool("Exhaustion"));
    }

    [Fact]
    public async Task ExhaustionLevelSix_StillKills_Async()
    {
        var (runtime, hero) = await TableAsync();
        await using var _ = runtime;

        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Exhaustion", 6));

        Assert.Contains(runtime.Snapshot.Actor(hero.Id)!.Effects, e => e.EffectId == "Dead");
    }

    [Fact]
    public async Task BlessAsATableStatus_AddsItsDieToTheCardsAttackRoll_Async()
    {
        var (runtime, hero) = await TableAsync();
        await using var _ = runtime;

        await runtime.EnqueueAsync(new AddStatusEffectCommand(hero.Id, "Bless", hero.Id, ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, hero.Id));

        var live = WebView(runtime, hero);
        var attack = _engine.AttackSite(_engine.Evaluate(live).Actions.Single(a => a.Entry.Id == "longbow"));
        Assert.Equal(new RollDice("Bless", "+1d4"), Assert.Single(_engine.Bonuses(live, attack).Dice));
        Assert.Empty(_engine.Bonuses(hero.Sheet, attack).Dice);
    }
}
