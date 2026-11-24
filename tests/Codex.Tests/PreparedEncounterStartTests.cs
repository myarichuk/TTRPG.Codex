using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Codex.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>
/// Starting a prepared encounter at the table (ENCOUNTER_BUILDER_DESIGN.md 4.2, 15): the command that sets the place and plan and rolls each creature's initiative on the
/// loop, and the service that makes the creatures, hands them over and marks the plan as run. Real runtime, real actor store (embedded Raven), the real SRD definition.
/// </summary>
[Collection("RavenDb")]
public class PreparedEncounterStartTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private static readonly SystemDefinition Srd = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

    private readonly RavenDbService _db;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenEncounterRepository _encounters;
    private readonly ComponentRegistry _registry;

    public PreparedEncounterStartTests(RavenDbFixture fixture)
    {
        _db = fixture.Open("PreparedStart_" + Guid.NewGuid());
        _actors = new ActorRepository(_db);
        _sessions = new RavenSessionRepository(_db);
        _encounters = new RavenEncounterRepository(_db);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<AttributesComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _db.Dispose();

    // ---- The command, on a runtime built directly ----

    private async Task<ActorDocument> SeedAsync(string campaignId, string name, long dex, ActorKind kind = ActorKind.Monster, ActorVisibility visibility = ActorVisibility.Hidden, string? publicName = null)
    {
        var doc = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = name, Kind = kind, Visibility = visibility, PublicName = publicName,
            OwnerUserId = kind == ActorKind.PlayerCharacter ? "player-1" : null,
            Sheet = new Dictionary<string, object> { ["HP"] = 12L, ["HP_Max"] = 12L, ["con"] = 12L, ["dex"] = dex }
        };
        ActorSheetSync.ApplySheetToState(doc, Srd, _registry);
        await _actors.SaveAsync(doc);
        return doc;
    }

    private async Task<SessionDocument> SeedSessionAsync(string campaignId)
    {
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        return session;
    }

    private CampaignRuntime Runtime(string campaignId, SessionDocument session, IDiceRoller? dice = null) =>
        new(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, dice, Srd);

    private static IDiceRoller ScriptedDice()
    {
        var dice = Substitute.For<IDiceRoller>();
        dice.Roll(Arg.Any<string>()).Returns(ci => new DiceRollResult(ci.Arg<string>(), new[] { 10 }, Array.Empty<int>(), 0, 10));
        return dice;
    }

    /// <summary>The expression the console's own path works out for an actor: its snapshot's live sheet, the table's resolver and linked sheets.</summary>
    private static string ConsoleExpression(CampaignRuntime runtime, ActorDocument doc) =>
        TableInitiative.Expression("1d20", TableInitiative.Bonus(Srd, new SheetRuleEngine(Srd, runtime.Resolver), () => runtime.Snapshot.Actor(doc.Id)!.LiveView(Srd, doc.Sheet), () => runtime.Linked));

    private static StartPreparedEncounterCommand Start(string planId, string? place, params ActorDocument[] who) =>
        new(planId, "Cellar ambush", place, place == "cellar" ? "Cellar" : "", who.Select(a => a.Id).ToList(), "gm");

    [Fact]
    public async Task Start_SetsThePlaceAndPlan_AndRollsEachActorWithTheConsolesExpressionAndTieBreak_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var quick = await SeedAsync(campaignId, "Ogre", 14);
        var slow = await SeedAsync(campaignId, "Imp", 8);
        var session = await SeedSessionAsync(campaignId);
        var dice = ScriptedDice();
        await using var runtime = Runtime(campaignId, session, dice);
        runtime.Hydrate(new[] { quick, slow });
        var expectedQuick = ConsoleExpression(runtime, quick);
        var expectedSlow = ConsoleExpression(runtime, slow);
        Assert.NotEqual(expectedQuick, expectedSlow);

        var command = Start("plan-1", "cellar", quick, slow);
        await runtime.EnqueueAsync(command);

        Assert.Null(command.Refusal);
        Assert.Equal("cellar", runtime.ActiveEncounter!.LocationId);
        Assert.Equal("plan-1", runtime.ActiveEncounter.PreparedEncounterId);
        Assert.Equal(command.EncounterId, runtime.ActiveEncounter.Id);
        Assert.Equal("plan-1", runtime.Snapshot.Encounter!.PreparedEncounterId);
        Assert.Equal(2, runtime.ActiveEncounter.Participants.Count);
        Assert.Equal(TableInitiative.TieBreak(Srd, quick.Sheet), runtime.ActiveEncounter.Participants.Single(p => p.ActorId == quick.Id).TieBreak);
        Assert.Equal(TableInitiative.TieBreak(Srd, slow.Sheet), runtime.ActiveEncounter.Participants.Single(p => p.ActorId == slow.Id).TieBreak);
        dice.Received(1).Roll(expectedQuick);
        dice.Received(1).Roll(expectedSlow);
        Assert.Equal(new[] { expectedQuick, expectedSlow }, session.RollLog.Select(r => r.Expression));
    }

    [Fact]
    public async Task AConditionAtTheTable_ReachesTheStartsRoll_AsItReachesTheConsoles_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var fighter = await SeedAsync(campaignId, "Ogre", 14);
        var session = await SeedSessionAsync(campaignId);
        var dice = ScriptedDice();
        await using var runtime = Runtime(campaignId, session, dice);
        runtime.Hydrate(new[] { fighter });
        var before = ConsoleExpression(runtime, fighter);

        await runtime.EnqueueAsync(new AdjustConditionCommand(fighter.Id, "Exhaustion", 1));
        var after = ConsoleExpression(runtime, runtime.GetActorDocument(fighter.Id)!);
        Assert.NotEqual(before, after);

        await runtime.EnqueueAsync(Start("plan-1", "cellar", fighter));

        dice.Received(1).Roll(after);
        dice.DidNotReceive().Roll(before);
    }

    [Fact]
    public async Task Start_IsRefused_WhenAnEncounterWithParticipantsRuns_AndChangesNothing_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var first = await SeedAsync(campaignId, "Ogre", 14);
        var second = await SeedAsync(campaignId, "Imp", 8);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session, ScriptedDice());
        runtime.Hydrate(new[] { first, second });
        await runtime.EnqueueAsync(new RollInitiativeCommand(first.Id, "1d20", "gm"));

        var command = Start("plan-1", "cellar", second);
        await runtime.EnqueueAsync(command);

        Assert.Equal(PreparedStartRefusal.EncounterRunning, command.Refusal);
        Assert.Single(runtime.ActiveEncounter!.Participants);
        Assert.Null(runtime.ActiveEncounter.LocationId);
        Assert.Null(runtime.ActiveEncounter.PreparedEncounterId);
        var line = session.Events.Last();
        Assert.Equal("EncounterStarted", line.Type);
        Assert.Equal("Encounter.StartRefused", line.Parts[0].Key);
        Assert.StartsWith(EventTerm.GmOnlyKind + ":", line.Parts[0].Kinds[0]);
    }

    [Fact]
    public async Task Start_ReusesAnEmptyLiveEncounter_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var first = await SeedAsync(campaignId, "Ogre", 14);
        var second = await SeedAsync(campaignId, "Imp", 8);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session, ScriptedDice());
        runtime.Hydrate(new[] { first, second });
        await runtime.EnqueueAsync(new RollInitiativeCommand(first.Id, "1d20", "gm"));
        var encounterId = runtime.ActiveEncounter!.Id;
        await runtime.EnqueueAsync(new RemoveParticipantCommand(first.Id));
        Assert.Empty(runtime.ActiveEncounter!.Participants);

        var command = Start("plan-1", "cellar", second);
        await runtime.EnqueueAsync(command);

        Assert.Null(command.Refusal);
        Assert.Equal(encounterId, command.EncounterId);
        Assert.Equal(encounterId, runtime.ActiveEncounter!.Id);
        Assert.Equal("plan-1", runtime.ActiveEncounter.PreparedEncounterId);
        Assert.Equal(second.Id, Assert.Single(runtime.ActiveEncounter.Participants).ActorId);
    }

    [Fact]
    public async Task AnActorTheTableDoesNotHold_IsSkipped_AndNobodyHeldIsRefused_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var held = await SeedAsync(campaignId, "Ogre", 14);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session, ScriptedDice());
        runtime.Hydrate(new[] { held });

        var nobody = new StartPreparedEncounterCommand("plan-1", "Cellar ambush", "cellar", "Cellar", new[] { "ghost-1", "ghost-2" }, "gm");
        await runtime.EnqueueAsync(nobody);

        Assert.Equal(PreparedStartRefusal.NobodyAtTable, nobody.Refusal);
        Assert.Null(runtime.ActiveEncounter);
        Assert.Equal("Encounter.StartEmpty", session.Events.Last().Parts[0].Key);

        var some = new StartPreparedEncounterCommand("plan-1", "Cellar ambush", "cellar", "Cellar", new[] { held.Id, "ghost-1" }, "gm");
        await runtime.EnqueueAsync(some);

        Assert.Null(some.Refusal);
        Assert.Equal(new[] { "ghost-1" }, some.Skipped);
        Assert.Equal(new[] { held.Id }, some.Joined);
        Assert.Contains(session.Events, e => e.Type == "EncounterStarted" && e.Parts.Any(p => p.Key == "Encounter.NotJoined"));
    }

    [Fact]
    public async Task ThePlayersLog_HasNoStartLine_AndEachCreaturesInitiativeAsToday_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var bandit = await SeedAsync(campaignId, "Bandit", 12, visibility: ActorVisibility.Unknown, publicName: "Bandit");
        var ambusher = await SeedAsync(campaignId, "Ambusher", 12, visibility: ActorVisibility.Hidden);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session, ScriptedDice());
        runtime.Hydrate(new[] { bandit, ambusher });

        await runtime.EnqueueAsync(new StartPreparedEncounterCommand("plan-1", "Cellar ambush", "cellar", "Cellar", new[] { bandit.Id, ambusher.Id }, "gm"));

        var player = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        var seen = SessionEventRedactor.ForViewer(session.Events, player, new[] { bandit });
        Assert.DoesNotContain(seen, e => e.Type == "EncounterStarted");
        Assert.All(seen, e => Assert.NotEmpty(e.Parts));
        var initiative = Assert.Single(seen, e => e.Type == "Initiative");
        Assert.Contains("Bandit", initiative.Description);
        Assert.DoesNotContain("Ambusher", string.Join(" ", seen.Select(e => e.Description)));

        var gm = SessionEventRedactor.ForViewer(session.Events, new CampaignAccess(campaignId, "gm", CampaignRole.DM), new[] { bandit, ambusher });
        var start = Assert.Single(gm, e => e.Type == "EncounterStarted");
        Assert.Contains("Cellar ambush", start.Description);
        Assert.Contains("Cellar", start.Description);
        Assert.Equal(2, gm.Count(e => e.Type == "Initiative"));
    }

    [Fact]
    public async Task ThePlayersLog_HasNoStartLine_EvenForAPlanWithNoPlace_OrARefusedStart_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var bandit = await SeedAsync(campaignId, "Bandit", 12, visibility: ActorVisibility.Unknown, publicName: "Bandit");
        var other = await SeedAsync(campaignId, "Imp", 12, visibility: ActorVisibility.Unknown, publicName: "Imp");
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session, ScriptedDice());
        runtime.Hydrate(new[] { bandit, other });

        // The plan's name is the only thing the line says, so it alone must keep the line from a player.
        await runtime.EnqueueAsync(new StartPreparedEncounterCommand("plan-1", "Secret plan name", null, string.Empty, new[] { bandit.Id }, "gm"));
        await runtime.EnqueueAsync(new StartPreparedEncounterCommand("plan-2", "Another secret plan", null, string.Empty, new[] { other.Id }, "gm"));

        Assert.Equal(2, session.Events.Count(e => e.Type == "EncounterStarted"));
        var seen = SessionEventRedactor.ForViewer(session.Events, new CampaignAccess(campaignId, "player-1", CampaignRole.Player), new[] { bandit, other });
        Assert.DoesNotContain(seen, e => e.Type == "EncounterStarted");
        Assert.DoesNotContain("secret", string.Join(" ", seen.Select(e => e.Description)), StringComparison.OrdinalIgnoreCase);
        Assert.Single(seen, e => e.Type == "Initiative");
    }

    [Fact]
    public async Task AFollowUpEvent_IsWrittenInTheSameSessionWrite_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var first = await SeedAsync(campaignId, "Ogre", 14);
        var second = await SeedAsync(campaignId, "Imp", 8);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session, ScriptedDice());
        runtime.Hydrate(new[] { first, second });

        await runtime.EnqueueAsync(Start("plan-1", "cellar", first, second));

        var stored = (await _sessions.GetAsync(session.Id))!;
        var types = stored.Events.Select(e => e.Type).ToList();
        var at = types.IndexOf("EncounterStarted");
        Assert.True(at >= 0);
        Assert.Equal(new[] { "EncounterStarted", "Initiative", "Initiative" }, types.Skip(at).Take(3));
        Assert.Equal(2, stored.RollLog.Count);
    }

    // ---- The service, with a real table ----

    private sealed class Hold(SemaphoreSlim gate) : IRuntimeCommand
    {
        public IReadOnlyCollection<string> AffectedActorIds => Array.Empty<string>();
        public void Apply(CampaignRuntime runtime) => gate.Wait(TimeSpan.FromSeconds(10));
        public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("Test", Array.Empty<EventMessage>());
    }

    private sealed record Table(PreparedEncounterService Service, CastService Cast, CampaignLibrary Library, CampaignRuntimeManager Manager, MemoryPlans Plans, ActorRepository Actors,
        CampaignDocument Campaign, CampaignAccess Dm, ActorDocument Npc, ActorDocument Pc);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>The encounter store, until a test makes it fail: the loop reports a failed write after a command as a failed command.</summary>
    private sealed class FlakyEncounters(IEncounterRepository inner) : IEncounterRepository
    {
        public bool Fail { get; set; }

        public Task<EncounterDocument?> GetAsync(string encounterId) => inner.GetAsync(encounterId);

        public Task SaveAsync(EncounterDocument encounter) => Fail ? throw new InvalidOperationException("The database is down.") : inner.SaveAsync(encounter);

        public Task<IEnumerable<EncounterDocument>> GetAllForCampaignAsync(string campaignId) => inner.GetAllForCampaignAsync(campaignId);

        public Task DeleteAllForCampaignAsync(string campaignId) => inner.DeleteAllForCampaignAsync(campaignId);
    }

    private async Task<Table> OpenAsync(bool live, IEncounterRepository? encounters = null)
    {
        var components = new ComponentRegistry();
        components.Register<ResourcePoolComponent>();
        components.Register<AttributesComponent>();
        components.Register<ActiveEffectsComponent>();
        var hooks = new CampaignLifecycleHooks();
        var campaigns = new CampaignRepository(_db, new NoOpSystemCatalog(), _actors, _sessions, new RavenNoteRepository(_db), new RegionRepository(_db),
            new FactRepository(_db), new RavenHomebrewRepository(_db), _encounters, lifecycle: hooks);
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, components, Substitute.For<IContentPackLoader>());
        await loader.LoadAndInitializeAsync(Path.Combine(TestPaths.ContentRoot, "systems"));
        var manager = new CampaignRuntimeManager(_actors, _sessions, campaigns, encounters ?? _encounters, components, loader, NullLoggerFactory.Instance, hooks);

        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Salt", SystemId = "srd5e" };
        await campaigns.SaveAsync(campaign);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var npc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Wendel Tarr", Kind = ActorKind.NonPlayerCharacter, Visibility = ActorVisibility.Known,
            Sheet = new Dictionary<string, object> { ["HP"] = 9L, ["HP_Max"] = 9L, ["dex"] = 12L } };
        var pc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Mira", Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-1", Visibility = ActorVisibility.Known,
            Sheet = new Dictionary<string, object> { ["HP"] = 12L, ["HP_Max"] = 12L, ["dex"] = 14L } };
        foreach (var actor in new[] { npc, pc })
        {
            ActorSheetSync.ApplySheetToState(actor, loader.GetDefinition("srd5e"), components);
            await _actors.SaveAsync(actor);
        }

        if (live) await manager.GetOrStartAsync(campaign, dm);

        var homebrew = Substitute.For<IHomebrewRepository>();
        var brews = new List<HomebrewEntryDocument>
        {
            new() { CampaignId = campaignId, SystemId = "srd5e", Type = "monster", EntryId = "bandit", Name = "Bandit", Properties = { ["hitPoints"] = 11L, ["dexterity"] = 14L } },
            new() { CampaignId = campaignId, SystemId = "srd5e", Type = "monster", EntryId = "wolf", Name = "Wolf", Properties = { ["hitPoints"] = 11L, ["dexterity"] = 15L } }
        };
        homebrew.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => brews.AsEnumerable());
        var wiki = Substitute.For<IWikiRepository>();
        wiki.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Array.Empty<WikiPageDocument>().AsEnumerable());
        var regions = Substitute.For<IRegionRepository>();
        var region = new RegionDocument
        {
            Id = "r1", CampaignId = campaignId, Name = "Greywater",
            Locations = { new LocationDocument { Id = "cellar", Name = "Cellar" }, new LocationDocument { Id = "hall", Name = "Hall" } }
        };
        regions.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(ci => ci.Arg<CampaignAccess>().IsDm ? new[] { region } : Array.Empty<RegionDocument>());
        var registry = new ContentRegistry();
        var library = new CampaignLibrary(registry, homebrew, loader);
        var cast = new CastService(manager, registry, components, loader, _actors, wiki);
        var plans = new MemoryPlans();
        var service = new PreparedEncounterService(plans, regions, _actors, library, registry, cast, manager);
        return new Table(service, cast, library, manager, plans, _actors, campaign, dm, npc, pc);
    }

    private static PreparedLine Creature(string id, string name, int count, bool hidden = false) => new()
    {
        Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = id, IsHomebrew = true, Name = name, Count = count, StartsHidden = hidden
    };

    private static async Task<PreparedEncounterDocument> PlanAsync(Table t, string name, PreparedStatus status = PreparedStatus.Ready, string? place = "cellar", params PreparedLine[] lines)
    {
        var plan = new PreparedEncounterDocument { CampaignId = t.Campaign.Id, Name = name, RegionId = place == null ? null : "r1", PlaceId = place, Status = status, Lines = lines.ToList() };
        Assert.True(await t.Plans.SaveAsync(plan, t.Dm));
        return plan;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(20);
        }

        Assert.Fail("The condition did not hold in time.");
    }

    private static async Task<List<ActorDocument>> CastOf(Table t) => (await t.Actors.GetVisibleForCampaignAsync(t.Dm)).ToList();

    [Fact]
    public async Task StartAsync_MakesEachCreatureLine_NumberedWithItsVisibility_AndReusesACastMember_Async()
    {
        var t = await OpenAsync(live: true);
        var plan = await PlanAsync(t, "Cellar ambush", lines: new[] { Creature("bandit", "Bandit", 2), Creature("wolf", "Wolf", 1, hidden: true), EncounterWorld.Cast(t.Npc.Id, t.Npc.Name) });

        var result = await t.Service.StartAsync(t.Campaign, t.Dm, plan.Id);

        Assert.True(result.Started, result.ProblemKey);
        Assert.Equal(4, result.Joined);
        Assert.Empty(result.NotJoined);
        var cast = await CastOf(t);
        var bandits = cast.Where(a => a.Name.StartsWith("Bandit")).OrderBy(a => a.Name).ToList();
        Assert.Equal(new[] { "Bandit 1", "Bandit 2" }, bandits.Select(a => a.Name));
        Assert.All(bandits, a => { Assert.Equal(ActorVisibility.Unknown, a.Visibility); Assert.Equal(a.Name, a.PublicName); });
        var wolf = Assert.Single(cast, a => a.Name == "Wolf");
        Assert.Equal(ActorVisibility.Hidden, wolf.Visibility);
        Assert.Null(wolf.PublicName);
        var npc = Assert.Single(cast, a => a.Name == "Wendel Tarr");
        Assert.Equal(ActorVisibility.Known, npc.Visibility);

        var runtime = t.Manager.TryGet(t.Campaign.Id)!;
        Assert.Equal(4, runtime.ActiveEncounter!.Participants.Count);
        Assert.Equal("cellar", runtime.ActiveEncounter.LocationId);
        Assert.Equal(plan.Id, runtime.ActiveEncounter.PreparedEncounterId);
        var stored = t.Plans.Held[plan.Id];
        Assert.Equal(PreparedStatus.Ran, stored.Status);
        Assert.Equal(new[] { result.EncounterId }, stored.RunEncounterIds);
        Assert.NotNull(stored.LastRunAt);
        // Each roll was written with the expression the system's bonus gives: the bandits' and the wolf's differ.
        Assert.Equal(4, runtime.ActiveSession!.RollLog.Count);
        Assert.Contains(runtime.ActiveSession.RollLog, r => r.Expression == "1d20+2");
        Assert.Contains(runtime.ActiveSession.RollLog, r => r.Expression == "1d20+2" && r.ActorId == bandits[0].Id);
    }

    [Fact]
    public async Task StartAsync_NumbersAcrossTwoLinesOfOneCreature_Async()
    {
        var t = await OpenAsync(live: true);
        var plan = await PlanAsync(t, "Bandits", lines: new[] { Creature("bandit", "Bandit", 2), Creature("bandit", "Bandit", 1, hidden: true) });

        var result = await t.Service.StartAsync(t.Campaign, t.Dm, plan.Id);

        Assert.True(result.Started, result.ProblemKey);
        var names = (await CastOf(t)).Where(a => a.Name.StartsWith("Bandit")).Select(a => a.Name).Order().ToList();
        Assert.Equal(new[] { "Bandit 1", "Bandit 2", "Bandit 3" }, names);
    }

    [Fact]
    public async Task StartAsync_IsRefused_WithNoTable_ForAPlayer_AndForAMissingPlan_Async()
    {
        var dark = await OpenAsync(live: false);
        var darkPlan = await PlanAsync(dark, "Bandits", lines: new[] { Creature("bandit", "Bandit", 2) });
        var before = (await CastOf(dark)).Count;

        Assert.Equal(EncounterStartProblem.NoTable, (await dark.Service.StartAsync(dark.Campaign, dark.Dm, darkPlan.Id)).Problem);
        Assert.Equal(before, (await CastOf(dark)).Count);

        var t = await OpenAsync(live: true);
        var plan = await PlanAsync(t, "Bandits", lines: new[] { Creature("bandit", "Bandit", 2) });
        var player = new CampaignAccess(t.Campaign.Id, "player-1", CampaignRole.Player);
        var count = (await CastOf(t)).Count;

        Assert.Equal(EncounterStartProblem.NotAllowed, (await t.Service.StartAsync(t.Campaign, player, plan.Id)).Problem);
        Assert.Equal(EncounterStartProblem.NotFound, (await t.Service.StartAsync(t.Campaign, t.Dm, "no-such-plan")).Problem);
        Assert.Equal(count, (await CastOf(t)).Count);
    }

    [Fact]
    public async Task StartAsync_IsRefused_WhileAnEncounterRuns_AndMakesNoCreatures_Async()
    {
        var t = await OpenAsync(live: true);
        var plan = await PlanAsync(t, "Bandits", lines: new[] { Creature("bandit", "Bandit", 2) });
        await t.Manager.TryGet(t.Campaign.Id)!.EnqueueAsync(new RollInitiativeCommand(t.Pc.Id, "1d20", "owner"));
        var count = (await CastOf(t)).Count;

        var result = await t.Service.StartAsync(t.Campaign, t.Dm, plan.Id);

        Assert.False(result.Started);
        Assert.Equal(EncounterStartProblem.EncounterRunning, result.Problem);
        Assert.Equal("Combat.Prepared.Refused.EncounterRunning", result.ProblemKey);
        Assert.Equal(count, (await CastOf(t)).Count);
        Assert.Equal(PreparedStatus.Ready, t.Plans.Held[plan.Id].Status);
    }

    [Fact]
    public async Task StartAsync_RefusedOnTheLoop_DeletesOnlyTheCreaturesItMade_Async()
    {
        var t = await OpenAsync(live: true);
        var plan = await PlanAsync(t, "Bandit and Wendel", lines: new[] { Creature("bandit", "Bandit", 1), EncounterWorld.Cast(t.Npc.Id, t.Npc.Name) });
        var runtime = t.Manager.TryGet(t.Campaign.Id)!;
        using var gate = new SemaphoreSlim(0);
        Task<EncounterStart>? start = null;
        try
        {
            _ = runtime.EnqueueAsync(new Hold(gate));
            _ = runtime.EnqueueAsync(new RollInitiativeCommand(t.Pc.Id, "1d20", "owner"));
            start = t.Service.StartAsync(t.Campaign, t.Dm, plan.Id);
            await WaitUntilAsync(async () => (await CastOf(t)).Any(a => a.Name == "Bandit"));
        }
        finally
        {
            gate.Release();
        }

        var result = await start!.WaitAsync(Patience);

        Assert.Equal(EncounterStartProblem.EncounterRunning, result.Problem);
        var cast = await CastOf(t);
        Assert.DoesNotContain(cast, a => a.Name == "Bandit");
        Assert.Contains(cast, a => a.Id == t.Npc.Id);
        Assert.Equal(t.Pc.Id, Assert.Single(runtime.ActiveEncounter!.Participants).ActorId);
        Assert.DoesNotContain(runtime.Snapshot.Actors.Keys, id => cast.All(a => a.Id != id));
    }

    [Fact]
    public async Task StartAsync_WithAnInvalidPlan_ReturnsTheCheck_Async()
    {
        var t = await OpenAsync(live: true);
        var plan = await PlanAsync(t, "Bad plan", lines: new[] { EncounterWorld.Cast(t.Pc.Id, t.Pc.Name) });

        var result = await t.Service.StartAsync(t.Campaign, t.Dm, plan.Id);

        Assert.Equal(EncounterStartProblem.Invalid, result.Problem);
        Assert.Contains(result.Check!.Errors, e => e.Key == "Encounters.Check.CastIsPlayer");
    }

    [Fact]
    public async Task StartAsync_ASecondStartWhileOneRuns_IsBusy_Async()
    {
        var t = await OpenAsync(live: true);
        var a = await PlanAsync(t, "A", lines: new[] { Creature("bandit", "Bandit", 1) });
        var b = await PlanAsync(t, "B", lines: new[] { Creature("wolf", "Wolf", 1) });
        var runtime = t.Manager.TryGet(t.Campaign.Id)!;
        using var gate = new SemaphoreSlim(0);
        Task<EncounterStart>? first = null;
        EncounterStart second;
        try
        {
            _ = runtime.EnqueueAsync(new Hold(gate));
            first = t.Service.StartAsync(t.Campaign, t.Dm, a.Id);
            await WaitUntilAsync(async () => (await CastOf(t)).Any(x => x.Name == "Bandit"));

            second = await t.Service.StartAsync(t.Campaign, t.Dm, b.Id);
        }
        finally
        {
            gate.Release();
        }

        Assert.Equal(EncounterStartProblem.Busy, second.Problem);
        Assert.DoesNotContain(await CastOf(t), x => x.Name == "Wolf");
        var done = await first!.WaitAsync(Patience);
        Assert.True(done.Started, done.ProblemKey);
    }

    [Fact]
    public async Task StartChoicesAsync_ListsReadyAndRan_NotDraft_ThePlaceLastUsedFirst_Async()
    {
        var t = await OpenAsync(live: false);
        await PlanAsync(t, "A", PreparedStatus.Ready, "cellar");
        var b = await PlanAsync(t, "B", PreparedStatus.Ready, "hall");
        await PlanAsync(t, "C", PreparedStatus.Draft, "cellar");
        await PlanAsync(t, "D", PreparedStatus.Ready, "hall");
        t.Plans.Held[b.Id].Status = PreparedStatus.Ran;
        t.Plans.Held[b.Id].RunEncounterIds.Add("e1");
        t.Plans.Held[b.Id].LastRunAt = DateTime.UtcNow;

        var none = await t.Service.StartChoicesAsync(t.Campaign, t.Dm, null);
        var cellar = await t.Service.StartChoicesAsync(t.Campaign, t.Dm, "cellar");

        Assert.Equal(new[] { "B", "D", "A" }, none.Select(r => r.Plan.Name));
        Assert.Equal(new[] { "A", "B", "D" }, cellar.Select(r => r.Plan.Name));
        Assert.Empty(await t.Service.StartChoicesAsync(t.Campaign, new CampaignAccess(t.Campaign.Id, "player-1", CampaignRole.Player), null));
    }

    [Fact]
    public async Task StartAsync_StartsADraft_FromTheEditorsPath_Async()
    {
        var t = await OpenAsync(live: true);
        var plan = await PlanAsync(t, "Draft fight", PreparedStatus.Draft, lines: new[] { Creature("bandit", "Bandit", 1) });

        var result = await t.Service.StartAsync(t.Campaign, t.Dm, PreparedEncounterService.RouteOf(plan.Id));

        Assert.True(result.Started, result.ProblemKey);
        Assert.Equal(PreparedStatus.Ran, t.Plans.Held[plan.Id].Status);
    }

    [Fact]
    public async Task AddToCastAsync_WithNoNewArguments_StillMakesHiddenCreatures_NamedAsBefore_Async()
    {
        var t = await OpenAsync(live: false);
        var item = (await t.Library.LookupAsync(t.Campaign, t.Dm))("monster", "bandit", true)!;
        var system = t.Library.Definition(t.Campaign);

        var three = await t.Cast.AddToCastAsync(item, t.Campaign, system, t.Dm, 3);
        var one = await t.Cast.AddToCastAsync(item, t.Campaign, system, t.Dm);

        Assert.Equal(new[] { "Bandit 1", "Bandit 2", "Bandit 3" }, three.Select(a => a.Name));
        Assert.Equal("Bandit", Assert.Single(one).Name);
        Assert.All(three.Concat(one), a => { Assert.Equal(ActorVisibility.Hidden, a.Visibility); Assert.Null(a.PublicName); });
    }

    // ---- Numbers must not give a hidden creature away ----

    [Fact]
    public async Task StartAsync_NumbersTheVisibleCopiesFirst_WhateverTheOrderOfTheLines_Async()
    {
        var t = await OpenAsync(live: true);
        // The hidden line is first: the visible bandits must still be "Bandit 1" and "Bandit 2", not 2 and 3.
        var plan = await PlanAsync(t, "Ambush", lines: new[] { Creature("bandit", "Bandit", 1, hidden: true), Creature("bandit", "Bandit", 2) });

        var result = await t.Service.StartAsync(t.Campaign, t.Dm, plan.Id);

        Assert.True(result.Started, result.ProblemKey);
        var bandits = (await CastOf(t)).Where(a => a.Name.StartsWith("Bandit")).ToList();
        var seen = bandits.Where(a => a.Visibility == ActorVisibility.Unknown).Select(a => a.Name).Order().ToList();
        Assert.Equal(new[] { "Bandit 1", "Bandit 2" }, seen);
        Assert.All(bandits.Where(a => a.Visibility == ActorVisibility.Unknown), a => Assert.Equal(a.Name, a.PublicName));
        var hidden = Assert.Single(bandits, a => a.Visibility == ActorVisibility.Hidden);
        Assert.Equal("Bandit 3", hidden.Name);
        Assert.Null(hidden.PublicName);
    }

    [Fact]
    public async Task StartAsync_ALoneVisibleCopy_NextToAHiddenLine_IsNotNumbered_Async()
    {
        var t = await OpenAsync(live: true);
        var plan = await PlanAsync(t, "Ambush", lines: new[] { Creature("bandit", "Bandit", 1), Creature("bandit", "Bandit", 1, hidden: true) });

        var result = await t.Service.StartAsync(t.Campaign, t.Dm, plan.Id);

        Assert.True(result.Started, result.ProblemKey);
        var bandits = (await CastOf(t)).Where(a => a.Name.StartsWith("Bandit")).ToList();
        var visible = Assert.Single(bandits, a => a.Visibility == ActorVisibility.Unknown);
        Assert.Equal("Bandit", visible.Name);
        Assert.Equal("Bandit", visible.PublicName);
        Assert.Equal("Bandit 2", Assert.Single(bandits, a => a.Visibility == ActorVisibility.Hidden).Name);
    }

    // ---- When something fails around the start ----

    [Fact]
    public async Task StartAsync_AFailureAfterTheEncounterStarted_KeepsTheCreatures_AndMarksThePlan_Async()
    {
        var flaky = new FlakyEncounters(_encounters);
        var t = await OpenAsync(live: true, flaky);
        var plan = await PlanAsync(t, "Ambush", lines: new[] { Creature("bandit", "Bandit", 1) });
        flaky.Fail = true;

        // The loop answers a failed write after the command as a failed command (here an InvalidOperationException, which once read as "the table closed").
        var result = await t.Service.StartAsync(t.Campaign, t.Dm, plan.Id);
        flaky.Fail = false;

        Assert.True(result.Started, result.ProblemKey);
        Assert.Equal(1, result.Joined);
        Assert.Contains(await CastOf(t), a => a.Name == "Bandit");
        Assert.Single(t.Manager.TryGet(t.Campaign.Id)!.ActiveEncounter!.Participants);
        Assert.Equal(PreparedStatus.Ran, t.Plans.Held[plan.Id].Status);
    }

    [Fact]
    public async Task StartAsync_APlanThatCannotBeMarkedAsRun_IsStillStarted_AndSaysSo_Async()
    {
        var t = await OpenAsync(live: true);
        var plan = await PlanAsync(t, "Ambush", lines: new[] { Creature("bandit", "Bandit", 1) });
        t.Plans.ThrowOnMarkRun = true;

        var result = await t.Service.StartAsync(t.Campaign, t.Dm, plan.Id);

        Assert.True(result.Started, result.ProblemKey);
        Assert.True(result.RunNotRecorded);
        Assert.Contains(await CastOf(t), a => a.Name == "Bandit");
        Assert.Equal(PreparedStatus.Ready, t.Plans.Held[plan.Id].Status);
    }

    [Fact]
    public async Task Start_WhenNoRollCanBeMade_IsRefused_AndLeavesNoEncounterBehind_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedAsync(campaignId, "Ogre", 14);
        var session = await SeedSessionAsync(campaignId);
        var badDie = new SystemDefinition { Id = "bad", Name = "Bad", Dice = new DiceConventions { Initiative = "not-a-die" } };
        await using var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, null, badDie);
        runtime.Hydrate(new[] { actor });

        var command = Start("plan-1", "cellar", actor);
        await runtime.EnqueueAsync(command);

        Assert.Equal(PreparedStartRefusal.NobodyAtTable, command.Refusal);
        Assert.Equal(new[] { actor.Id }, command.Skipped);
        Assert.Null(command.EncounterId);
        Assert.Null(runtime.ActiveEncounter);
        Assert.Empty(session.RollLog);
    }

    private sealed class Noisy : IRuntimeCommand, IFollowedByEvents
    {
        public IReadOnlyCollection<string> AffectedActorIds => Array.Empty<string>();
        public void Apply(CampaignRuntime runtime) { }
        public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("Noisy", Array.Empty<EventMessage>());
        public IReadOnlyList<SessionEvent> EventsAfter(CampaignRuntime runtime) => throw new InvalidOperationException("A follow-up line could not be worked out.");
    }

    [Fact]
    public async Task AFollowUpThatThrows_LosesOnlyItsOwnLines_NothingIsWrittenTwice_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedAsync(campaignId, "Ogre", 14);
        var session = await SeedSessionAsync(campaignId);
        await using var runtime = Runtime(campaignId, session, ScriptedDice());
        runtime.Hydrate(new[] { actor });

        await runtime.EnqueueAsync(new Noisy());
        await runtime.EnqueueAsync(new RollInitiativeCommand(actor.Id, "1d20", "gm"));

        var stored = (await _sessions.GetAsync(session.Id))!;
        Assert.Single(stored.Events, e => e.Type == "Noisy");
        Assert.Single(stored.Events, e => e.Type == "Initiative");
        Assert.Single(session.Events, e => e.Type == "Noisy");
    }

    // ---- A copy that fails midway is still counted ----

    private static (CastService Cast, LibraryItem Item, CampaignDocument Campaign, CampaignAccess Dm, Func<int> Saved) Flaky(bool savedBeforeItFails)
    {
        var components = new ComponentRegistry();
        var saved = new Dictionary<string, ActorDocument>();
        var calls = 0;
        var actors = Substitute.For<IActorRepository>();
        actors.TrySaveAsync(Arg.Any<ActorDocument>(), Arg.Any<CampaignAccess>()).Returns(ci =>
        {
            var actor = ci.Arg<ActorDocument>();
            if (++calls == 2)
            {
                if (savedBeforeItFails) saved[actor.Id] = actor;
                throw new IOException("The store failed on the second copy.");
            }

            saved[actor.Id] = actor;
            return true;
        });
        actors.GetVisibleAsync(Arg.Any<string>(), Arg.Any<CampaignAccess>()).Returns(ci => saved.GetValueOrDefault(ci.ArgAt<string>(0)));
        var manager = new CampaignRuntimeManager(actors, Substitute.For<ISessionRepository>(), Substitute.For<ICampaignRepository>(), Substitute.For<IEncounterRepository>(), components,
            new PluginLoader(NullLogger<PluginLoader>.Instance, components, Substitute.For<IContentPackLoader>()), NullLoggerFactory.Instance);
        var cast = new CastService(manager, new ContentRegistry(), components, Substitute.For<ISystemCatalog>(), actors, Substitute.For<IWikiRepository>());
        var brew = new HomebrewEntryDocument { CampaignId = "c", SystemId = "srd5e", Type = "monster", EntryId = "bandit", Name = "Bandit", Properties = { ["hitPoints"] = 11L } };
        return (cast, new LibraryItem(brew.ToEntry(), null, brew), new CampaignDocument { Id = "c", SystemId = "srd5e" }, new CampaignAccess("c", "gm", CampaignRole.DM), () => saved.Count);
    }

    [Fact]
    public async Task AddToCastAsync_ACopyThatFailsAfterItWasSaved_IsReturnedWithTheOnesBeforeIt_Async()
    {
        var (cast, item, campaign, dm, saved) = Flaky(savedBeforeItFails: true);

        var made = await cast.AddToCastAsync(item, campaign, Srd, dm, 3);

        Assert.Equal(new[] { "Bandit 1", "Bandit 2" }, made.Select(a => a.Name));
        Assert.Equal(2, saved());
    }

    [Fact]
    public async Task AddToCastAsync_ACopyThatFailsBeforeItWasSaved_IsLeftOut_AndNoMoreAreMade_Async()
    {
        var (cast, item, campaign, dm, saved) = Flaky(savedBeforeItFails: false);

        var made = await cast.AddToCastAsync(item, campaign, Srd, dm, 3);

        Assert.Equal(new[] { "Bandit 1" }, made.Select(a => a.Name));
        Assert.Equal(1, saved());
    }
}
