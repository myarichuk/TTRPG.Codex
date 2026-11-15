using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>C4 of docs/SHEET_CONDITIONS_DESIGN.md: <see cref="AdjustConditionCommand"/> raises and lowers a valued condition's value, and the
/// turn, a rest or a moment lowers it through <c>value.decrease</c>. Runs on srd5e, which has no <c>events:</c>, with valued conditions injected.</summary>
[Collection("RavenDb")]
public class AdjustConditionCommandTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private const string FrightenedKey = "condition_frightened";

    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenEncounterRepository _encounters;
    private readonly ComponentRegistry _registry;

    public AdjustConditionCommandTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("AdjustCondition_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _encounters = new RavenEncounterRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<AttributesComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    /// <summary>srd5e with Frightened as a valued condition: a maximum of 4 (or a formula), falling at <paramref name="on"/>.</summary>
    private static SystemDefinition System(string? on = "turnEnd", string max = "4", int by = 1, Action<SystemDefinition>? more = null)
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        system.ConditionEffects["Frightened"] = new ConditionEffectDefinition
        {
            Value = new ConditionValueDefinition { Max = max, Decrease = on == null ? null : new ConditionDecreaseDefinition { On = on, By = by } }
        };
        more?.Invoke(system);
        return system;
    }

    private async Task<(CampaignRuntime Runtime, ActorDocument Hero, ActorDocument Goblin, SessionDocument Session)> TableAsync(SystemDefinition system)
    {
        var campaignId = Guid.NewGuid().ToString();
        async Task<ActorDocument> Seed(string name, ActorKind kind)
        {
            var doc = new ActorDocument
            {
                Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = name, Kind = kind, OwnerUserId = kind == ActorKind.PlayerCharacter ? "player-1" : null,
                Sheet = new Dictionary<string, object> { ["HP"] = 20L, ["HP_Max"] = 20L, ["con"] = 14L, ["dex"] = 12L, ["level"] = 5L }
            };
            ActorSheetSync.ApplySheetToState(doc, system, _registry);
            await _actors.SaveAsync(doc);
            return doc;
        }

        var hero = await Seed("Aria", ActorKind.PlayerCharacter);
        var goblin = await Seed("Goblin", ActorKind.Monster);
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, null, system);
        runtime.Hydrate(new[] { hero, goblin });
        return (runtime, hero, goblin, session);
    }

    private static int? Level(CampaignRuntime runtime, ActorDocument actor, string key = FrightenedKey) => runtime.Snapshot.Actor(actor.Id)!.Pool(key);

    private static List<string> Holding(CampaignRuntime runtime, SystemDefinition system, ActorDocument actor)
    {
        var state = runtime.Snapshot.Actor(actor.Id)!;
        return SheetRuleEngine.RawList(SheetConditions.LiveView(system, actor.Sheet, state.Pools, state.Effects.Select(e => e.EffectId)), SheetModel.ConditionsKey);
    }

    private static async Task TakeTurnsAsync(CampaignRuntime runtime, ActorDocument first, ActorDocument second)
    {
        await runtime.EnqueueAsync(new SetInitiativeCommand(first.Id, 18));
        await runtime.EnqueueAsync(new SetInitiativeCommand(second.Id, 9));
    }

    // ---- The command ----

    [Fact]
    public async Task RaisingFromZero_MakesTheConditionHold_AndWritesNoStatus_Async()
    {
        var system = System();
        var (runtime, hero, _, session) = await TableAsync(system);
        await using var _ = runtime;
        Assert.DoesNotContain("Frightened", Holding(runtime, system, hero));

        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Frightened", 2));

        Assert.Equal(2, Level(runtime, hero));
        Assert.Contains("Frightened", Holding(runtime, system, hero));
        Assert.Empty(runtime.Snapshot.Actor(hero.Id)!.Effects);
        Assert.Contains(session.Events, e => e.Type == "StatusEffect" && e.Description == "Aria: Frightened is now 2.");
    }

    [Fact]
    public async Task LoweringToZero_EndsTheCondition_AndClampsBelow_Async()
    {
        var system = System();
        var (runtime, hero, _, session) = await TableAsync(system);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Frightened", 2));

        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Frightened", -5));

        Assert.Equal(0, Level(runtime, hero));
        Assert.DoesNotContain("Frightened", Holding(runtime, system, hero));
        Assert.Empty(runtime.Snapshot.Actor(hero.Id)!.Effects);
        Assert.Contains(session.Events, e => e.Type == "StatusEffect" && e.Description == "Aria: Frightened has ended.");
    }

    [Fact]
    public async Task Raising_StopsAtTheMaximum_Async()
    {
        var system = System();
        var (runtime, hero, _, _) = await TableAsync(system);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Frightened", 9));

        Assert.Equal(4, Level(runtime, hero));
    }

    [Fact]
    public async Task TheMaximum_CanBeAFormula_WorkedOutOnTheLiveView_Async()
    {
        var system = System(max: "level - 2");
        var (runtime, hero, _, _) = await TableAsync(system);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Frightened", 9));

        Assert.Equal(3, Level(runtime, hero));
    }

    [Fact]
    public async Task Undo_RestoresTheValue_Async()
    {
        var system = System();
        var (runtime, hero, _, _) = await TableAsync(system);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Frightened", 1));
        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Frightened", 2));
        Assert.Equal(3, Level(runtime, hero));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));

        Assert.Equal(1, Level(runtime, hero));
    }

    [Fact]
    public async Task Exhaustion_StillKillsAtTheTopLevel_AndIsLoggedAsExhaustion_Async()
    {
        var system = System();
        var (runtime, hero, _, session) = await TableAsync(system);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Exhaustion", 5));
        Assert.DoesNotContain(runtime.Snapshot.Actor(hero.Id)!.Effects, e => e.EffectId == "Dead");
        Assert.Contains(session.Events, e => e.Type == "Exhaustion" && e.Description == "Aria is at exhaustion level 5.");
        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Exhaustion", 1));

        Assert.Contains(runtime.Snapshot.Actor(hero.Id)!.Effects, e => e.EffectId == "Dead");
        Assert.Contains(session.Events, e => e.Type == "Exhaustion" && e.Description.Contains("died"));
    }

    // ---- Turn decrease ----

    [Fact]
    public async Task TurnEnd_LowersTheCreaturesCondition_OnlyAtItsOwnTurnEnd_AndUndoRestoresIt_Async()
    {
        var system = System();
        var (runtime, hero, goblin, session) = await TableAsync(system);
        await using var _ = runtime;
        await TakeTurnsAsync(runtime, hero, goblin);
        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Frightened", 2));

        await runtime.EnqueueAsync(new NextTurnCommand()); // the hero's turn ends
        Assert.Equal(1, Level(runtime, hero));
        Assert.Contains(session.Events, e => e.Type == "TurnAdvance" && e.Description.Contains("Aria: Frightened drops to 1."));

        await runtime.EnqueueAsync(new NextTurnCommand()); // the goblin's turn ends: not the hero's
        Assert.Equal(1, Level(runtime, hero));

        await runtime.EnqueueAsync(new NextTurnCommand()); // the hero's turn ends again
        Assert.Equal(0, Level(runtime, hero));
        Assert.DoesNotContain("Frightened", Holding(runtime, system, hero));
        Assert.Contains(session.Events, e => e.Type == "TurnAdvance" && e.Description.Contains("Aria: Frightened ends."));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));
        Assert.Equal(1, Level(runtime, hero));
    }

    [Fact]
    public async Task TurnStart_LowersTheCreatureWhoseTurnBegins_Once_Async()
    {
        var system = System(on: "turnStart");
        var (runtime, hero, goblin, _) = await TableAsync(system);
        await using var _ = runtime;
        await TakeTurnsAsync(runtime, hero, goblin);
        await runtime.EnqueueAsync(new AdjustConditionCommand(goblin.Id, "Frightened", 3));
        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Frightened", 3));

        await runtime.EnqueueAsync(new NextTurnCommand()); // the goblin's turn starts

        Assert.Equal(2, Level(runtime, goblin));
        Assert.Equal(3, Level(runtime, hero));
        await runtime.EnqueueAsync(new UndoLastChangeCommand(goblin.Id));
        Assert.Equal(3, Level(runtime, goblin));
    }

    // ---- Rests and moments ----

    [Fact]
    public async Task ARestsDecrease_LowersTheCondition_Async()
    {
        var system = System(on: "Long Rest", by: 2);
        var (runtime, hero, _, session) = await TableAsync(system);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Frightened", 3));

        await runtime.EnqueueAsync(new RestCommand(hero.Id, "Short Rest", Array.Empty<string>()));
        Assert.Equal(3, Level(runtime, hero));
        await runtime.EnqueueAsync(new RestCommand(hero.Id, "Long Rest", Array.Empty<string>()));

        Assert.Equal(1, Level(runtime, hero));
        Assert.Contains(session.Events, e => e.Type == "Rest" && e.Description.Contains("Frightened drops to 1"));
    }

    [Fact]
    public async Task AMomentsDecrease_LowersTheCondition_Async()
    {
        var system = System(on: "dawn");
        var (runtime, hero, _, _) = await TableAsync(system);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Frightened", 1));

        await runtime.EnqueueAsync(new MomentCommand(hero.Id, "dawn"));

        Assert.Equal(0, Level(runtime, hero));
    }

    [Fact]
    public async Task Exhaustion_WithADecreaseAndARestsExhaustionDelta_IsLoweredOnce_Async()
    {
        var system = System(more: s => s.ConditionEffects["Exhaustion"] = new ConditionEffectDefinition
        {
            Value = new ConditionValueDefinition { Key = "Exhaustion", Max = "6", FromVitals = true, Decrease = new ConditionDecreaseDefinition { On = "Long Rest", By = 1 } }
        });
        var (runtime, hero, _, _) = await TableAsync(system);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Exhaustion", 3));

        await runtime.EnqueueAsync(new RestCommand(hero.Id, "Long Rest", Array.Empty<string>()));

        Assert.Equal(2, Level(runtime, hero, "Exhaustion"));
    }
}
