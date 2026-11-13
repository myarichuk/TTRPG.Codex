using Codex.Core.Components;
using Codex.Core.Rules;
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

    private async Task<ActorDocument> SeedAsync(string campaignId, string name, int hp, ActorKind kind = ActorKind.Monster, Action<Dictionary<string, object>>? sheet = null, string? idPrefix = null)
    {
        var doc = new ActorDocument
        {
            Id = idPrefix + Guid.NewGuid(),
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
    public async Task EndingTheTable_MidFight_KeepsTheSessionOpen_SoAPlainStartBringsTheOrderBack_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Ambush" };
        await campaigns.SaveAsync(campaign);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);

        var runtime = await manager.GetOrStartAsync(campaign, dm);
        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 18));
        var sessionId = runtime.ActiveSession!.Id;
        var encounterId = runtime.ActiveEncounter!.Id;

        // The console's "End the table" promises the order comes back (D6).
        Assert.False(await manager.EvictAsync(campaignId));
        Assert.Equal(SessionStatus.Live, (await _sessions.GetAsync(sessionId))!.Status);

        var resumed = await manager.GetOrStartAsync(campaign, dm);
        Assert.Equal(sessionId, resumed.ActiveSession!.Id);
        Assert.Equal(encounterId, resumed.ActiveEncounter!.Id);

        // Once the fight is over, ending the table completes the session as before.
        await resumed.EnqueueAsync(new EndEncounterCommand());
        Assert.True(await manager.EvictAsync(campaignId));
        Assert.Equal(SessionStatus.Completed, (await _sessions.GetAsync(sessionId))!.Status);
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

    [Fact]
    public async Task A_button_press_with_no_source_logs_no_source_And_a_turn_is_named_by_whose_it_is_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero });

        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 3, ""));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(hero.Id, "Poisoned", "", 2, EffectExpiry.EndOfTurn, hero.Id));

        Assert.Contains(session.Events, e => e.Description == "Aria took 3 damage.");
        Assert.Contains(session.Events, e => e.Description == "Aria gained Poisoned. It lasts 2 rounds, ending at the end of Aria's turn.");
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
        // The conditional entry ("... from nonmagical attacks") is left to the DM: the table can't tell a magic club from a plain one.
        await runtime.EnqueueAsync(new ApplyDamageCommand(elemental.Id, "HP", 9, "club", "bludgeoning"));
        Assert.Equal(46, Hp(runtime, elemental.Id));
        await runtime.EnqueueAsync(new ApplyDamageCommand(elemental.Id, "HP", 20, "dart", "poison"));
        Assert.Equal(46, Hp(runtime, elemental.Id));
        await runtime.EnqueueAsync(new ApplyDamageCommand(elemental.Id, "HP", 10, "ray", "cold"));
        Assert.Equal(26, Hp(runtime, elemental.Id));
        await runtime.EnqueueAsync(new ApplyDamageCommand(elemental.Id, "HP", 10, "sword"));
        Assert.Equal(16, Hp(runtime, elemental.Id));

        var log = session.Events.Where(e => e.Type == "Damage").Select(e => e.Description).ToList();
        Assert.Contains(log, d => d.Contains("5 fire damage") && d.Contains("Resisted: 11 rolled"));
        Assert.Contains(log, d => d.Contains("immune to poison"));
        Assert.Contains(log, d => d.Contains("20 cold damage") && d.Contains("Vulnerable"));
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
        // The snapshot carries the rules' verdict, which the table's console reads (Phase 7).
        Assert.Equal((false, true), (runtime.Snapshot.Actor(hero.Id)!.IsDead, runtime.Snapshot.Actor(hero.Id)!.IsDown));

        // 10 HP, 22 damage: 12 left over, more than the maximum of 10.
        await runtime.EnqueueAsync(new ApplyDamageCommand(tank.Id, "HP", 22, "dragon"));
        Assert.True(Has(runtime, tank.Id, "Dead"));
        Assert.False(Has(runtime, tank.Id, "Unconscious"));
        Assert.Equal((true, false), (runtime.Snapshot.Actor(tank.Id)!.IsDead, runtime.Snapshot.Actor(tank.Id)!.IsDown));
        Assert.Equal((false, false), (runtime.Snapshot.Actor(goblin.Id)!.IsDead, runtime.Snapshot.Actor(goblin.Id)!.IsDown));

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

    // ---- Feature counters: refill rules and spends ----

    private Task<ActorDocument> WithCounterAsync(string campaignId, string name, string key, int left, int max, string rules, string label) =>
        SeedAsync(campaignId, name, 12, ActorKind.PlayerCharacter, s =>
        {
            s[key] = (long)left;
            s[key + "_Max"] = (long)max;
            s[key + "_Rest"] = rules;
            s[key + "_Label"] = label;
        });

    [Fact]
    public async Task ARestRefillsACounterByItsOwnRules_PartOnAShortRest_AllOnALongRest_AndLogsIt_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await WithCounterAsync(campaignId, "Aria", "uses_wild", 0, 4, "Short Rest=1|Long Rest", "Wild Shape");
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero });

        await runtime.EnqueueAsync(new RestCommand(hero.Id, "Short Rest", Array.Empty<string>()));
        Assert.Equal(1, runtime.Snapshot.Actor(hero.Id)!.Pool("uses_wild"));
        await runtime.EnqueueAsync(new RestCommand(hero.Id, "Short Rest", Array.Empty<string>()));
        Assert.Equal(2, runtime.Snapshot.Actor(hero.Id)!.Pool("uses_wild"));
        Assert.Contains(session.Events, e => e.Type == "Rest" && e.Description.Contains("Wild Shape +1"));

        await runtime.EnqueueAsync(new RestCommand(hero.Id, "Long Rest", Array.Empty<string>()));
        Assert.Equal(4, runtime.Snapshot.Actor(hero.Id)!.Pool("uses_wild"));
        Assert.Contains(session.Events, e => e.Type == "Rest" && e.Description.Contains("Wild Shape +2"));
    }

    [Fact]
    public async Task ARestFromTheSheet_RefillsItsSlotsAndCounters_ButHealsNothing_Async()
    {
        // The sheet's own rest button, pressed while the table runs: what it does off the table (slots and feature uses), through the
        // table, and nothing of a full rest (hit points stay as they are).
        var campaignId = Guid.NewGuid().ToString();
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter, s =>
        {
            s["uses_wild"] = 0L;
            s["uses_wild_Max"] = 2L;
            s["uses_wild_Rest"] = "Long Rest";
            s["uses_wild_Label"] = "Wild Shape";
            s["slots_1"] = 0L;
            s["slots_1_Max"] = 3L;
        });
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero });
        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 5, "ogre"));

        await runtime.EnqueueAsync(new RestCommand(hero.Id, "Long Rest", new[] { "slots_1" }, PoolsOnly: true));

        Assert.Equal(7, Hp(runtime, hero.Id));
        Assert.Equal(2, runtime.Snapshot.Actor(hero.Id)!.Pool("uses_wild"));
        Assert.Equal(3, runtime.Snapshot.Actor(hero.Id)!.Pool("slots_1"));
    }

    [Fact]
    public async Task ARolledRefill_RollsThroughTheTableDice_IsLogged_AndStopsAtTheMaximum_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await WithCounterAsync(campaignId, "Aria", "uses_charge", 0, 4, "dawn=1d6+1", "Staff charges");
        var session = await SeedSessionAsync(campaignId);
        var dice = Substitute.For<IDiceRoller>();
        dice.Roll("1d6+1").Returns(new DiceRollResult("1d6+1", new[] { 4 }, Array.Empty<int>(), 1, 5));
        await using var runtime = Runtime(campaignId, session, dice);
        runtime.Hydrate(new[] { hero });

        await runtime.EnqueueAsync(new MomentCommand(hero.Id, "dawn"));

        Assert.Equal(4, runtime.Snapshot.Actor(hero.Id)!.Pool("uses_charge")); // rolled 5, capped at 4
        Assert.Contains(session.RollLog, r => r.ActorId == hero.Id && r.Expression == "1d6+1" && r.Label == "Staff charges" && r.Total == 5);
        Assert.Contains(session.Events, e => e.Type == "Rest" && e.Description.Contains("Dawn comes for Aria") && e.Description.Contains("Staff charges +4"));
    }

    [Fact]
    public async Task ARefillOnInitiative_WhenEmpty_AppliesOnJoiningTheOrder_NotOnACorrectedRoll_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await WithCounterAsync(campaignId, "Aria", "uses_rage", 0, 3, "initiative=1!empty", "Rage");
        var mate = await WithCounterAsync(campaignId, "Bram", "uses_rage", 2, 3, "initiative=1!empty", "Rage");
        var session = await SeedSessionAsync(campaignId);
        var dice = Substitute.For<IDiceRoller>();
        dice.Roll("1d20+2").Returns(new DiceRollResult("1d20+2", new[] { 13 }, Array.Empty<int>(), 2, 15));
        await using var runtime = Runtime(campaignId, session, dice);
        runtime.Hydrate(new[] { hero, mate });

        await runtime.EnqueueAsync(new RollInitiativeCommand(hero.Id, "1d20+2", "player-1"));
        await runtime.EnqueueAsync(new RollInitiativeCommand(mate.Id, "1d20+2", "player-1"));
        Assert.Equal(1, runtime.Snapshot.Actor(hero.Id)!.Pool("uses_rage")); // none left: one back
        Assert.Equal(2, runtime.Snapshot.Actor(mate.Id)!.Pool("uses_rage")); // not empty: nothing
        Assert.Contains(session.Events, e => e.Type == "Initiative" && e.Description.Contains("Rage +1"));

        await runtime.EnqueueAsync(new AdjustResourceCommand(hero.Id, "uses_rage", -1, "Rage"));
        Assert.Equal(0, runtime.Snapshot.Actor(hero.Id)!.Pool("uses_rage"));
        await runtime.EnqueueAsync(new RollInitiativeCommand(hero.Id, "1d20+2", "player-1")); // already in the order
        Assert.Equal(0, runtime.Snapshot.Actor(hero.Id)!.Pool("uses_rage"));
    }

    [Fact]
    public async Task ASpendLargerThanWhatIsLeft_IsRefused_AndChangesNothing_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await WithCounterAsync(campaignId, "Aria", "uses_focus", 1, 4, "Short Rest", "Focus");
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero });

        var tooMuch = new AdjustResourceCommand(hero.Id, "uses_focus", -2, "Focus");
        await runtime.EnqueueAsync(tooMuch);
        Assert.False(tooMuch.Succeeded);
        Assert.Equal(1, runtime.Snapshot.Actor(hero.Id)!.Pool("uses_focus"));

        var one = new AdjustResourceCommand(hero.Id, "uses_focus", -1, "Focus");
        await runtime.EnqueueAsync(one);
        Assert.True(one.Succeeded);
        Assert.Equal(0, runtime.Snapshot.Actor(hero.Id)!.Pool("uses_focus"));
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
        Assert.Contains(session.Events, e => e.Type == "StatusEffect" && e.Description.Contains("gained Blinded") && e.Description.Contains("until removed"));

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

    // ---- Review fixes of 2026-10-22 ----

    [Fact]
    public async Task Initiative_RolledLowestFirst_StillStartsOnTheHighestRoll_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var goblin = await SeedAsync(campaignId, "Goblin", 7);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero, goblin });

        await runtime.EnqueueAsync(new SetInitiativeCommand(goblin.Id, 5));
        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 20));

        var encounter = runtime.ActiveEncounter!;
        Assert.Equal(new[] { hero.Id, goblin.Id }, encounter.Participants.Select(p => p.ActorId));
        Assert.Equal(hero.Id, encounter.Participants[encounter.TurnIndex].ActorId);
    }

    [Fact]
    public async Task ActNow_HandsTheTurnBack_WithoutTickingTheInterruptedActorsStartOfTurnAgain_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var rogue = await SeedAsync(campaignId, "Rogue", 10);
        var fighter = await SeedAsync(campaignId, "Fighter", 10);
        var goblin = await SeedAsync(campaignId, "Goblin", 10);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { rogue, fighter, goblin });
        await runtime.EnqueueAsync(new SetInitiativeCommand(rogue.Id, 25));
        await runtime.EnqueueAsync(new SetInitiativeCommand(fighter.Id, 15));
        await runtime.EnqueueAsync(new SetInitiativeCommand(goblin.Id, 5));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(fighter.Id, "Blessed", "cleric", 2, EffectExpiry.StartOfTurn, fighter.Id));

        // The rogue delays; the fighter's turn starts and Blessed ticks from 2 to 1.
        await runtime.EnqueueAsync(new SetTurnStateCommand(rogue.Id, TurnState.Delayed));
        await runtime.EnqueueAsync(new NextTurnCommand());
        Assert.Equal(1, runtime.Snapshot.Actor(fighter.Id)!.Effects.Single(e => e.EffectId == "Blessed").RoundsRemaining);

        // The rogue jumps in ahead of the fighter, then hands the turn back: it is the same turn, not a new one.
        await runtime.EnqueueAsync(new ActNowCommand(rogue.Id));
        await runtime.EnqueueAsync(new NextTurnCommand());
        var encounter = runtime.ActiveEncounter!;
        Assert.Equal(fighter.Id, encounter.Participants[encounter.TurnIndex].ActorId);
        Assert.Equal(1, runtime.Snapshot.Actor(fighter.Id)!.Effects.Single(e => e.EffectId == "Blessed").RoundsRemaining);

        // Next round it ticks as normal.
        await runtime.EnqueueAsync(new NextTurnCommand());
        await runtime.EnqueueAsync(new NextTurnCommand());
        await runtime.EnqueueAsync(new NextTurnCommand());
        Assert.Equal(fighter.Id, encounter.Participants[encounter.TurnIndex].ActorId);
        Assert.False(Has(runtime, fighter.Id, "Blessed"));
    }

    [Theory]
    [InlineData(1, "z-", "a-")] // the one who acted holds current + 1, and the newcomer's id sorts first
    [InlineData(0, "b-", "d-")] // the one who acted ties the current actor outright and sorts ahead by id
    public async Task ActNow_WithTiedInitiative_NeverGivesAnyoneASecondTurn_Async(int actedTieBreak, string actedIdPrefix, string delayerIdPrefix)
    {
        var campaignId = Guid.NewGuid().ToString();
        var acted = await SeedAsync(campaignId, "Acted", 10, idPrefix: actedIdPrefix);
        var acting = await SeedAsync(campaignId, "Acting", 10, idPrefix: "c-");
        var delayer = await SeedAsync(campaignId, "Delayer", 10, idPrefix: delayerIdPrefix);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { acted, acting, delayer });
        await runtime.EnqueueAsync(new SetInitiativeCommand(delayer.Id, 20));
        await runtime.EnqueueAsync(new SetInitiativeCommand(acted.Id, 15, actedTieBreak));
        await runtime.EnqueueAsync(new SetInitiativeCommand(acting.Id, 15, 0));

        await runtime.EnqueueAsync(new SetTurnStateCommand(delayer.Id, TurnState.Delayed));
        await runtime.EnqueueAsync(new NextTurnCommand());
        await runtime.EnqueueAsync(new NextTurnCommand());
        var encounter = runtime.ActiveEncounter!;
        Assert.Equal(acting.Id, encounter.Participants[encounter.TurnIndex].ActorId);

        await runtime.EnqueueAsync(new ActNowCommand(delayer.Id));
        Assert.Equal(new[] { acted.Id, delayer.Id, acting.Id }, encounter.Participants.Select(p => p.ActorId));
        Assert.Equal(delayer.Id, encounter.Participants[encounter.TurnIndex].ActorId);

        // The order is now held by the numbers, so a later re-sort keeps it.
        await runtime.EnqueueAsync(new SetInitiativeCommand(acting.Id, 15, 0));
        Assert.Equal(new[] { acted.Id, delayer.Id, acting.Id }, encounter.Participants.Select(p => p.ActorId));

        await runtime.EnqueueAsync(new NextTurnCommand());
        Assert.Equal(acting.Id, encounter.Participants[encounter.TurnIndex].ActorId);
        Assert.Equal(1, encounter.Round);
    }

    [Fact]
    public async Task PlayerLog_LeavesOutHiddenActorsSheetEditsAndRemoval_AndKeepsOtherActorsNumbersVague_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var player = new CampaignAccess(campaignId, "player-9", CampaignRole.Player);
        var lich = await SeedAsync(campaignId, "Vecnarion the Lich", 100);
        lich.Visibility = ActorVisibility.Hidden;
        await _actors.SaveAsync(lich);
        var ally = await SeedAsync(campaignId, "Bram", 30, ActorKind.PlayerCharacter, s => s["level"] = 5L);
        var session = await SeedSessionAsync(campaignId);
        var dice = Substitute.For<IDiceRoller>();
        dice.Roll(Arg.Any<string>()).Returns(new DiceRollResult("1d8", new[] { 4 }, Array.Empty<int>(), 0, 4));
        await using var runtime = Runtime(campaignId, session, dice);
        runtime.Hydrate(new[] { lich, ally });

        var edited = (await StoredAsync(campaignId, lich.Id))!;
        edited.Biography = "Older than the kingdom.";
        await runtime.EnqueueAsync(new ReplaceActorCommand(edited, new Dictionary<string, object>(lich.Sheet), dm, Srd));
        await runtime.EnqueueAsync(new RemoveActorCommand(lich.Id));
        await runtime.EnqueueAsync(new ApplyDamageCommand(ally.Id, "HP", 20, "trap"));
        await runtime.EnqueueAsync(new GrantTemporaryHitPointsCommand(ally.Id, 11, "false life"));
        await runtime.EnqueueAsync(new SpendHitDiceCommand(ally.Id, 2, 8));

        var visible = await _actors.GetVisibleForCampaignAsync(player);
        var log = SessionEventRedactor.ForViewer(session.Events, player, visible);

        Assert.DoesNotContain(log, e => e.Description.Contains("Vecnarion"));
        var temporary = Assert.Single(log, e => e.Type == "TemporaryHitPoints");
        Assert.DoesNotContain("11", temporary.Description);
        var hitDice = Assert.Single(log, e => e.Type == "HitDice");
        Assert.DoesNotContain("12", hitDice.Description);

        // The DM still reads the full account.
        var dmLog = SessionEventRedactor.ForViewer(session.Events, dm, await _actors.GetVisibleForCampaignAsync(dm));
        Assert.Contains(dmLog, e => e.Type == "SheetEdited" && e.Description.Contains("Vecnarion"));
        Assert.Contains(dmLog, e => e.Type == "HitDice" && e.Description.Contains("recovered 12"));
    }

    [Fact]
    public async Task PlayerLog_NamesAStrangerByTheirPublicName_AndAHiddenSourceAsSomeone_Async()
    {
        // F20: the log stored real names, and a player could see every event about an actor they can see, strangers included.
        var campaignId = Guid.NewGuid().ToString();
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var player = new CampaignAccess(campaignId, "player-9", CampaignRole.Player);
        var stranger = await SeedAsync(campaignId, "Prince Aldric", 20, ActorKind.NonPlayerCharacter);
        stranger.Visibility = ActorVisibility.Unknown;
        stranger.PublicName = "the hooded man";
        await _actors.SaveAsync(stranger);
        var assassin = await SeedAsync(campaignId, "Silent Vess", 20);
        assassin.Visibility = ActorVisibility.Hidden;
        await _actors.SaveAsync(assassin);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { stranger, assassin });

        await runtime.EnqueueAsync(new ApplyDamageCommand(stranger.Id, "HP", 5, assassin.Id));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(stranger.Id, "Poisoned", assassin.Id, 2, EffectExpiry.EndOfTurn, stranger.Id));

        var log = SessionEventRedactor.ForViewer(session.Events, player, await _actors.GetVisibleForCampaignAsync(player));
        Assert.NotEmpty(log);
        Assert.All(log, e =>
        {
            Assert.DoesNotContain("Aldric", e.Description);
            Assert.DoesNotContain("Vess", e.Description);
            Assert.DoesNotContain(e.Parts.SelectMany(p => p.Args), arg => arg.Contains("Aldric") || arg.Contains("Vess"));
        });
        Assert.Contains(log, e => e.Type == "StatusEffect" && e.Description.Contains("the hooded man") && e.Description.Contains("someone"));

        // The DM reads the real names.
        var dmLog = SessionEventRedactor.ForViewer(session.Events, dm, await _actors.GetVisibleForCampaignAsync(dm));
        Assert.Contains(dmLog, e => e.Type == "StatusEffect" && e.Description.Contains("Prince Aldric") && e.Description.Contains("Silent Vess"));
    }

    [Fact]
    public async Task DamageTypes_MatchWholeWordsOnly_AndSkipConditionalEntries_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var golem = await SeedAsync(campaignId, "Golem", 100, sheet: s =>
        {
            s["resistances"] = "fire, cold; Bludgeoning, Piercing, and Slashing from Nonmagical Attacks";
            s["immunities"] = new List<object> { "Poisoned" };
        });
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { golem });

        await runtime.EnqueueAsync(new ApplyDamageCommand(golem.Id, "HP", 10, "wizard", "cold"));
        Assert.Equal(95, Hp(runtime, golem.Id));
        await runtime.EnqueueAsync(new ApplyDamageCommand(golem.Id, "HP", 10, "magic sword", "slashing"));
        Assert.Equal(85, Hp(runtime, golem.Id));
        await runtime.EnqueueAsync(new ApplyDamageCommand(golem.Id, "HP", 10, "spear", "piercing"));
        Assert.Equal(75, Hp(runtime, golem.Id));

        // Immune to the Poisoned condition is not immune to poison damage.
        await runtime.EnqueueAsync(new ApplyDamageCommand(golem.Id, "HP", 10, "dart", "poison"));
        Assert.Equal(65, Hp(runtime, golem.Id));
    }

    [Fact]
    public async Task Manager_ClosesALiveSessionLeftIdleForDays_AndStartsANewOne_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Last week" };
        await campaigns.SaveAsync(campaign);
        var lastWeek = await SeedSessionAsync(campaignId, SessionStatus.Live, "Last week");
        await _sessions.UpdateAsync(lastWeek.Id, s => s.Events.Add(new SessionEvent { Type = "Note", Description = "old", Timestamp = DateTime.UtcNow.AddDays(-2) }));
        await campaigns.UpdateAsync(campaignId, c => c.CurrentSessionId = lastWeek.Id);
        campaign.CurrentSessionId = lastWeek.Id;

        var runtime = await manager.GetOrStartAsync(campaign, new CampaignAccess(campaignId, "owner", CampaignRole.DM));

        Assert.NotEqual(lastWeek.Id, runtime.ActiveSession!.Id);
        Assert.Equal(SessionStatus.Completed, (await _sessions.GetAsync(lastWeek.Id))!.Status);
        await manager.EvictAsync(campaignId);
    }

    // F13 at the live table: the table writes what it is given, so the role is read from the stored campaign before it is.
    [Fact]
    public async Task SheetSave_AtALiveTable_IsRefusedForSomeoneRemovedFromTheCampaign_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Demoted", Members = { new CampaignMember { UserId = "co-dm", Role = CampaignRole.DM } } };
        await campaigns.SaveAsync(campaign);
        var owner = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var coDm = new CampaignAccess(campaignId, "co-dm", CampaignRole.DM);
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        await manager.GetOrStartAsync(campaign, owner);

        campaign.Members.RemoveAll(m => m.UserId == "co-dm");
        await campaigns.SaveAsync(campaign);
        var edited = new ActorDocument { Id = hero.Id, CampaignId = campaignId, Name = "Renamed by a stranger", Kind = ActorKind.PlayerCharacter, OwnerUserId = hero.OwnerUserId, Sheet = new Dictionary<string, object>(hero.Sheet) };

        Assert.False(await manager.TrySaveActorAsync(edited, hero.Sheet, coDm, Srd));
        await manager.EvictAsync(campaignId);
        Assert.Equal("Aria", (await StoredAsync(campaignId, hero.Id))!.Name);
    }

    [Fact]
    public async Task SheetSave_ForAnActorDeletedWhileItWasQueued_FailsCleanly_AndDoesNotBringItBack_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Race" };
        await campaigns.SaveAsync(campaign);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var runtime = await manager.GetOrStartAsync(campaign, dm);
        Assert.True(runtime.HasActor(hero.Id));

        // Hold the command loop so the delete's detach and the save queue up behind it, in that order -
        // the two steps TryDeleteActorAsync takes, done by hand so the order is certain.
        var gate = new BlockingCommand();
        var held = runtime.EnqueueAsync(gate);
        await gate.Entered.Task;
        Assert.True(await _actors.TryDeleteAsync(hero.Id, dm));
        var detached = runtime.EnqueueAsync(new RemoveActorCommand(hero.Id));
        var edited = new ActorDocument { Id = hero.Id, CampaignId = campaignId, Name = "Aria", Kind = ActorKind.PlayerCharacter, OwnerUserId = hero.OwnerUserId, Sheet = new Dictionary<string, object>(hero.Sheet) };
        var saved = manager.TrySaveActorAsync(edited, hero.Sheet, dm, Srd);
        gate.Release.SetResult();

        await held;
        await detached;
        Assert.False(await saved);
        Assert.Null(await StoredAsync(campaignId, hero.Id));
        await manager.EvictAsync(campaignId);
    }

    // ---- Table rules of the 2026-11-01 review (Phase 4) ----

    [Fact]
    public async Task RemovingWhoeverIsActing_PassesTheTurnAsNextTurnDoes_SkippingTheDeadAndBeginningTheNext_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var a = await SeedAsync(campaignId, "A", 10);
        var b = await SeedAsync(campaignId, "B", 10);
        var c = await SeedAsync(campaignId, "C", 10);
        var d = await SeedAsync(campaignId, "D", 10);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { a, b, c, d });
        await runtime.EnqueueAsync(new SetInitiativeCommand(a.Id, 20));
        await runtime.EnqueueAsync(new SetInitiativeCommand(b.Id, 15));
        await runtime.EnqueueAsync(new SetInitiativeCommand(c.Id, 10));
        await runtime.EnqueueAsync(new SetInitiativeCommand(d.Id, 5));
        await runtime.EnqueueAsync(new NextTurnCommand());
        await runtime.EnqueueAsync(new ApplyDamageCommand(c.Id, "HP", 10, ""));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(d.Id, "Frightened", "", 1, EffectExpiry.StartOfTurn, d.Id));
        Assert.True(Has(runtime, c.Id, "Dead"));

        // B flees on their own turn: C is dead and passed over, and D's turn begins, so its start-of-turn condition ends (R2).
        await runtime.EnqueueAsync(new RemoveParticipantCommand(b.Id));
        var encounter = runtime.ActiveEncounter!;
        Assert.Equal(d.Id, encounter.Participants[encounter.TurnIndex].ActorId);
        Assert.False(Has(runtime, d.Id, "Frightened"));
        Assert.Equal(1, encounter.Round);
    }

    [Fact]
    public async Task MarkingDead_GoesThroughTheRules_AndReviving_ComesBackOnOneHitPointWithNoFailedSaves_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero });
        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 15, ""));
        await runtime.EnqueueAsync(new RecordDeathSaveCommand(hero.Id, false, 2));
        Assert.Equal(2, runtime.Snapshot.Actor(hero.Id)!.Pool("DeathSaveFailures"));

        await runtime.EnqueueAsync(new KillCommand(hero.Id));
        Assert.True(Has(runtime, hero.Id, "Dead"));
        Assert.False(Has(runtime, hero.Id, "Unconscious"));
        Assert.Null(runtime.Snapshot.Actor(hero.Id)!.Pool("DeathSaveFailures"));

        // R3: removing "Dead" by hand left an unconscious character two failures from dying again.
        await runtime.EnqueueAsync(new ReviveCommand(hero.Id));
        Assert.False(Has(runtime, hero.Id, "Dead"));
        Assert.False(Has(runtime, hero.Id, "Unconscious"));
        Assert.Equal(1, Hp(runtime, hero.Id));
        Assert.Null(runtime.Snapshot.Actor(hero.Id)!.Pool("DeathSaveFailures"));
        Assert.Contains(session.Events, e => e.Type == "Killed" && e.Description == "Aria is dead.");
        Assert.Contains(session.Events, e => e.Type == "Revived");
    }

    [Fact]
    public async Task ADeath_EndsWhatTheCreatureConcentratedOn_AndTheEffectsCountedOnItsTurns_AsLeavingTheOrderDoes_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var cleric = await SeedAsync(campaignId, "Cleric", 9);
        var fighter = await SeedAsync(campaignId, "Fighter", 20, ActorKind.PlayerCharacter);
        var goblin = await SeedAsync(campaignId, "Goblin", 7);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { cleric, fighter, goblin });
        foreach (var (actor, roll) in new[] { (cleric, 20), (fighter, 15), (goblin, 10) })
        {
            await runtime.EnqueueAsync(new SetInitiativeCommand(actor.Id, roll));
        }

        // Bless on the fighter, held by the cleric's concentration; a curse on the goblin counted on the cleric's turns; and
        // a mark that lasts until removed.
        await runtime.EnqueueAsync(new AddStatusEffectCommand(fighter.Id, "Blessed", cleric.Id, 10, EffectExpiry.EndOfTurn, fighter.Id, HeldBy: cleric.Id));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(goblin.Id, "Cursed", cleric.Id, 3, EffectExpiry.EndOfTurn, cleric.Id));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(goblin.Id, "Marked", cleric.Id, 0, EffectExpiry.EndOfTurn, cleric.Id));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(goblin.Id, "Prone", fighter.Id, 2, EffectExpiry.EndOfTurn, fighter.Id));
        Assert.True(Has(runtime, cleric.Id, "Concentrating"));

        await runtime.EnqueueAsync(new ApplyDamageCommand(cleric.Id, "HP", 9, ""));
        Assert.True(Has(runtime, cleric.Id, "Dead"));
        Assert.False(Has(runtime, cleric.Id, "Concentrating"));
        Assert.False(Has(runtime, fighter.Id, "Blessed"));
        Assert.False(Has(runtime, goblin.Id, "Cursed"));
        Assert.True(Has(runtime, goblin.Id, "Marked"));

        // Leaving the order ends what was counted on the leaver's turns too.
        await runtime.EnqueueAsync(new RemoveParticipantCommand(fighter.Id));
        Assert.False(Has(runtime, goblin.Id, "Prone"));

        // And the change reached the stored goblin, not only the table's copy.
        var stored = (await StoredAsync(campaignId, goblin.Id))!;
        Assert.DoesNotContain(_registry.Hydrate(stored.State).OfType<ActiveEffectsComponent>().Single().Effects, e => e.EffectId == "Cursed");
    }

    [Fact]
    public async Task Concentration_AHitAsksForASave_RemovingItEndsWhatItHeld_AndGoingDownLosesIt_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var wizard = await SeedAsync(campaignId, "Wizard", 20, ActorKind.PlayerCharacter);
        var goblin = await SeedAsync(campaignId, "Goblin", 7);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { wizard, goblin });

        await runtime.EnqueueAsync(new AddStatusEffectCommand(goblin.Id, "Restrained", wizard.Id, 10, EffectExpiry.EndOfTurn, goblin.Id, HeldBy: wizard.Id));
        var hit = new ApplyDamageCommand(wizard.Id, "HP", 8, "");
        await runtime.EnqueueAsync(hit);
        Assert.Equal(10, hit.ConcentrationDc);
        Assert.Contains(session.Events, e => e.Type == "Damage" && e.Description.Contains("Wizard is concentrating: a DC 10 save keeps it."));

        await runtime.EnqueueAsync(new RemoveStatusEffectCommand(wizard.Id, "Concentrating"));
        Assert.False(Has(runtime, goblin.Id, "Restrained"));
        Assert.Contains(session.Events, e => e.Type == "StatusEffectRemoved" && e.Description.Contains("One effect it held up ended."));

        await runtime.EnqueueAsync(new AddStatusEffectCommand(goblin.Id, "Restrained", wizard.Id, 10, EffectExpiry.EndOfTurn, goblin.Id, HeldBy: wizard.Id));
        var downing = new ApplyDamageCommand(wizard.Id, "HP", 40, "");
        await runtime.EnqueueAsync(downing);
        Assert.Null(downing.ConcentrationDc);
        Assert.False(Has(runtime, goblin.Id, "Restrained"));
        Assert.Contains(session.Events, e => e.Type == "Damage" && e.Description.Contains("Wizard lost concentration."));
    }

    [Fact]
    public async Task Undo_PutsBackAMistypedHit_IncludingGoingDown_ButNotOnceSomethingElseChangedTheCreature_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { hero });
        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 10));

        // 21 typed for 12: down, death saves begun. Undo puts it all back (R5).
        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 21, ""));
        Assert.True(Has(runtime, hero.Id, "Unconscious"));
        Assert.Equal("Damage", runtime.Snapshot.Actor(hero.Id)!.UndoKind);
        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));
        Assert.Equal(12, Hp(runtime, hero.Id));
        Assert.False(Has(runtime, hero.Id, "Unconscious"));
        Assert.Null(runtime.Snapshot.Actor(hero.Id)!.UndoKind);
        Assert.Equal(12, Codex.Core.YamlValues.GetInt((await StoredAsync(campaignId, hero.Id))!.Sheet, "HP"));

        // A hit, then the turn ends and a condition wears off: the hit can no longer be taken back on its own.
        await runtime.EnqueueAsync(new AddStatusEffectCommand(hero.Id, "Prone", "", 1, EffectExpiry.EndOfTurn, hero.Id));
        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 3, ""));
        await runtime.EnqueueAsync(new NextTurnCommand());
        Assert.Null(runtime.Snapshot.Actor(hero.Id)!.UndoKind);
        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));
        Assert.Equal(9, Hp(runtime, hero.Id));
        Assert.Contains(session.Events, e => e.Type == "Undo" && e.Description.Contains("nothing to undo"));
    }

    [Fact]
    public async Task APoolsMaximum_IsWhereverTheSystemSaysItIs_ForHealingAndSpendingAlike_Async()
    {
        var system = new SystemDefinition { Id = "wounds", Resources = { new ResourceDefinition { Key = "Wounds", MaxKey = "WoundCap", IsPrimary = true } } };
        Assert.Equal("WoundCap", system.MaxKeyOf("Wounds"));
        Assert.Equal("slots_1_Max", system.MaxKeyOf("slots_1"));

        var campaignId = Guid.NewGuid().ToString();
        var pool = new ResourcePoolComponent();
        pool.Set("Wounds", 2);
        pool.Set("WoundCap", 5);
        var ferryman = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Ferryman", State = _registry.Snapshot(new object[] { pool }) };
        await _actors.SaveAsync(ferryman);
        await using var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, system: system);
        runtime.Hydrate(new[] { ferryman });

        // R9: damage was capped by WoundCap while healing looked for "Wounds_Max" and had no cap at all.
        await runtime.EnqueueAsync(new ApplyHealingCommand(ferryman.Id, "Wounds", 10, ""));
        Assert.Equal(5, runtime.Snapshot.Actor(ferryman.Id)!.Pool("Wounds"));
        await runtime.EnqueueAsync(new ApplyDamageCommand(ferryman.Id, "Wounds", 4, ""));
        await runtime.EnqueueAsync(new AdjustResourceCommand(ferryman.Id, "Wounds", 10, "wounds"));
        Assert.Equal(5, runtime.Snapshot.Actor(ferryman.Id)!.Pool("Wounds"));
    }


    [Fact]
    public async Task ASpendLargerThanWhatIsLeft_IsRefused_SoTwoSpendersAtTheLastPointCannotBothSpendIt_AndARegainStopsAtTheMaximum_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        // A feature's counter of uses on the sheet becomes a table pool like a slot level, under the same key.
        var monk = await SeedAsync(campaignId, "Monk", 20, ActorKind.PlayerCharacter, sheet =>
        {
            sheet["slots_1"] = 1L;
            sheet["slots_1_Max"] = 2L;
            sheet[SheetRuleEngine.UsesPrefix + "focus"] = 2L;
            sheet[SheetRuleEngine.UsesPrefix + "focus" + SheetRuleEngine.UsesMaxSuffix] = 3L;
        });
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session);
        runtime.Hydrate(new[] { monk });

        // Two people press Cast on the last slot at once: the first spends it, the second is refused and changes nothing.
        var first = new AdjustResourceCommand(monk.Id, "slots_1", -1, "spell slot");
        var second = new AdjustResourceCommand(monk.Id, "slots_1", -1, "spell slot");
        await Task.WhenAll(runtime.EnqueueAsync(first), runtime.EnqueueAsync(second));
        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.NotNull(second.Failed);
        Assert.Equal(0, runtime.Snapshot.Actor(monk.Id)!.Pool("slots_1"));
        Assert.Contains(session.Events, e => e.Type == "ResourceSpent" && e.Description.Contains("could not use"));

        // Spending 3 of a counter's 2 is refused, not clamped to 0; spending 2 of 2 is allowed.
        var overspend = new AdjustResourceCommand(monk.Id, "uses_focus", -3, "Focus");
        await runtime.EnqueueAsync(overspend);
        Assert.NotNull(overspend.Failed);
        Assert.Equal(2, runtime.Snapshot.Actor(monk.Id)!.Pool("uses_focus"));
        var exact = new AdjustResourceCommand(monk.Id, "uses_focus", -2, "Focus");
        await runtime.EnqueueAsync(exact);
        Assert.True(exact.Succeeded);
        Assert.Equal(0, runtime.Snapshot.Actor(monk.Id)!.Pool("uses_focus"));

        // A regain stops at the maximum, as before.
        var regain = new AdjustResourceCommand(monk.Id, "uses_focus", 5, "Focus");
        await runtime.EnqueueAsync(regain);
        Assert.True(regain.Succeeded);
        Assert.Equal(3, runtime.Snapshot.Actor(monk.Id)!.Pool("uses_focus"));
    }

    [Fact]
    public async Task RevealingACreatureMidFight_ReachesTheTable_SoEveryTablePageReloadsWhoItMaySee_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Reveal" };
        await campaigns.SaveAsync(campaign);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var runtime = await manager.GetOrStartAsync(campaign, dm);
        var goblin = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Grisk", Kind = ActorKind.Monster, Visibility = ActorVisibility.Hidden };
        Assert.True(await manager.TryCreateActorAsync(goblin, dm));
        var roster = runtime.Snapshot.RosterVersion;

        Assert.False(await manager.TrySetVisibilityAsync(goblin.Id, ActorVisibility.Known, new CampaignAccess(campaignId, "player-1", CampaignRole.Player)));
        Assert.True(await manager.TrySetVisibilityAsync(goblin.Id, ActorVisibility.Unknown, dm));

        Assert.NotEqual(roster, runtime.Snapshot.RosterVersion);
        Assert.Equal(ActorVisibility.Unknown, runtime.GetActorDocument(goblin.Id)!.Visibility);
        Assert.Equal(ActorVisibility.Unknown, (await StoredAsync(campaignId, goblin.Id))!.Visibility);
        // A stranger's real name stays out of what players read of the log.
        var reveal = runtime.ActiveSession!.Events.Last(e => e.Type == "ActorVisibility");
        Assert.DoesNotContain("Grisk", reveal.PlayerDescription);
        await manager.EvictAsync(campaignId);
    }

    [Fact]
    public async Task Retiring_TakesAnActorOffTheTableButKeepsIt_AndTheDmCanBringItBack_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Retire", Members = { new CampaignMember { UserId = "player-1", Role = CampaignRole.Player } } };
        await campaigns.SaveAsync(campaign);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var player = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        var hero = await SeedAsync(campaignId, "Aria", 12, ActorKind.PlayerCharacter);
        var goblin = await SeedAsync(campaignId, "Goblin", 7);
        var runtime = await manager.GetOrStartAsync(campaign, dm);
        await runtime.EnqueueAsync(new SetInitiativeCommand(goblin.Id, 12));

        Assert.False(await manager.TryRetireActorAsync(goblin.Id, player));
        Assert.True(await manager.TryRetireActorAsync(goblin.Id, dm));
        Assert.False(runtime.HasActor(goblin.Id));
        Assert.Empty(runtime.ActiveEncounter!.Participants);
        var kept = (await StoredAsync(campaignId, goblin.Id))!;
        Assert.True(kept.Retired);
        Assert.Contains(await _actors.GetVisibleForCampaignAsync(dm), a => a.Id == goblin.Id && a.Retired);

        // A player retires their own character; only the DM brings anyone back.
        Assert.True(await manager.TryRetireActorAsync(hero.Id, player));
        Assert.False(await manager.TryRestoreActorAsync(hero.Id, player));
        Assert.True(await manager.TryRestoreActorAsync(goblin.Id, dm));
        Assert.True(runtime.HasActor(goblin.Id));
        Assert.False((await StoredAsync(campaignId, goblin.Id))!.Retired);

        // A table started later leaves the retired out.
        await manager.EvictAsync(campaignId);
        var again = await manager.GetOrStartAsync(campaign, dm);
        Assert.False(again.HasActor(hero.Id));
        Assert.True(again.HasActor(goblin.Id));
        await manager.EvictAsync(campaignId);
    }

    private sealed class BlockingCommand : IRuntimeCommand
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

        public void Apply(CampaignRuntime runtime)
        {
            Entered.SetResult();
            Release.Task.Wait();
        }

        public SessionEvent Describe(CampaignRuntime runtime) => new() { Type = "Test", Description = "held" };
    }
}
