using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>The recovery check of the real four-degrees system at a live table: the start of a dying creature's turn writes its dying and wounded
/// values into the pool, says the check, a death at the death value gives the dead status, and undo puts the numbers back.</summary>
[Collection("RavenDb")]
public class FourDegreesRulesTableTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private static readonly SystemDefinition System =
        SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "four-degrees", SystemDefinitionLoader.FileName));

    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly ComponentRegistry _registry;

    public FourDegreesRulesTableTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("FourDegreesRules_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    private async Task<(CampaignRuntime Runtime, SessionDocument Session, ActorDocument Hero)> TableAsync(int dying, int wounded = 0, int doomed = 0)
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Vex", Kind = ActorKind.PlayerCharacter };
        hero.Sheet["HP"] = 0L;
        hero.Sheet["HP_Max"] = 20L;
        ActorSheetSync.ApplySheetToState(hero, System, _registry);
        await _actors.SaveAsync(hero);
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: System);
        runtime.Hydrate(new[] { hero });
        var pool = runtime.GetEntity(hero.Id)!.Value.Get<ResourcePoolComponent>();
        pool.Set("HP", 0);
        pool.Set("condition_dying", dying);
        pool.Set("condition_wounded", wounded);
        pool.Set("condition_doomed", doomed);
        return (runtime, session, hero);
    }

    private async Task<List<string>> LogAsync(SessionDocument session) =>
        (await _sessions.GetAsync(session.Id))!.Events.Select(e => e.Description).ToList();

    private static Task Turn(CampaignRuntime runtime, ActorDocument hero, int face) =>
        runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "turnStart", new Dictionary<string, object?>(), new SequenceDiceSource(new[] { face })));

    [Fact]
    public async Task AFailedRecoveryCheck_RaisesDying_IsLogged_AndUndoRestoresIt()
    {
        var (runtime, session, hero) = await TableAsync(dying: 1);
        await using var _ = runtime;
        var pool = runtime.GetEntity(hero.Id)!.Value.Get<ResourcePoolComponent>();

        await Turn(runtime, hero, 4);

        Assert.Equal(2, pool.Get("condition_dying"));
        var log = await LogAsync(session);
        Assert.Contains(log, d => d.Contains("rolled d20: 4"));
        Assert.Contains(log, d => d.Contains("Recovery check"));
        Assert.Contains("Vex: Dying is now 2.", string.Join(" ", log));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));
        Assert.Equal(1, pool.Get("condition_dying"));
    }

    [Fact]
    public async Task AdvancingInitiativeOntoADyingCharacter_RollsItsCheck_AndAnotherCreaturesTurnLogsNothing()
    {
        // The real path: the combat console's next turn. Its dice are the table's own, so the outcome is any of the four; every one moves dying 2.
        var campaignId = Guid.NewGuid().ToString();
        var other = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Lurk", Kind = ActorKind.PlayerCharacter };
        other.Sheet["HP"] = 12L;
        other.Sheet["HP_Max"] = 20L;
        ActorSheetSync.ApplySheetToState(other, System, _registry);
        await _actors.SaveAsync(other);
        var (runtime, session, hero) = await TableAsync(dying: 2, wounded: 1);
        await using var _ = runtime;
        runtime.Hydrate(new[] { hero, other });
        var pool = runtime.GetEntity(hero.Id)!.Value.Get<ResourcePoolComponent>();
        pool.Set("HP", 0);
        pool.Set("condition_dying", 2);
        pool.Set("condition_wounded", 1);
        await runtime.EnqueueAsync(new SetInitiativeCommand(other.Id, 20));
        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 10));

        await runtime.EnqueueAsync(new NextTurnCommand()); // onto the dying character

        var log = await LogAsync(session);
        Assert.Single(log, d => d.Contains("rolled d20"));
        Assert.Contains(log, d => d.Contains("Recovery check DC 12"));
        var dead = CodexWorld.HasStatus(runtime.GetEntity(hero.Id)!.Value, "Dead");
        Assert.True(dead || pool.Get("condition_dying") != 2);

        await runtime.EnqueueAsync(new NextTurnCommand()); // onto the healthy one: nothing is rolled or said
        Assert.Single(await LogAsync(session), d => d.Contains("rolled d20"));
        Assert.DoesNotContain(await LogAsync(session), d => d.Contains("Lurk: the turnStart"));
    }

    [Fact]
    public async Task ASuccessfulRecoveryCheck_EndsDying_AndRaisesWounded()
    {
        var (runtime, session, hero) = await TableAsync(dying: 1, wounded: 1);
        await using var _ = runtime;
        var pool = runtime.GetEntity(hero.Id)!.Value.Get<ResourcePoolComponent>();

        await Turn(runtime, hero, 15);

        Assert.Equal(0, pool.Get("condition_dying"));
        Assert.Equal(2, pool.Get("condition_wounded"));
        var log = await LogAsync(session);
        Assert.Contains("Vex: Dying has ended.", string.Join(" ", log));
        Assert.Contains("Vex: Wounded is now 2.", string.Join(" ", log));
    }

    [Fact]
    public async Task ACheckThatReachesTheDeathValue_KillsTheCreature_WithTheDeadStatus()
    {
        var (runtime, session, hero) = await TableAsync(dying: 3);
        await using var _ = runtime;
        var entity = runtime.GetEntity(hero.Id)!.Value;

        await Turn(runtime, hero, 5);

        Assert.True(CodexWorld.HasStatus(entity, "Dead"));
        Assert.Equal(0, entity.Get<ResourcePoolComponent>().Get("condition_dying"));
        Assert.Contains("Vex dies.", string.Join(" ", await LogAsync(session)));
    }

    [Fact]
    public async Task ACreatureThatIsNotDying_HasNoCheckAndNothingInTheLog()
    {
        var (runtime, session, hero) = await TableAsync(dying: 0);
        await using var _ = runtime;

        await Turn(runtime, hero, 5);

        Assert.DoesNotContain(await LogAsync(session), d => d.Contains("rolled"));
    }

    // ---- Persistent damage: the end-of-turn flat check (conditions persistent-damage) ----

    private async Task<(CampaignRuntime Runtime, SessionDocument Session, ActorDocument Body)> BurningAsync(ActorKind kind, bool burning = true)
    {
        var campaignId = Guid.NewGuid().ToString();
        var body = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Ember", Kind = kind };
        body.Sheet["HP"] = 12L;
        body.Sheet["HP_Max"] = 20L;
        ActorSheetSync.ApplySheetToState(body, System, _registry);
        await _actors.SaveAsync(body);
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: System);
        runtime.Hydrate(new[] { body });
        if (burning)
            await runtime.EnqueueAsync(new AddStatusEffectCommand(body.Id, "Persistent Damage", body.Id, ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, body.Id));
        return (runtime, session, body);
    }

    private static bool Burning(CampaignRuntime runtime, ActorDocument body) => runtime.Snapshot.Actor(body.Id)!.Effects.Any(e => e.EffectId == "Persistent Damage");

    [Theory]
    [InlineData(ActorKind.PlayerCharacter, 15, false, "Success")]
    [InlineData(ActorKind.Monster, 15, false, "Success")]
    [InlineData(ActorKind.PlayerCharacter, 14, true, "Failure")]
    [InlineData(ActorKind.Monster, 14, true, "Failure")]
    public async Task PersistentDamage_EndsOnASuccessfulFlatCheck_AndStaysOnAFailure(ActorKind kind, int face, bool stays, string degree)
    {
        var (runtime, session, body) = await BurningAsync(kind);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new RaiseEventCommand(body.Id, "turnEnd", new Dictionary<string, object?>(), new SequenceDiceSource(new[] { face })));

        Assert.Equal(stays, Burning(runtime, body));
        var log = await LogAsync(session);
        Assert.Contains(log, d => d.Contains($"rolled d20: {face}"));
        Assert.Contains(log, d => d.Contains($"Persistent damage check DC 15, {degree}"));
        Assert.Contains(log, d => d.Contains("the end of turn rule rolled")); // the English log says the turn event in words, not its key
    }

    [Fact]
    public async Task NoPersistentDamage_MeansNoRollAndNoLine_AtTheEndOfATurn()
    {
        var (runtime, session, body) = await BurningAsync(ActorKind.Monster, burning: false);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new RaiseEventCommand(body.Id, "turnEnd", new Dictionary<string, object?>(), new SequenceDiceSource(new[] { 15 })));

        Assert.DoesNotContain(await LogAsync(session), d => d.Contains("rolled") || d.Contains("Persistent damage"));
    }

    [Fact]
    public async Task NextTurn_RollsThePersistentDamageCheck_AndStillLowersAValuedCondition()
    {
        // The real path: the combat console's next turn. The check ends the condition, and the built-in lowering of frightened runs after
        // the script (declaring turnEnd keeps it).
        var (runtime, session, body) = await BurningAsync(ActorKind.Monster);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new SetInitiativeCommand(body.Id, 15));
        await runtime.EnqueueAsync(new AdjustConditionCommand(body.Id, "Frightened", 2));

        await runtime.EnqueueAsync(new NextTurnCommand { Dice = new SequenceDiceSource(new[] { 16 }) });

        Assert.Equal(1, runtime.Snapshot.Actor(body.Id)!.Pool("condition_frightened"));
        Assert.False(Burning(runtime, body));
        var log = await LogAsync(session);
        Assert.Single(log, d => d.Contains("rolled d20"));
        Assert.Contains(log, d => d.Contains("Persistent damage check DC 15, Success"));
    }

    [Fact]
    public async Task OneUndo_TakesBackTheCheckAndTheLoweringTogether()
    {
        var (runtime, _, body) = await BurningAsync(ActorKind.Monster);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new SetInitiativeCommand(body.Id, 15));
        await runtime.EnqueueAsync(new AdjustConditionCommand(body.Id, "Frightened", 2));
        await runtime.EnqueueAsync(new NextTurnCommand { Dice = new SequenceDiceSource(new[] { 16 }) });
        Assert.False(Burning(runtime, body));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(body.Id));

        Assert.True(Burning(runtime, body));
        Assert.Equal(2, runtime.Snapshot.Actor(body.Id)!.Pool("condition_frightened"));
    }

    [Fact]
    public async Task ACheckThatChangesNothing_LeavesAnEarlierUndoAlone()
    {
        var (runtime, _, body) = await BurningAsync(ActorKind.Monster);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new SetInitiativeCommand(body.Id, 15));
        await runtime.EnqueueAsync(new ApplyDamageCommand(body.Id, "HP", 3, "fire"));
        Assert.Equal(9, runtime.Snapshot.Actor(body.Id)!.Pool("HP"));

        await runtime.EnqueueAsync(new NextTurnCommand { Dice = new SequenceDiceSource(new[] { 14 }) }); // a failed check: only a line in the log
        Assert.True(Burning(runtime, body));
        await runtime.EnqueueAsync(new UndoLastChangeCommand(body.Id));

        Assert.Equal(12, runtime.Snapshot.Actor(body.Id)!.Pool("HP"));
    }

    [Fact]
    public async Task OneUndo_TakesBackADyingCreaturesRecoveryAtTheStartOfItsTurn()
    {
        var (runtime, _, hero) = await TableAsync(dying: 1, wounded: 0);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 10));

        await runtime.EnqueueAsync(new NextTurnCommand { Dice = new SequenceDiceSource(new[] { 15 }) }); // its turn ends, and comes round to itself
        var pool = runtime.GetEntity(hero.Id)!.Value.Get<ResourcePoolComponent>();
        Assert.Equal(0, pool.Get("condition_dying"));
        Assert.Equal(1, pool.Get("condition_wounded"));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));

        Assert.Equal(1, pool.Get("condition_dying"));
        Assert.Equal(0, pool.Get("condition_wounded"));
    }

    [Fact]
    public async Task ALoneCreature_TurnEndAndTurnStart_AreOneUndo()
    {
        // Persistent damage and frightened at the end of its turn, dying at the start of the next (which is its own again).
        var (runtime, _, hero) = await TableAsync(dying: 1, wounded: 0);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 10));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(hero.Id, "Persistent Damage", hero.Id, ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, hero.Id));
        await runtime.EnqueueAsync(new AdjustConditionCommand(hero.Id, "Frightened", 2));
        var pool = runtime.GetEntity(hero.Id)!.Value.Get<ResourcePoolComponent>();

        await runtime.EnqueueAsync(new NextTurnCommand { Dice = new SequenceDiceSource(new[] { 16, 15 }) });
        Assert.False(Burning(runtime, hero));
        Assert.Equal(1, pool.Get("condition_frightened"));
        Assert.Equal(0, pool.Get("condition_dying"));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));

        Assert.True(Burning(runtime, hero));
        Assert.Equal(2, pool.Get("condition_frightened"));
        Assert.Equal(1, pool.Get("condition_dying"));
        Assert.Equal(0, pool.Get("condition_wounded"));
    }
}
