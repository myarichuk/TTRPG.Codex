using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>P4b review: <c>stays</c> (Prone after Unconscious) is written on every way the source ends, the drop runs once on a system with a <c>turnEnd</c> handler,
/// a handler cannot clear a condition that only another brings, and undo takes a stay back.</summary>
[Collection("RavenDb")]
public class ConditionStaysTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenEncounterRepository _encounters;
    private readonly ComponentRegistry _registry;

    private static readonly SystemDefinition Kind = SystemDefinitionLoader.Parse("""
        id: stays-test
        name: Stays test
        conditions: [Sleeping, Dazed, Prone]
        conditionEffects:
          Sleeping:
            implies: [Dazed, { condition: Prone, stays: true }]
          Frightened:
            value: { max: 4, decrease: { on: turnEnd, by: 1 } }
        sheetKinds:
          - kind: hero
            label: Hero
            tracks:
              - { key: HP, max: 10, min: -5, start: max }
        events:
          wake:
            - { intents: [{ condition: { remove: Sleeping } }] }
          shake:
            - { intents: [{ condition: { remove: Dazed } }] }
          turnEnd:
            - { intents: [{ log: { key: ended } }] }
        """);

    public ConditionStaysTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("Stays_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _encounters = new RavenEncounterRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<AttributesComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    private async Task<(CampaignRuntime Runtime, ActorDocument Hero, SessionDocument Session)> TableAsync(SystemDefinition system, bool srd = false)
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Aria", Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-1",
            Sheet = srd
                ? new Dictionary<string, object> { ["HP"] = 20L, ["HP_Max"] = 20L, ["con"] = 14L, ["dex"] = 12L, ["level"] = 5L }
                : new Dictionary<string, object> { ["kind"] = "hero", ["HP"] = 10L, ["HP_Max"] = 10L }
        };
        ActorSheetSync.ApplySheetToState(hero, system, _registry);
        await _actors.SaveAsync(hero);
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, null, system);
        runtime.Hydrate(new[] { hero });
        return (runtime, hero, session);
    }

    private static List<string> Statuses(CampaignRuntime runtime, ActorDocument hero) => runtime.Snapshot.Actor(hero.Id)!.Effects.Select(e => e.EffectId).ToList();

    private static Task AddAsync(CampaignRuntime runtime, ActorDocument hero, string name, int rounds = ActiveEffect.Indefinite) =>
        runtime.EnqueueAsync(new AddStatusEffectCommand(hero.Id, name, hero.Id, rounds, EffectExpiry.EndOfTurn, hero.Id));

    [Fact]
    public async Task ATimedSource_LeavesTheStayWhenItExpires_Async()
    {
        var (runtime, hero, session) = await TableAsync(Kind);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 15));
        await AddAsync(runtime, hero, "Sleeping", rounds: 1);

        await runtime.EnqueueAsync(new NextTurnCommand());

        Assert.Equal(new[] { "Prone" }, Statuses(runtime, hero));
        Assert.Contains(session.Events, e => e.Description.Contains("Aria gained Prone."));
    }

    [Fact]
    public async Task TheCardsCross_OnASheetOnlySource_LeavesTheStay_AndUndoTakesItBack_Async()
    {
        var (runtime, hero, _) = await TableAsync(Kind);
        await using var _ = runtime;
        runtime.GetActorDocument(hero.Id)!.Sheet[SheetModel.ConditionsKey] = new List<object> { "Sleeping" };

        await runtime.EnqueueAsync(new RemoveStatusEffectCommand(hero.Id, "Sleeping"));

        Assert.Equal(new[] { "Prone" }, Statuses(runtime, hero));
        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));
        Assert.Empty(Statuses(runtime, hero));
    }

    [Fact]
    public async Task ARemovedStatus_LeavesTheStayOnce_AndUndoTakesItBack_Async()
    {
        var (runtime, hero, _) = await TableAsync(Kind);
        await using var _ = runtime;
        await AddAsync(runtime, hero, "Sleeping");
        await AddAsync(runtime, hero, "Prone");

        await runtime.EnqueueAsync(new RemoveStatusEffectCommand(hero.Id, "Sleeping"));
        Assert.Equal(new[] { "Prone" }, Statuses(runtime, hero));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));
        Assert.Equal(new[] { "Sleeping", "Prone" }, Statuses(runtime, hero));
    }

    [Fact]
    public async Task AHandlerThatEndsTheSource_WritesTheStayOnce_AndSaysIt_Async()
    {
        var (runtime, hero, session) = await TableAsync(Kind);
        await using var _ = runtime;
        await AddAsync(runtime, hero, "Sleeping");

        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "wake", new Dictionary<string, object?>()));

        Assert.Equal(new[] { "Prone" }, Statuses(runtime, hero));
        Assert.Contains(session.Events, e => e.Description.Contains("Prone"));
    }

    [Fact]
    public async Task AHandlerThatClearsAnImpliedOnlyCondition_ChangesNothing_AndLogsNothing_Async()
    {
        var (runtime, hero, session) = await TableAsync(Kind);
        await using var _ = runtime;
        await AddAsync(runtime, hero, "Sleeping");
        var logged = session.Events.Count;

        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "shake", new Dictionary<string, object?>()));

        Assert.Equal(new[] { "Sleeping" }, Statuses(runtime, hero));
        Assert.DoesNotContain(session.Events.Skip(logged), e => e.Description.Contains("Dazed"));
    }

    [Fact]
    public async Task TheTurnEndDrop_RunsOnce_OnASystemWithATurnEndHandler_Async()
    {
        var (runtime, hero, _) = await TableAsync(Kind);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 15));
        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Frightened", 2));

        await runtime.EnqueueAsync(new NextTurnCommand());

        Assert.Equal(1, runtime.Snapshot.Actor(hero.Id)!.Pool("condition_frightened"));
    }

    [Fact]
    public async Task HealingUpFromZero_LeavesProne_AndSaysIt_Async()
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        var (runtime, hero, session) = await TableAsync(system, srd: true);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 30, "trap"));
        Assert.Contains("Unconscious", Statuses(runtime, hero));

        await runtime.EnqueueAsync(new ApplyHealingCommand(hero.Id, "HP", 5, "potion"));

        var statuses = Statuses(runtime, hero);
        Assert.DoesNotContain("Unconscious", statuses);
        Assert.Contains("Prone", statuses);
        Assert.Contains(session.Events, e => e.Type == "Healing" && e.Description.Contains("Aria gained Prone."));
    }

    [Fact]
    public async Task DyingEndsUnconsciousToo_AndTheBodyStaysProne_Async()
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        var (runtime, hero, _) = await TableAsync(system, srd: true);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 30, "trap"));

        Assert.True(TableRules.Kill(runtime, runtime.GetEntity(hero.Id)!.Value, hero.Id));

        var body = runtime.GetEntity(hero.Id)!.Value;
        Assert.True(CodexWorld.HasStatus(body, "Prone"));
        Assert.False(CodexWorld.HasStatus(body, "Unconscious"));
    }
}
