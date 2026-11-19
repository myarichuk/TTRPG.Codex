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
}
