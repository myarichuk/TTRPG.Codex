using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>C3 of docs/SHEET_CONDITIONS_DESIGN.md: <see cref="SheetConditions.LiveView"/>, the one view the hosts evaluate, and the condition value
/// keys the table and the sheet share (mirrored like a <c>uses_*</c> counter, merged by delta).</summary>
public class SheetConditionLiveViewTests
{
    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: live-view
        name: Live view
        resources: [{ key: hp }]
        conditionEffects:
          Frightened:
            value: { max: 4 }
            modifiers: [{ target: attack, amount: "-value" }]
          Hexed:
            modifiers: [{ target: attack, amount: "-2" }]
        """);

    private static List<string> Conditions(IReadOnlyDictionary<string, object> sheet) => SheetRuleEngine.RawList(sheet, SheetModel.ConditionsKey);

    [Fact]
    public void TheConditions_AreTheSheetsOwn_TheTablesStatuses_AndEveryValuedConditionAboveZero()
    {
        var sheet = new Dictionary<string, object> { ["hp"] = 10L, [SheetModel.ConditionsKey] = new List<object> { "Poisoned" } };
        var pools = new Dictionary<string, int> { ["condition_frightened"] = 2 };

        var live = SheetConditions.LiveView(System, sheet, pools, new[] { "Hexed", "poisoned" });

        Assert.Equal(new[] { "Poisoned", "Hexed", "Frightened" }, Conditions(live));
    }

    [Fact]
    public void ThePools_OverrideTheNumericKeys_AndNothingElse()
    {
        var sheet = new Dictionary<string, object> { ["hp"] = 10L, ["name"] = "Vex", ["uses_dash"] = 1L };
        var pools = new Dictionary<string, int> { ["hp"] = 4, ["uses_dash"] = 0, ["name"] = 9, ["Stray"] = 5, ["condition_frightened"] = 3, ["stress"] = 2 };

        var live = SheetConditions.LiveView(System, sheet, pools, null, new[] { "stress" });

        Assert.Equal(4L, live["hp"]);
        Assert.Equal(0L, live["uses_dash"]);
        Assert.Equal("Vex", live["name"]);
        Assert.False(live.ContainsKey("Stray"));
        Assert.Equal(3L, live["condition_frightened"]);
        Assert.Equal(2L, live["stress"]); // a sheet kind's track the sheet has not written yet
    }

    [Fact]
    public void AValuedCondition_AtZero_DoesNotHold_ListedAsAStatusOrNot()
    {
        var sheet = new Dictionary<string, object> { [SheetModel.ConditionsKey] = new List<object> { "Frightened" }, ["condition_frightened"] = 3L };
        var pools = new Dictionary<string, int> { ["condition_frightened"] = 0 };

        var live = SheetConditions.LiveView(System, sheet, pools, new[] { "Frightened" });

        Assert.Empty(Conditions(live));
        Assert.Equal(0L, live["condition_frightened"]);
    }

    [Fact]
    public void NoPoolsAndNoStatuses_IsTheSheetWithItsOwnConditions()
    {
        var sheet = new Dictionary<string, object> { [SheetModel.ConditionsKey] = new List<object> { "Hexed" }, ["condition_frightened"] = 1L };

        var live = SheetConditions.LiveView(System, sheet, null, null);

        Assert.Equal(new[] { "Hexed", "Frightened" }, Conditions(live));
    }

    [Fact]
    public void NothingPassedIn_IsChanged()
    {
        var own = new List<object> { "Poisoned" };
        var sheet = new Dictionary<string, object> { ["hp"] = 10L, [SheetModel.ConditionsKey] = own };
        var pools = new Dictionary<string, int> { ["hp"] = 4, ["condition_frightened"] = 2 };
        var statuses = new List<string> { "Hexed" };

        var live = SheetConditions.LiveView(System, sheet, pools, statuses);

        Assert.NotSame(sheet, live);
        Assert.Equal(new[] { "hp", SheetModel.ConditionsKey }, sheet.Keys);
        Assert.Equal(10L, sheet["hp"]);
        Assert.Equal(new object[] { "Poisoned" }, own);
        Assert.Same(own, sheet[SheetModel.ConditionsKey]);
        Assert.Equal(new[] { 4, 2 }, pools.Values);
        Assert.Equal(new[] { "Hexed" }, statuses);
    }

    [Fact]
    public void TheValueKeys_AreCounters_SoTheSheetAndTheTableMergeByDelta()
    {
        Assert.Contains(("condition_frightened", (string?)null), SheetCounters.Of(System, Array.Empty<string>()));

        // The sheet page raised it by one (1 to 2) while the table raised it by two (1 to 3): the save leaves 4.
        var merged = new Dictionary<string, object>();
        SheetCounters.MergeByDelta(merged,
            edited: new Dictionary<string, object> { ["condition_frightened"] = 2L },
            baseline: new Dictionary<string, object> { ["condition_frightened"] = 1L },
            live: new Dictionary<string, object> { ["condition_frightened"] = 3L }, System);
        Assert.Equal(4L, merged["condition_frightened"]);

        // The table lowering it below what the sheet takes off stops at 0.
        SheetCounters.MergeByDelta(merged,
            new Dictionary<string, object> { ["condition_frightened"] = 0L },
            new Dictionary<string, object> { ["condition_frightened"] = 3L },
            new Dictionary<string, object> { ["condition_frightened"] = 1L }, System);
        Assert.Equal(0L, merged["condition_frightened"]);
    }

    [Fact]
    public void TheValueKeys_AreMirroredToThePools_LikeAUsesCounter()
    {
        var registry = new ComponentRegistry();
        registry.Register<ResourcePoolComponent>();
        var doc = new ActorDocument { Id = "a", Name = "Vex" };
        doc.Sheet["condition_frightened"] = 3L;

        ActorSheetSync.ApplySheetToState(doc, System, registry);
        Assert.Equal(3, registry.Hydrate(doc.State).OfType<ResourcePoolComponent>().Single().Get("condition_frightened"));

        // A sheet that does not hold it leaves the table's number alone, and the table's number is written back when it changes.
        doc.Sheet.Remove("condition_frightened");
        ActorSheetSync.ApplySheetToState(doc, System, registry);
        Assert.Equal(3, registry.Hydrate(doc.State).OfType<ResourcePoolComponent>().Single().Get("condition_frightened"));
        ActorSheetSync.ApplyStateToSheet(doc, System, registry);
        Assert.Equal(3L, doc.Sheet["condition_frightened"]);
    }
}

/// <summary>
/// The hosts (docs/SHEET_CONDITIONS_DESIGN.md, "Hosts that must pass the live view"): a character's attack bonus at the table drops while a condition
/// holds there, as a table status and as a value in the table's pools, and the stored sheet never takes either. The real SRD system with a flat
/// <c>Hexed</c> and a valued <c>Weary</c> (-2 to attacks, -5 speed, a level each) added, as the C2 tests do.
/// </summary>
[Collection("RavenDb")]
public class SheetConditionHostsTests : IClassFixture<RavenDbFixture>, IAsyncLifetime
{
    private const string WearyKey = "condition_weary";

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly ComponentRegistry _components;
    private SystemDefinition _system = null!;
    private SheetRuleEngine _engine = null!;

    public SheetConditionHostsTests(RavenDbFixture fixture)
    {
        _components = new ComponentRegistry();
        _components.Register<ResourcePoolComponent>();
        _components.Register<ActiveEffectsComponent>();
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
        _dbService = new RavenDbService(fixture.DbPath, "LiveView_" + Guid.NewGuid(), runInMemory: true);
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition("srd5e")!;
        _system.ConditionEffects["Hexed"] = new ConditionEffectDefinition { Modifiers = { new ModifierDefinition { Target = "attack", Amount = "-2" } } };
        _system.ConditionEffects["Weary"] = new ConditionEffectDefinition
        {
            Value = new ConditionValueDefinition { Max = "6" },
            Modifiers =
            {
                new ModifierDefinition { Target = "attack", Amount = "-2 * value" },
                new ModifierDefinition { Target = "speed", Amount = "-5 * value" }
            }
        };
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

    private async Task<(CampaignRuntime Runtime, ActorDocument Hero, string CampaignId)> TableAsync(params string[] sheetConditions)
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
        if (sheetConditions.Length > 0) sheet[SheetModel.ConditionsKey] = sheetConditions.Cast<object>().ToList();

        var hero = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Vex", Kind = ActorKind.PlayerCharacter, Sheet = sheet };
        ActorSheetSync.ApplySheetToState(hero, _system, _components);
        await _actors.SaveAsync(hero);

        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _components, NullLogger.Instance, _sessions, session, system: _system) { Resolver = new Pack(_registry) };
        runtime.Hydrate(new[] { hero });
        return (runtime, hero, campaignId);
    }

    private int LongbowAttack(IReadOnlyDictionary<string, object> sheet) => _engine.Evaluate(sheet).Actions.Single(a => a.Entry.Id == "longbow").AttackBonus!.Value;

    // What a Web host does: the snapshot's pools and effects, no runtime.
    private Dictionary<string, object> WebView(CampaignRuntime runtime, ActorDocument hero)
    {
        var state = runtime.Snapshot.Actor(hero.Id)!;
        return SheetConditions.LiveView(_system, hero.Sheet, state.Pools, state.Effects.Select(e => e.EffectId));
    }

    // A command that publishes the snapshot after the test changed a pool directly.
    private static Task Publish(CampaignRuntime runtime, ActorDocument hero) =>
        runtime.EnqueueAsync(new AddStatusEffectCommand(hero.Id, "Marker", hero.Id, ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, hero.Id));

    private static void SetPool(CampaignRuntime runtime, ActorDocument hero, string key, int value) =>
        runtime.GetEntity(hero.Id)!.Value.Get<ResourcePoolComponent>().Set(key, value);

    [Fact]
    public async Task ATableStatus_LowersTheAttackBonusAtTheTable_AndTheStoredSheetIsUnchanged()
    {
        var (runtime, hero, _) = await TableAsync();
        await using var _ = runtime;
        var stored = new Dictionary<string, object>(hero.Sheet);
        var plain = LongbowAttack(hero.Sheet);

        await runtime.EnqueueAsync(new AddStatusEffectCommand(hero.Id, "Hexed", hero.Id, ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, hero.Id));

        Assert.Equal(plain - 2, LongbowAttack(WebView(runtime, hero)));
        Assert.Equal(plain - 2, LongbowAttack(runtime.LiveSheetOf(hero)));
        Assert.Equal(plain, LongbowAttack(hero.Sheet));
        Assert.Equal(stored.Keys.OrderBy(k => k), hero.Sheet.Keys.OrderBy(k => k));
        Assert.False(hero.Sheet.ContainsKey(SheetModel.ConditionsKey));
    }

    [Fact]
    public async Task AValueInThePools_LowersTheAttackBonusAtTheTable_AndTheStoredSheetIsUnchanged()
    {
        var (runtime, hero, _) = await TableAsync();
        await using var _ = runtime;
        var plain = LongbowAttack(hero.Sheet);

        SetPool(runtime, hero, WearyKey, 2);
        await Publish(runtime, hero);

        Assert.Equal(plain - 4, LongbowAttack(WebView(runtime, hero)));
        Assert.Equal(plain - 4, LongbowAttack(runtime.LiveSheetOf(hero)));
        Assert.Equal(plain, LongbowAttack(hero.Sheet));
        Assert.Contains("Weary", SheetRuleEngine.RawList(WebView(runtime, hero), SheetModel.ConditionsKey));
        Assert.False(hero.Sheet.ContainsKey(SheetModel.ConditionsKey));

        // The Number fields the condition grants reach the live view too (EvaluateLive), on its copy: the stored speed is not touched.
        var live = WebView(runtime, hero);
        _engine.EvaluateLive(live);
        Assert.Equal(20, YamlValues.GetInt(live, "speed"));
        Assert.Equal(30, YamlValues.GetInt(hero.Sheet, "speed"));

        SetPool(runtime, hero, WearyKey, 0);
        await Publish(runtime, hero);
        Assert.Equal(plain, LongbowAttack(WebView(runtime, hero)));
    }

    [Fact]
    public async Task AValueRaisedAtTheTable_ShowsOnTheSheet_AndTheSheetsEditMergesByDelta()
    {
        var (runtime, hero, campaignId) = await TableAsync();
        await using var _ = runtime;
        SetPool(runtime, hero, WearyKey, 1);
        await Publish(runtime, hero);

        // The page opens: the table's value is on the sheet.
        var live = new ActorDocument { Sheet = new Dictionary<string, object>(), State = _components.Snapshot(EntityComponentSync.SnapshotAll(runtime.GetEntity(hero.Id)!.Value, _components.RegisteredComponents)) };
        ActorSheetSync.ApplyStateToSheet(live, _system, _components);
        Assert.Equal(1L, live.Sheet[WearyKey]);

        // The table raises it by two while the page, opened at 1, raises it by one.
        SetPool(runtime, hero, WearyKey, 3);
        var baseline = new Dictionary<string, object>(hero.Sheet) { [WearyKey] = 1L };
        var edited = new ActorDocument { Id = hero.Id, CampaignId = campaignId, Name = hero.Name, Sheet = new Dictionary<string, object>(baseline) { [WearyKey] = 2L } };
        await runtime.EnqueueAsync(new ReplaceActorCommand(edited, baseline, new CampaignAccess(campaignId, "dm", CampaignRole.DM), _system));

        var cached = runtime.GetActorDocument(hero.Id)!;
        Assert.Equal(4L, cached.Sheet[WearyKey]);
        Assert.Equal(4, runtime.GetEntity(hero.Id)!.Value.Get<ResourcePoolComponent>().Get(WearyKey));
    }

    [Fact]
    public async Task APageSaveWhileExhaustedAtTheTable_DoesNotKeepSpeedLoweredOnceItEnds_NorTheTablesStatuses()
    {
        var (runtime, hero, campaignId) = await TableAsync();
        await using var _ = runtime;
        Assert.Equal(30, YamlValues.GetInt(hero.Sheet, "speed"));
        SetPool(runtime, hero, WearyKey, 2);
        await runtime.EnqueueAsync(new AddStatusEffectCommand(hero.Id, "Hexed", hero.Id, ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, hero.Id));

        // The page as ActorSheet runs it: opened with the table's value on the sheet, recomputed in place (the stored sheet's own value lowers
        // the speed, remembered in granted__speed), the live view worked on a copy, then saved as the editor left it.
        var loaded = new Dictionary<string, object>(hero.Sheet) { [WearyKey] = 2L };
        var edit = new Dictionary<string, object>(loaded);
        _engine.Recompute(edit);
        var evaluated = SheetConditions.LiveView(_system, edit, runtime.Snapshot.Actor(hero.Id)!.Pools, runtime.Snapshot.Actor(hero.Id)!.Effects.Select(e => e.EffectId));
        _engine.Recompute(evaluated);
        Assert.Equal(20, YamlValues.GetInt(edit, "speed"));
        Assert.False(edit.ContainsKey(SheetModel.ConditionsKey)); // the live view's conditions stayed on its copy

        var edited = new ActorDocument { Id = hero.Id, CampaignId = campaignId, Name = hero.Name, Sheet = edit };
        await runtime.EnqueueAsync(new ReplaceActorCommand(edited, loaded, new CampaignAccess(campaignId, "dm", CampaignRole.DM), _system));
        var cached = runtime.GetActorDocument(hero.Id)!;
        Assert.Equal(20, YamlValues.GetInt(cached.Sheet, "speed"));
        Assert.Equal(2L, cached.Sheet[WearyKey]);
        Assert.False(cached.Sheet.ContainsKey(SheetModel.ConditionsKey)); // Hexed is the table's, not the sheet's

        // The exhaustion ends at the table. The persist step writes the table's number onto the stored sheet and takes the speed it had lowered off
        // with it, so the sheet is right off the table too, before anyone opens it again.
        SetPool(runtime, hero, WearyKey, 0);
        await Publish(runtime, hero);
        var stored = (await _actors.GetVisibleAsync(hero.Id, new CampaignAccess(campaignId, "dm", CampaignRole.DM)))!;

        Assert.Equal(0L, stored.Sheet[WearyKey]);
        Assert.Equal(30, YamlValues.GetInt(stored.Sheet, "speed"));
        Assert.False(stored.Sheet.ContainsKey(SheetRuleEngine.GrantedPrefix + "speed"));
        Assert.False(stored.Sheet.ContainsKey(SheetModel.ConditionsKey));

        // And the page's own next recompute agrees.
        _engine.Recompute(stored.Sheet);
        Assert.Equal(30, YamlValues.GetInt(stored.Sheet, "speed"));
    }


    // What a Web host does after this change: the snapshot's own LiveView, which reads the sheet's conditions from the table's copy.
    private Dictionary<string, object> TableView(CampaignRuntime runtime, ActorDocument page) =>
        runtime.Snapshot.Actor(page.Id)!.LiveView(_system, page.Sheet);

    [Fact]
    public async Task AConditionSetOnTheSheet_IsInTheSnapshot_AndCountsAtTheTable()
    {
        var (runtime, hero, _) = await TableAsync("Hexed");
        await using var _ = runtime;
        await Publish(runtime, hero);
        var state = runtime.Snapshot.Actor(hero.Id)!;

        Assert.Equal(new[] { "Hexed" }, state.SheetConditions);
        Assert.Equal(new[] { "Hexed" }, state.OnlyOnSheet);
        var plain = new Dictionary<string, object>(hero.Sheet);
        plain.Remove(SheetModel.ConditionsKey);
        Assert.Equal(LongbowAttack(plain) - 2, LongbowAttack(TableView(runtime, hero)));
    }

    [Fact]
    public async Task RemovingASheetSetCondition_AtTheTable_TakesItOffTheSheet_AndUndoPutsItBack()
    {
        var (runtime, hero, campaignId) = await TableAsync("Hexed", "Inspired");
        await using var _ = runtime;
        // The page's copy of the sheet, which does not reload when the table changes it.
        var page = new ActorDocument { Id = hero.Id, Sheet = new Dictionary<string, object>(hero.Sheet) };
        var hexed = LongbowAttack(TableView(runtime, page));

        await runtime.EnqueueAsync(new RemoveStatusEffectCommand(hero.Id, "hexed"));

        var state = runtime.Snapshot.Actor(hero.Id)!;
        Assert.Equal(new[] { "Inspired" }, state.SheetConditions);
        Assert.Equal(hexed + 2, LongbowAttack(TableView(runtime, page))); // the page's stale copy still lists Hexed; the table's does not
        Assert.Equal(new object[] { "Inspired" }, runtime.GetActorDocument(hero.Id)!.Sheet[SheetModel.ConditionsKey]);
        Assert.Equal("StatusEffectRemoved", state.UndoKind);
        var stored = (await _actors.GetVisibleAsync(hero.Id, new CampaignAccess(campaignId, "dm", CampaignRole.DM)))!;
        Assert.Equal(new[] { "Inspired" }, SheetRuleEngine.RawList(stored.Sheet, SheetModel.ConditionsKey));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));
        Assert.Equal(new[] { "Hexed", "Inspired" }, runtime.Snapshot.Actor(hero.Id)!.SheetConditions);
        Assert.Equal(hexed, LongbowAttack(TableView(runtime, page)));
    }

    [Fact]
    public async Task RemovingTheLastSheetSetCondition_RemovesTheKey()
    {
        var (runtime, hero, _) = await TableAsync("Hexed");
        await using var _ = runtime;

        await runtime.EnqueueAsync(new RemoveStatusEffectCommand(hero.Id, "Hexed"));

        Assert.False(runtime.GetActorDocument(hero.Id)!.Sheet.ContainsKey(SheetModel.ConditionsKey));
        Assert.Empty(runtime.Snapshot.Actor(hero.Id)!.SheetConditions!);
    }

    [Fact]
    public async Task AConditionOnTheSheetAndAsAStatus_IsShownOnce_AndOneRemoveTakesBoth()
    {
        var (runtime, hero, _) = await TableAsync("Hexed");
        await using var _ = runtime;
        await runtime.EnqueueAsync(new AddStatusEffectCommand(hero.Id, "Hexed", hero.Id, ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, hero.Id));
        Assert.Empty(runtime.Snapshot.Actor(hero.Id)!.OnlyOnSheet);

        await runtime.EnqueueAsync(new RemoveStatusEffectCommand(hero.Id, "Hexed"));

        var state = runtime.Snapshot.Actor(hero.Id)!;
        Assert.DoesNotContain(state.Effects, e => e.EffectId == "Hexed");
        Assert.Empty(state.SheetConditions!);
    }
}
