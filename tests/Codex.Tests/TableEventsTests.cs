using Codex.Core.Components;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The live table raising a system's declared events (<see cref="TableEvents"/>, overhaul step 6): damage, healing and turns go to
/// the system's handlers when it has sheet kinds, the intents land where the table keeps each thing (numeric tracks in the pool,
/// slots and the sheet's conditions on the stored sheet, conditions as statuses), what is left for the table is logged, and
/// undo puts all of it back.
/// </summary>
[Collection("RavenDb")]
public class TableEventsTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly ComponentRegistry _registry;

    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: table-events
        name: Table events
        conditions: [Dead, Hurt, Down]
        moments: [{ key: downtime, label: Downtime }]
        sheetKinds:
          - kind: hero
            label: Hero
            tracks:
              - { key: armor, max: 3, display: boxes }
              - { key: HP, max: 10, min: -5, start: max, thresholds: [{ below: 0, condition: Down }] }
              - { key: stress, max: 3, overflow: { into: trauma, then: reset, ask: { who: gm, choices: [cold, haunted] } } }
              - { key: trauma, max: 4 }
              - key: harm
                display: slots
                holds: { type: text }
                levels: [{ level: 1, slots: 1, condition: Hurt }]
                whenFull: pushUp
                beyond: { condition: Dead }
        events:
          downtime:
            - { intents: [{ set: { track: armor, to: 0 } }] }
          damage: { builtin: damage }
          heal:
            - { intents: [{ adjust: { track: HP, by: "ev.amount" } }] }
          assist:
            - { intents: [{ adjust: { actor: "ev.helper", track: stress, by: 1 } }, { log: { key: assisted, args: [ev.helper] } }] }
          turnStart:
            - { when: "HP < 0", intents: [{ roll: { definition: deathSave } }] }
        rolls:
          - { key: deathSave, dice: 1d10 }
          - key: push
            dice: 1d6
            outcomes: [{ name: high, when: "total >= 4" }, { name: low }]
            then: [{ intents: [{ adjust: { track: stress, by: 2 } }] }]
        """);

    public TableEventsTests(RavenDbFixture fixture)
    {
        _dbService = new RavenDbService(fixture.DbPath, "TableEvents_" + Guid.NewGuid(), runInMemory: true);
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    private async Task<ActorDocument> HeroAsync(string campaignId, string name, params (string Key, object Value)[] sheet)
    {
        var doc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = name, Kind = ActorKind.PlayerCharacter };
        doc.Sheet["kind"] = "hero";
        doc.Sheet["HP"] = 10L;
        doc.Sheet["HP_Max"] = 10L;
        doc.Sheet["stress"] = 0L;
        doc.Sheet["trauma"] = 0L;
        foreach (var (key, value) in sheet) doc.Sheet[key] = value;
        ActorSheetSync.ApplySheetToState(doc, System, _registry);
        await _actors.SaveAsync(doc);
        return doc;
    }

    private async Task<(CampaignRuntime Runtime, SessionDocument Session)> TableAsync(string campaignId, params ActorDocument[] actors)
    {
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: System);
        runtime.Hydrate(actors);
        return (runtime, session);
    }

    private async Task<ActorDocument> StoredAsync(string campaignId, string id) =>
        (await _actors.GetVisibleAsync(id, new CampaignAccess(campaignId, "dm", CampaignRole.DM)))!;

    private static int Pool(CampaignRuntime runtime, string actorId, string key) => runtime.GetEntity(actorId)!.Value.Get<ResourcePoolComponent>().Get(key);

    private async Task<List<string>> LogAsync(SessionDocument session) =>
        (await _sessions.GetAsync(session.Id))!.Events.Select(e => e.Description).ToList();

    [Fact]
    public async Task Damage_GoesToTheSystemsHandler_AndTheTrackMayGoBelowZero()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId, "Vex");
        var (runtime, session) = await TableAsync(campaignId, hero);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 12, "dm"));

        // The vitals rules would have stopped at zero; the track's own minimum is -5.
        Assert.Equal(-2, Pool(runtime, hero.Id, "HP"));
        Assert.Equal(-2, Codex.Core.YamlValues.GetInt(await StoredAsync(campaignId, hero.Id).ContinueWith(t => t.Result.Sheet), "HP"));
        Assert.Contains("Vex: HP 10 → -2.", await LogAsync(session));

        await runtime.EnqueueAsync(new ApplyHealingCommand(hero.Id, "HP", 5, "dm"));
        Assert.Equal(3, Pool(runtime, hero.Id, "HP"));
    }

    [Fact]
    public async Task ADeclaredMoment_RefillsTheCountersThatNameIt_RunsItsHandler_IsLogged_AndUndoPutsItAllBack()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId, "Vex", ("armor", 2L), ("uses_scheme", 0L), ("uses_scheme_Max", 2L), ("uses_scheme_Rest", "downtime"), ("uses_other", 0L), ("uses_other_Max", 2L), ("uses_other_Rest", "dawn"));
        var (runtime, session) = await TableAsync(campaignId, hero);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new MomentCommand(hero.Id, "downtime"));

        Assert.Equal(2, Pool(runtime, hero.Id, "uses_scheme"));
        Assert.Equal(0, Pool(runtime, hero.Id, "uses_other")); // it names dawn, not downtime
        Assert.Equal(0, Pool(runtime, hero.Id, "armor")); // the downtime handler reset it
        Assert.Contains(await LogAsync(session), d => d.StartsWith("Downtime for Vex:", StringComparison.Ordinal));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));
        Assert.Equal(0, Pool(runtime, hero.Id, "uses_scheme"));
        Assert.Equal(2, Pool(runtime, hero.Id, "armor"));
    }

    [Fact]
    public async Task ADeclaredMomentThatChangesNothing_IsStillLogged()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId, "Vex");
        var (runtime, session) = await TableAsync(campaignId, hero);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new MomentCommand(hero.Id, "downtime"));

        Assert.Contains(await LogAsync(session), d => d.StartsWith("Downtime for Vex", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dawn_LogsAsItAlwaysDid_AndRaisesNoEventTheSystemDoesNotHandle()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId, "Vex", ("armor", 2L));
        var (runtime, session) = await TableAsync(campaignId, hero);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new MomentCommand(hero.Id, "dawn"));

        Assert.Equal(2, Pool(runtime, hero.Id, "armor"));
        Assert.Contains("Dawn comes for Vex.", await LogAsync(session));
    }

    [Fact]
    public async Task ADirectMark_WritesTheSlotTrackOnTheStoredSheet_AndBeyondTheLastLevelBringsACondition_AndUndoTakesItAllBack()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId, "Vex", ("harm", new Dictionary<string, object> { ["1"] = new List<object> { "Bruised" } }), ("notes", "keep me"));
        var (runtime, session) = await TableAsync(campaignId, hero);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "mark", new Dictionary<string, object?> { ["track"] = "harm", ["level"] = 1, ["text"] = "Broken arm" }));

        // Level 1 is full and pushes up, past the last level: the harm stays as it was and the hero is dead.
        var stored = await StoredAsync(campaignId, hero.Id);
        Assert.Equal(new object[] { "Dead" }, (List<object>)stored.Sheet["conditions"]);
        Assert.Equal("keep me", stored.Sheet["notes"]);
        Assert.True(Codex.Core.CodexWorld.HasStatus(runtime.GetEntity(hero.Id)!.Value, "Dead"));
        Assert.Contains("Vex is now Dead.", await LogAsync(session));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));
        stored = await StoredAsync(campaignId, hero.Id);
        Assert.False(stored.Sheet.ContainsKey("conditions"));
        Assert.False(Codex.Core.CodexWorld.HasStatus(runtime.GetEntity(hero.Id)!.Value, "Dead"));
    }

    [Fact]
    public async Task AMarkInAFreeSlot_IsStored_AndAnOverflowsQuestion_IsLoggedForTheGm()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId, "Vex", ("stress", 3L));
        var (runtime, session) = await TableAsync(campaignId, hero);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "mark", new Dictionary<string, object?> { ["track"] = "harm", ["text"] = "Cut" }));
        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "adjust", new Dictionary<string, object?> { ["track"] = "stress", ["by"] = 1 }));

        var stored = await StoredAsync(campaignId, hero.Id);
        var harm = (Dictionary<string, object>)stored.Sheet["harm"];
        Assert.Equal(new object[] { "Cut" }, (List<object>)harm["1"]);
        Assert.Equal((0, 1), (Pool(runtime, hero.Id, "stress"), Pool(runtime, hero.Id, "trauma")));
        var log = await LogAsync(session);
        Assert.Contains("Vex: harm holds Cut.", log);
        Assert.Contains(log, l => l.Contains("Vex: stress 3 → 0.") && l.Contains("Vex: trauma 0 → 1.") && l.Contains("The GM chooses for Vex: cold, haunted."));
    }

    [Fact]
    public async Task AnIntentForAnotherActor_IsAppliedToThem_AndALogLineIsSaid()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId, "Vex");
        var helper = await HeroAsync(campaignId, "Lurk");
        var (runtime, session) = await TableAsync(campaignId, hero, helper);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "assist", new Dictionary<string, object?> { ["helper"] = helper.Id }));

        Assert.Equal(1, Pool(runtime, helper.Id, "stress"));
        Assert.Equal(0, Pool(runtime, hero.Id, "stress"));
        Assert.Equal(1, Codex.Core.YamlValues.GetInt((await StoredAsync(campaignId, helper.Id)).Sheet, "stress"));
        var log = await LogAsync(session);
        Assert.Contains(log, l => l.Contains("Lurk: stress 0 → 1.") && l.Contains("Vex: assisted Lurk"));
    }

    [Fact]
    public async Task ATurnStarting_RaisesTurnStart_ForWhoseTurnItIs()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId, "Vex", ("HP", -1L));
        var other = await HeroAsync(campaignId, "Lurk");
        var (runtime, session) = await TableAsync(campaignId, hero, other);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new SetInitiativeCommand(other.Id, 20));
        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 10));
        await runtime.EnqueueAsync(new NextTurnCommand());

        Assert.Contains(await LogAsync(session), l => l.Contains("Vex is to roll deathSave.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHandlerThatCannotRun_IsLogged_AndChangesNothing()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId, "Vex");
        var (runtime, session) = await TableAsync(campaignId, hero);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "heal", new Dictionary<string, object?>()));

        Assert.Equal(10, Pool(runtime, hero.Id, "HP"));
        Assert.Contains(await LogAsync(session), l => l.StartsWith("Vex: the system rule for heal could not run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AScriptedHandler_RollsThroughTheTable_WhichLogsTheFaces_AndAScriptThatFails_ChangesNothing()
    {
        var system = SystemDefinitionLoader.Parse("""
            id: table-scripts
            name: Table scripts
            functions:
              bonus: { formula: "(x) => x + 1" }
            sheetKinds:
              - kind: hero
                label: Hero
                tracks:
                  - { key: HP, max: 10, min: -5, start: max }
                keyedLists:
                  - key: skills
                    label: Skills
                    itemFields: [{ key: level, type: Number, min: 0, max: 5 }]
                    keys: [{ key: climb, label: Climb }]
            events:
              train: { script: rules.js#train }
              burn: { script: rules.js#burn }
              hang: { script: rules.js#hang }
            """);
        system.AddScript("rules.js", """
            export function burn(ev, sheet, ctx) { return [{ adjust: { track: "HP", by: -ctx.call("bonus", ctx.d(6)) } }]; }
            export function hang() { for (;;) {} }
            export function train(ev, sheet) { return [{ set: { list: "skills", key: "climb", field: "level", to: sheet.lists.skills.climb.level + 2 } }]; }
            """);
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId, "Vex");
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: system);
        runtime.Hydrate(new[] { hero });
        await using var _ = runtime;

        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "burn", new Dictionary<string, object?>(), new SequenceDiceSource(new[] { 4 })));
        Assert.Equal(5, Pool(runtime, hero.Id, "HP"));
        Assert.Contains("Vex: the burn rule rolled d6: 4. Vex: HP 10 → 5.", await LogAsync(session));
        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));
        Assert.Equal(10, Pool(runtime, hero.Id, "HP"));

        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "train", new Dictionary<string, object?>()));
        Assert.Contains("Vex: Skills changed.", await LogAsync(session));
        var skills = (IDictionary<string, object>)(await StoredAsync(campaignId, hero.Id)).Sheet["skills"];
        Assert.Equal(2L, Codex.Core.YamlValues.ToNumber(((IDictionary<string, object>)skills["climb"])["level"]) is { } level ? (long)level : -1);

        var hang = new RaiseEventCommand(hero.Id, "hang", new Dictionary<string, object?>());
        await runtime.EnqueueAsync(hang);

        Assert.Equal(10, Pool(runtime, hero.Id, "HP"));
        Assert.Contains("ran more than 100000 statements", hang.Failed);
        Assert.Contains(await LogAsync(session), l => l.StartsWith("Vex: the system rule for hang could not run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHandler_CountsBesideTheSheet_EndsWhatTheCreatureHeld_AndKillsIt()
    {
        var system = SystemDefinitionLoader.Parse("""
            id: table-kills
            name: Table kills
            conditions: [Dead, Blessed]
            sheetKinds:
              - kind: hero
                label: Hero
                tracks:
                  - { key: HP, max: 10, min: -5, start: max }
            events:
              fall:
                - { intents: [{ counter: { key: falls, by: 1 } }, { endConcentration: {} }] }
                - { when: "HP <= 0", intents: [{ kill: { condition: Dead } }] }
            """);
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId, "Vex");
        var friend = await HeroAsync(campaignId, "Ora");
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: system);
        runtime.Hydrate(new[] { hero, friend });
        await using var _ = runtime;
        var ora = runtime.GetEntity(friend.Id)!.Value;
        runtime.World.AddStatus(ora, "Blessed", "bless", Codex.Core.Components.ActiveEffect.Indefinite, Codex.Core.Components.EffectExpiry.EndOfTurn, hero.Id, heldBy: hero.Id);

        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "fall", new Dictionary<string, object?>()));

        Assert.Equal(1, Pool(runtime, hero.Id, "falls"));
        Assert.False(Codex.Core.CodexWorld.HasStatus(ora, "Blessed"));
        Assert.False(Codex.Core.CodexWorld.HasStatus(runtime.GetEntity(hero.Id)!.Value, "Dead"));
        Assert.Contains("Vex: falls 0 → 1. Vex stops concentrating; 1 held effects end.", await LogAsync(session));

        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 12, "dm"));
        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "fall", new Dictionary<string, object?>()));

        Assert.Equal(2, Pool(runtime, hero.Id, "falls"));
        Assert.True(Codex.Core.CodexWorld.HasStatus(runtime.GetEntity(hero.Id)!.Value, "Dead"));
        Assert.Contains(await LogAsync(session), l => l.EndsWith("Vex dies.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARollAtTheTable_IsLoggedWithItsOutcome_AndWhatItCostsIsApplied()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId, "Vex");
        var (runtime, session) = await TableAsync(campaignId, hero);
        await using var _ = runtime;

        var command = new MakeRollCommand(hero.Id, "player", new Codex.Core.Rules.SheetRollRequest { Definition = "push" },
            Dice: new Codex.Plugin.Abstractions.Dice.SequenceDiceSource(new[] { 5 }));
        await runtime.EnqueueAsync(command);

        Assert.Equal("high", command.Rolled!.Outcome);
        Assert.Equal(2, Pool(runtime, hero.Id, "stress"));
        var entry = Assert.Single(runtime.Snapshot.RollLog);
        Assert.Equal(("high", 5, "player"), (entry.Outcome, entry.Total, entry.RollerUserId));
        Assert.Contains(await LogAsync(session), l => l.Contains("Vex rolled push: high (5).") && l.Contains("Vex: stress 0 → 2."));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));
        Assert.Equal(0, Pool(runtime, hero.Id, "stress"));
    }
}
