using Codex.Core.Components;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>
/// The table as an encounter loop (audit 2026-10-21, §5 step 3): any planned session can be run,
/// an encounter can be ended and is restored, participants can be removed, the current turn
/// survives re-rolls, damage knows its type, zero hit points means something, rests heal, and
/// conditions can last until removed and survive the table being ended. Runs against the real
/// SRD system definition so the data in <c>vitals:</c> is what is being tested.
/// </summary>
[Collection("RavenDb")]
public class EncounterLoopTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private static readonly SystemDefinition Srd = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenEncounterRepository _encounters;
    private readonly ComponentRegistry _registry;

    public EncounterLoopTests(RavenDbFixture fixture)
    {
        _dbService = new RavenDbService(fixture.DbPath, "Encounter_" + Guid.NewGuid(), runInMemory: true);
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _encounters = new RavenEncounterRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<AttributesComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    private async Task<ActorDocument> SeedAsync(string campaignId, string name, int hp, ActorKind kind = ActorKind.Monster, Action<Dictionary<string, object>>? sheet = null)
    {
        var doc = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = campaignId,
            Name = name,
            Kind = kind,
            OwnerUserId = kind == ActorKind.PlayerCharacter ? "player-1" : null,
            Sheet = new Dictionary<string, object> { ["HP"] = (long)hp, ["HP_Max"] = (long)hp, ["con"] = 14L, ["dex"] = 12L }
        };
        sheet?.Invoke(doc.Sheet);
        ActorSheetSync.ApplySheetToState(doc, Srd, _registry);
        await _actors.SaveAsync(doc);
        return doc;
    }

    private async Task<SessionDocument> SeedSessionAsync(string campaignId, SessionStatus status = SessionStatus.Live, string title = "Live")
    {
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = status, Title = title };
        await _sessions.SaveAsync(session);
        return session;
    }

    private CampaignRuntime Runtime(string campaignId, SessionDocument session, IDiceRoller? dice = null) =>
        new(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, dice, Srd);

    private (CampaignRuntimeManager Manager, CampaignRepository Campaigns) BuildManager()
    {
        var hooks = new CampaignLifecycleHooks();
        var campaigns = new CampaignRepository(_dbService, new NoOpSystemCatalog(), _actors, _sessions,
            new RavenNoteRepository(_dbService), new RegionRepository(_dbService), new FactRepository(_dbService),
            new RavenHomebrewRepository(_dbService), _encounters, lifecycle: hooks);
        var loader = new Codex.Core.PluginLoader(NullLogger<Codex.Core.PluginLoader>.Instance, _registry, Substitute.For<IContentPackLoader>());
        return (new CampaignRuntimeManager(_actors, _sessions, campaigns, _encounters, _registry, loader, NullLoggerFactory.Instance, hooks), campaigns);
    }

    private Task<ActorDocument?> StoredAsync(string campaignId, string actorId) =>
        _actors.GetVisibleAsync(actorId, new CampaignAccess(campaignId, "owner", CampaignRole.DM));

    private static int Hp(CampaignRuntime runtime, string actorId) => runtime.Snapshot.Actor(actorId)!.Pool("HP")!.Value;
    private static bool Has(CampaignRuntime runtime, string actorId, string effect) => runtime.Snapshot.Actor(actorId)!.Effects.Any(e => e.EffectId == effect);

    // ---- Sessions and encounters (F4, F9) ----

    [Fact]
    public async Task Manager_RunsThePlannedSessionTheDmPicked_AndANewOneOnceTheCurrentIsComplete_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Two evenings" };
        await campaigns.SaveAsync(campaign);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var first = await SeedSessionAsync(campaignId, SessionStatus.Completed, "Session 1");
        var planned = await SeedSessionAsync(campaignId, SessionStatus.Planned, "Into the sewers");
        await campaigns.UpdateAsync(campaignId, c => c.CurrentSessionId = first.Id);
        campaign.CurrentSessionId = first.Id;

        // Picking the planned session runs it, and the campaign now points at it.
        var runtime = await manager.GetOrStartAsync(campaign, dm, planned.Id);
        Assert.Equal(planned.Id, runtime.ActiveSession!.Id);
        Assert.Equal(SessionStatus.Live, (await _sessions.GetAsync(planned.Id))!.Status);
        Assert.Equal(planned.Id, (await campaigns.GetAsync(campaignId))!.CurrentSessionId);

        // Another session cannot be started underneath it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetOrStartAsync(campaign, dm, first.Id));
        await manager.EvictAsync(campaignId);
        Assert.Equal(SessionStatus.Completed, (await _sessions.GetAsync(planned.Id))!.Status);

        // With the current session complete, a plain start opens a brand-new session rather than
        // appending tonight's events to it (F4).
        var again = await manager.GetOrStartAsync(campaign, dm);
        Assert.NotEqual(planned.Id, again.ActiveSession!.Id);
        Assert.NotEqual(first.Id, again.ActiveSession!.Id);
        Assert.Equal(3, (await _sessions.GetAllForCampaignAsync(campaignId)).Count());
        await manager.EvictAsync(campaignId);
    }

    [Fact]
    public async Task Manager_RestoresAnUnfinishedEncounter_WhenTheSessionIsReopened_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Interrupted" };
        await campaigns.SaveAsync(campaign);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var goblin = await SeedAsync(campaignId, "Goblin", 7);

        var runtime = await manager.GetOrStartAsync(campaign, dm);
        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 18));
        await runtime.EnqueueAsync(new SetInitiativeCommand(goblin.Id, 9));
        await runtime.EnqueueAsync(new NextTurnCommand());
        var sessionId = runtime.ActiveSession!.Id;
        var encounterId = runtime.ActiveEncounter!.Id;
        await manager.EvictAsync(campaignId);

        // The DM reopens the same session: the order and the turn come back as they were (F9).
        var resumed = await manager.GetOrStartAsync(campaign, dm, sessionId);
        Assert.Equal(encounterId, resumed.ActiveEncounter!.Id);
        Assert.Equal(new[] { hero.Id, goblin.Id }, resumed.ActiveEncounter.Participants.Select(p => p.ActorId));
        Assert.Equal(1, resumed.ActiveEncounter.TurnIndex);

        // Ending the encounter closes it for good: the next start of this session has no order.
        await resumed.EnqueueAsync(new EndEncounterCommand());
        Assert.Null(resumed.ActiveEncounter);
        Assert.NotNull((await _encounters.GetAsync(encounterId))!.EndedAt);
        await manager.EvictAsync(campaignId);
        var third = await manager.GetOrStartAsync(campaign, dm, sessionId);
        Assert.Null(third.ActiveEncounter);
        await manager.EvictAsync(campaignId);
    }

    [Fact]
    public async Task EndEncounter_ExpiresTimedConditions_AndKeepsIndefiniteOnes_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero });

        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 10));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(hero.Id, "Poisoned", "trap", 3, EffectExpiry.EndOfTurn, hero.Id));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(hero.Id, "Cursed", "idol", 0, EffectExpiry.EndOfTurn, hero.Id));
        await runtime.EnqueueAsync(new EndEncounterCommand());

        Assert.Null(runtime.ActiveEncounter);
        Assert.False(Has(runtime, hero.Id, "Poisoned"));
        Assert.True(Has(runtime, hero.Id, "Cursed"));
        Assert.Contains(session.Events, e => e.Type == "EncounterEnded");
    }

    // ---- Initiative (R2, F12) ----

    [Fact]
    public async Task ReSettingInitiative_KeepsTheCurrentTurn_AndRemovingAParticipant_KeepsThePointer_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var a = await SeedAsync(campaignId, "A", 10);
        var b = await SeedAsync(campaignId, "B", 10);
        var c = await SeedAsync(campaignId, "C", 10);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { a, b, c });

        await runtime.EnqueueAsync(new SetInitiativeCommand(a.Id, 20, TieBreak: 12));
        await runtime.EnqueueAsync(new SetInitiativeCommand(b.Id, 10, TieBreak: 12));
        await runtime.EnqueueAsync(new SetInitiativeCommand(c.Id, 10, TieBreak: 16));
        Assert.Equal(new[] { a.Id, c.Id, b.Id }, runtime.ActiveEncounter!.Participants.Select(p => p.ActorId));

        await runtime.EnqueueAsync(new NextTurnCommand());
        Assert.Equal(c.Id, runtime.ActiveEncounter.Participants[runtime.ActiveEncounter.TurnIndex].ActorId);

        // B's corrected roll moves them to the top; it is still C's turn.
        await runtime.EnqueueAsync(new SetInitiativeCommand(b.Id, 25));
        Assert.Equal(new[] { b.Id, a.Id, c.Id }, runtime.ActiveEncounter.Participants.Select(p => p.ActorId));
        Assert.Equal(c.Id, runtime.ActiveEncounter.Participants[runtime.ActiveEncounter.TurnIndex].ActorId);

        // Removing someone ahead of the current turn keeps it on C.
        await runtime.EnqueueAsync(new RemoveParticipantCommand(b.Id));
        Assert.Equal(c.Id, runtime.ActiveEncounter.Participants[runtime.ActiveEncounter.TurnIndex].ActorId);

        // Removing C on their own turn, as the last in the order, starts the next round.
        await runtime.EnqueueAsync(new RemoveParticipantCommand(c.Id));
        Assert.Equal(a.Id, runtime.ActiveEncounter.Participants[runtime.ActiveEncounter.TurnIndex].ActorId);
        Assert.Equal(2, runtime.ActiveEncounter.Round);
    }

    [Fact]
    public async Task PreviousTurn_RestoresHasActed_AndActNow_MovesADelayedParticipantBeforeTheCurrentOne_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var a = await SeedAsync(campaignId, "A", 10);
        var b = await SeedAsync(campaignId, "B", 10);
        var c = await SeedAsync(campaignId, "C", 10);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { a, b, c });
        await runtime.EnqueueAsync(new SetInitiativeCommand(a.Id, 20));
        await runtime.EnqueueAsync(new SetInitiativeCommand(b.Id, 15));
        await runtime.EnqueueAsync(new SetInitiativeCommand(c.Id, 10));

        await runtime.EnqueueAsync(new SetTurnStateCommand(a.Id, TurnState.Delayed));
        await runtime.EnqueueAsync(new NextTurnCommand());
        await runtime.EnqueueAsync(new NextTurnCommand());
        var encounter = runtime.ActiveEncounter!;
        Assert.Equal(c.Id, encounter.Participants[encounter.TurnIndex].ActorId);
        Assert.True(encounter.Participants.Single(p => p.ActorId == b.Id).HasActed);

        await runtime.EnqueueAsync(new PreviousTurnCommand());
        Assert.False(encounter.Participants.Single(p => p.ActorId == b.Id).HasActed);
        await runtime.EnqueueAsync(new NextTurnCommand());

        // A, who delayed, acts now: they slot in just before C and take the turn.
        await runtime.EnqueueAsync(new ActNowCommand(a.Id));
        Assert.Equal(new[] { b.Id, a.Id, c.Id }, encounter.Participants.Select(p => p.ActorId));
        Assert.Equal(a.Id, encounter.Participants[encounter.TurnIndex].ActorId);
        Assert.Equal(TurnState.Normal, encounter.Participants.Single(p => p.ActorId == a.Id).State);
    }

    // ---- Damage types, zero hit points, death saves (R11, R13, R19) ----

    [Fact]
    public async Task Damage_IsHalvedByResistance_CancelledByImmunity_AndDoubledByVulnerability_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var elemental = await SeedAsync(campaignId, "Elemental", 60, sheet: s =>
        {
            s["resistances"] = new List<object> { "fire", "bludgeoning, piercing, and slashing from nonmagical attacks" };
            s["immunities"] = new List<object> { "poison", "Poisoned" };
            s["vulnerabilities"] = new List<object> { "cold" };
        });
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { elemental });

        await runtime.EnqueueAsync(new ApplyDamageCommand(elemental.Id, "HP", 11, "wizard", "fire"));
        Assert.Equal(55, Hp(runtime, elemental.Id));
        await runtime.EnqueueAsync(new ApplyDamageCommand(elemental.Id, "HP", 9, "club", "bludgeoning"));
        Assert.Equal(51, Hp(runtime, elemental.Id));
        await runtime.EnqueueAsync(new ApplyDamageCommand(elemental.Id, "HP", 20, "dart", "poison"));
        Assert.Equal(51, Hp(runtime, elemental.Id));
        await runtime.EnqueueAsync(new ApplyDamageCommand(elemental.Id, "HP", 10, "ray", "cold"));
        Assert.Equal(31, Hp(runtime, elemental.Id));
        await runtime.EnqueueAsync(new ApplyDamageCommand(elemental.Id, "HP", 10, "sword"));
        Assert.Equal(21, Hp(runtime, elemental.Id));

        var log = session.Events.Where(e => e.Type == "Damage").Select(e => e.Description).ToList();
        Assert.Contains(log, d => d.Contains("5 fire damage") && d.Contains("resisted, 11 rolled"));
        Assert.Contains(log, d => d.Contains("immune to poison"));
        Assert.Contains(log, d => d.Contains("20 cold damage") && d.Contains("vulnerable"));
    }

    [Fact]
    public async Task ZeroHitPoints_DropsACharacter_KillsACreature_AndKillsOutrightOnOverflow_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var tank = await SeedAsync(campaignId, "Bram", 10, ActorKind.PlayerCharacter);
        var goblin = await SeedAsync(campaignId, "Goblin", 7);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero, tank, goblin });

        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 15, "ogre"));
        Assert.Equal(0, Hp(runtime, hero.Id));
        Assert.True(Has(runtime, hero.Id, "Unconscious"));
        Assert.False(Has(runtime, hero.Id, "Dead"));

        // 10 HP, 22 damage: 12 left over, more than the maximum of 10.
        await runtime.EnqueueAsync(new ApplyDamageCommand(tank.Id, "HP", 22, "dragon"));
        Assert.True(Has(runtime, tank.Id, "Dead"));
        Assert.False(Has(runtime, tank.Id, "Unconscious"));

        await runtime.EnqueueAsync(new ApplyDamageCommand(goblin.Id, "HP", 7, "arrow"));
        Assert.True(Has(runtime, goblin.Id, "Dead"));

        // A dead creature is skipped by the turn order.
        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 20));
        await runtime.EnqueueAsync(new SetInitiativeCommand(goblin.Id, 15));
        await runtime.EnqueueAsync(new SetInitiativeCommand(tank.Id, 10));
        await runtime.EnqueueAsync(new NextTurnCommand());
        var encounter = runtime.ActiveEncounter!;
        Assert.Equal(hero.Id, encounter.Participants[encounter.TurnIndex].ActorId);
        Assert.Equal(2, encounter.Round);

        Assert.Contains(session.Events, e => e.Type == "Damage" && e.Description.Contains("Aria dropped to 0") && e.PlayerDescription == "Aria took damage and went down.");
        Assert.Contains(session.Events, e => e.Type == "Damage" && e.Description.Contains("Bram died"));
    }

    [Fact]
    public async Task DeathSaves_ThreeSuccessesStabilize_ThreeFailuresKill_AHitWhileDownFails_AndHealingGetsUp_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var mate = await SeedAsync(campaignId, "Bram", 12, ActorKind.PlayerCharacter);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero, mate });
        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 12, "ogre"));
        await runtime.EnqueueAsync(new ApplyDamageCommand(mate.Id, "HP", 12, "ogre"));

        await runtime.EnqueueAsync(new RecordDeathSaveCommand(hero.Id, true));
        await runtime.EnqueueAsync(new RecordDeathSaveCommand(hero.Id, true));
        Assert.Equal(2, runtime.Snapshot.Actor(hero.Id)!.Pool("DeathSaveSuccesses"));
        await runtime.EnqueueAsync(new RecordDeathSaveCommand(hero.Id, true));
        Assert.True(Has(runtime, hero.Id, "Stable"));
        Assert.True(Has(runtime, hero.Id, "Unconscious"));
        Assert.Null(runtime.Snapshot.Actor(hero.Id)!.Pool("DeathSaveSuccesses"));

        // Healing a stable character brings them up: no longer stable or unconscious.
        await runtime.EnqueueAsync(new ApplyHealingCommand(hero.Id, "HP", 5, "potion"));
        Assert.Equal(5, Hp(runtime, hero.Id));
        Assert.False(Has(runtime, hero.Id, "Stable"));
        Assert.False(Has(runtime, hero.Id, "Unconscious"));

        // A critical hit while down is two failures; a third kills, and healing a dead character does nothing.
        await runtime.EnqueueAsync(new ApplyDamageCommand(mate.Id, "HP", 3, "ogre", IsCritical: true));
        Assert.Equal(2, runtime.Snapshot.Actor(mate.Id)!.Pool("DeathSaveFailures"));
        await runtime.EnqueueAsync(new RecordDeathSaveCommand(mate.Id, false));
        Assert.True(Has(runtime, mate.Id, "Dead"));
        await runtime.EnqueueAsync(new ApplyHealingCommand(mate.Id, "HP", 5, "potion"));
        Assert.Equal(0, Hp(runtime, mate.Id));
        Assert.Contains(session.Events, e => e.Type == "Healing" && e.Description.Contains("Bram is dead"));

        // Revived by the DM clearing the dead condition, then healed.
        await runtime.EnqueueAsync(new RemoveStatusEffectCommand(mate.Id, "Dead"));
        await runtime.EnqueueAsync(new ApplyHealingCommand(mate.Id, "HP", 1, "revivify"));
        Assert.Equal(1, Hp(runtime, mate.Id));
    }

    [Fact]
    public async Task RollingADeathSave_UsesTheTableDice_AndANaturalTwentyBringsTheCharacterBack_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var session = await SeedSessionAsync(campaignId);
        var dice = Substitute.For<IDiceRoller>();
        dice.Roll("1d20").Returns(new DiceRollResult("1d20", new[] { 20 }, Array.Empty<int>(), 0, 20));
        await using var runtime = Runtime(campaignId, session, dice);
        runtime.Hydrate(new[] { hero });
        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 12, "ogre"));

        await runtime.EnqueueAsync(new RollDeathSaveCommand(hero.Id, "player-1"));

        Assert.Equal(1, Hp(runtime, hero.Id));
        Assert.False(Has(runtime, hero.Id, "Unconscious"));
        Assert.Contains(session.RollLog, r => r.ActorId == hero.Id && r.Result.Contains("Death save"));
        Assert.Contains(session.Events, e => e.Type == "DeathSave" && e.Description.Contains("natural 20"));
    }

    [Fact]
    public async Task TemporaryHitPoints_KeepTheHigher_AbsorbFirst_AndAreClearedByALongRest_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero });

        await runtime.EnqueueAsync(new GrantTemporaryHitPointsCommand(hero.Id, 8, "false life"));
        await runtime.EnqueueAsync(new GrantTemporaryHitPointsCommand(hero.Id, 5, "inspiring leader"));
        Assert.Equal(8, runtime.Snapshot.Actor(hero.Id)!.Pool("TempHP"));

        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 10, "goblin"));
        Assert.Equal(0, runtime.Snapshot.Actor(hero.Id)!.Pool("TempHP"));
        Assert.Equal(10, Hp(runtime, hero.Id));
        Assert.Contains(session.Events, e => e.Type == "Damage" && e.Description.Contains("8 absorbed by temporary hit points"));

        await runtime.EnqueueAsync(new GrantTemporaryHitPointsCommand(hero.Id, 3, "ward"));
        await runtime.EnqueueAsync(new RestCommand(hero.Id, "Long Rest", Array.Empty<string>()));
        Assert.Equal(0, runtime.Snapshot.Actor(hero.Id)!.Pool("TempHP"));
        Assert.Equal(12, Hp(runtime, hero.Id));
    }

    // ---- Rests, hit dice, exhaustion (R5, R12) ----

    [Fact]
    public async Task LongRest_RestoresHitPoints_RegainsHalfTheHitDice_AndEasesExhaustion_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await SeedAsync(campaignId, "Aria", 30, ActorKind.PlayerCharacter, s => s["level"] = 5L);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero });
        Assert.Equal(5, runtime.Snapshot.Actor(hero.Id)!.Pool("HitDice_Max"));

        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 20, "ogre"));
        await runtime.EnqueueAsync(new AdjustExhaustionCommand(hero.Id, 2));
        Assert.Equal(2, runtime.Snapshot.Actor(hero.Id)!.Pool("Exhaustion"));

        // Spending hit dice on a short rest: 1d8 + CON (+2) each, five dice available.
        var dice = Substitute.For<IDiceRoller>();
        dice.Roll("1d8").Returns(new DiceRollResult("1d8", new[] { 4 }, Array.Empty<int>(), 0, 4));
        await using var resting = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, dice, Srd);
        resting.Hydrate(new[] { (await StoredAsync(campaignId, hero.Id))! });
        await resting.EnqueueAsync(new SpendHitDiceCommand(hero.Id, 2, 8));
        Assert.Equal(22, Hp(resting, hero.Id));
        Assert.Equal(3, resting.Snapshot.Actor(hero.Id)!.Pool("HitDice"));
        Assert.Contains(session.Events, e => e.Type == "HitDice" && e.Description.Contains("spent 2 hit dice") && e.Description.Contains("recovered 12"));

        await resting.EnqueueAsync(new RestCommand(hero.Id, "Long Rest", Array.Empty<string>()));
        Assert.Equal(30, Hp(resting, hero.Id));
        Assert.Equal(5, resting.Snapshot.Actor(hero.Id)!.Pool("HitDice"));
        Assert.Equal(1, resting.Snapshot.Actor(hero.Id)!.Pool("Exhaustion"));
        Assert.Contains(session.Events, e => e.Type == "Rest" && e.Description.Contains("Hit Points restored") && e.Description.Contains("2 hit dice regained") && e.Description.Contains("exhaustion now 1"));
    }

    [Fact]
    public async Task Exhaustion_AtTheTopLevel_Kills_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero });

        await runtime.EnqueueAsync(new AdjustExhaustionCommand(hero.Id, 5));
        Assert.False(Has(runtime, hero.Id, "Dead"));
        await runtime.EnqueueAsync(new AdjustExhaustionCommand(hero.Id, 1));
        Assert.True(Has(runtime, hero.Id, "Dead"));
    }

    // ---- Conditions (R6, F14, F23) ----

    [Fact]
    public async Task Conditions_CanLastUntilRemoved_TickAtTheStartOfATurn_AndSurviveAPersist_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var goblin = await SeedAsync(campaignId, "Goblin", 7);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero, goblin });
        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 20));
        await runtime.EnqueueAsync(new SetInitiativeCommand(goblin.Id, 10));

        await runtime.EnqueueAsync(new AddStatusEffectCommand(hero.Id, "Blinded", "spell", 0, EffectExpiry.EndOfTurn, hero.Id));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(goblin.Id, "Frightened", "spell", 1, EffectExpiry.StartOfTurn, goblin.Id));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(goblin.Id, "Prone", "trip", 1, EffectExpiry.EndOfTurn, goblin.Id));

        // Hero's turn ends, goblin's begins: Frightened (start of the goblin's turn) expires, Prone (end) stays.
        await runtime.EnqueueAsync(new NextTurnCommand());
        Assert.False(Has(runtime, goblin.Id, "Frightened"));
        Assert.True(Has(runtime, goblin.Id, "Prone"));
        await runtime.EnqueueAsync(new NextTurnCommand());
        Assert.False(Has(runtime, goblin.Id, "Prone"));
        for (var i = 0; i < 6; i++) await runtime.EnqueueAsync(new NextTurnCommand());
        Assert.True(Has(runtime, hero.Id, "Blinded"));
        Assert.Contains(session.Events, e => e.Type == "StatusEffect" && e.Description.Contains("gained Blinded until removed"));

        // The condition was persisted with the actor and comes back when the actor is hydrated again.
        var stored = (await StoredAsync(campaignId, hero.Id))!;
        var effects = _registry.Hydrate(stored.State).OfType<ActiveEffectsComponent>().Single().Effects;
        Assert.Contains(effects, e => e.EffectId == "Blinded" && e.IsIndefinite && e.AnchorId == hero.Id);
    }

    // ---- Actors joining and leaving a live table (F10) ----

    [Fact]
    public async Task Manager_HydratesAnActorCreatedWhileLive_AndDetachesOneDeleted_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Live cast" };
        await campaigns.SaveAsync(campaign);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var runtime = await manager.GetOrStartAsync(campaign, dm);

        var pool = new ResourcePoolComponent();
        pool.Set("HP", 7);
        pool.Set("HP_Max", 7);
        var goblin = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Goblin", Kind = ActorKind.Monster, State = _registry.Snapshot(new object[] { pool }) };
        Assert.True(await manager.TryCreateActorAsync(goblin, dm));
        Assert.True(runtime.HasActor(goblin.Id));

        await runtime.EnqueueAsync(new SetInitiativeCommand(goblin.Id, 12));
        await runtime.EnqueueAsync(new ApplyDamageCommand(goblin.Id, "HP", 3, "arrow"));
        Assert.Equal(4, Hp(runtime, goblin.Id));

        Assert.True(await manager.TryDeleteActorAsync(goblin.Id, dm));
        Assert.False(runtime.HasActor(goblin.Id));
        Assert.Empty(runtime.ActiveEncounter!.Participants);
        Assert.Null(await StoredAsync(campaignId, goblin.Id));
        await manager.EvictAsync(campaignId);
    }
}
