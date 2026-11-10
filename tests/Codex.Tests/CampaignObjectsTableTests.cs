using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The campaign's clocks and tables stored and at the live table (overhaul step 7): one document per campaign, changed by the
/// GM or by events, written back by the table key by key, and logged so that a player never reads of what is kept for the GM.
/// </summary>
[Collection("RavenDb")]
public class CampaignObjectsTableTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _db;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenCampaignObjectsRepository _objects;
    private readonly ComponentRegistry _registry;

    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: clock-test
        name: Clock test
        sheetKinds:
          - kind: hero
            label: Hero
            tracks:
              - { key: stress, max: 6 }
        campaign:
          clocks: { segments: [4, 6], visibility: [gm, players] }
          tables:
            - key: factions
              label: Factions
              itemFields: [{ key: status, type: Number, min: -3, max: 3, default: 0 }]
        events:
          tick:
            - { target: campaign.clocks, intents: [{ adjust: { clock: ev.clock, by: ev.by } }] }
          alarm:
            - { intents: [{ adjust: { track: stress, by: 1 } }] }
            - { target: campaign.clocks, intents: [{ adjust: { clock: Ritual, by: 1 } }] }
        """);

    public CampaignObjectsTableTests(RavenDbFixture fixture)
    {
        _db = new RavenDbService(fixture.DbPath, "CampaignObjects_" + Guid.NewGuid(), runInMemory: true);
        _actors = new ActorRepository(_db);
        _sessions = new RavenSessionRepository(_db);
        _objects = new RavenCampaignObjectsRepository(_db);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _db.Dispose();

    private static CampaignAccess Gm(string campaignId) => new(campaignId, "gm", CampaignRole.DM);
    private static CampaignAccess Player(string campaignId) => new(campaignId, "p1", CampaignRole.Player);

    private async Task<string> CampaignAsync()
    {
        var campaign = new CampaignDocument { Id = "campaigns/" + Guid.NewGuid(), OwnerId = "gm", Name = "Clocks", SystemId = System.Id };
        campaign.Members.Add(new CampaignMember { UserId = "gm", Role = CampaignRole.DM });
        campaign.Members.Add(new CampaignMember { UserId = "p1", Role = CampaignRole.Player });
        using var session = _db.Store.OpenAsyncSession();
        await session.StoreAsync(campaign);
        await session.SaveChangesAsync();
        await _objects.TryChangeAsync(Gm(campaign.Id), state =>
            CampaignObjects.AddClock(System, state, "Alarm", 6, "gm") && CampaignObjects.AddClock(System, state, "Ritual", 4, "players")
            && CampaignObjects.AddRow(System, state, "factions", "The Watch"));
        return campaign.Id;
    }

    private async Task<(CampaignRuntime Runtime, SessionDocument Session)> TableAsync(string campaignId, params ActorDocument[] actors)
    {
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: System)
        {
            CampaignObjectsStore = _objects
        };
        runtime.Hydrate(actors);
        runtime.HydrateCampaignObjects((await _objects.GetDocumentAsync(campaignId))?.State);
        return (runtime, session);
    }

    private async Task<IReadOnlyList<CampaignClock>> StoredClocksAsync(string campaignId) =>
        CampaignObjects.Clocks((await _objects.GetDocumentAsync(campaignId))!.State);

    [Fact]
    public async Task TheStore_KeepsNestedState_AndShowsAPlayerOnlyWhatIsTheirs_AndOnlyTheGmChangesIt()
    {
        var campaignId = await CampaignAsync();

        var gm = await _objects.GetVisibleAsync(Gm(campaignId), System);
        Assert.Equal(new[] { "Alarm", "Ritual" }, CampaignObjects.Clocks(gm).Select(c => c.Name));
        Assert.Equal(0d, Assert.Single(CampaignObjects.Rows(System, gm, "factions")).Fields["status"]);
        var player = await _objects.GetVisibleAsync(Player(campaignId), System);
        Assert.Equal(new[] { "Ritual" }, CampaignObjects.Clocks(player).Select(c => c.Name));

        Assert.Null(await _objects.TryChangeAsync(Player(campaignId), state => CampaignObjects.RemoveClock(state, "Ritual")));
        Assert.Null(await _objects.TryChangeAsync(new CampaignAccess(campaignId, "gm", CampaignRole.DM, systemMissing: true), state => CampaignObjects.RemoveClock(state, "Ritual")));
        Assert.Equal(2, (await StoredClocksAsync(campaignId)).Count);
    }

    [Fact]
    public async Task ATick_AtTheTable_FillsTheClock_WritesItBack_AndAPlayerDoesNotReadOfTheGmsClock()
    {
        var campaignId = await CampaignAsync();
        var (runtime, session) = await TableAsync(campaignId);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new CampaignEventCommand("tick", new Dictionary<string, object?> { ["clock"] = "Alarm", ["by"] = 4 }));
        await runtime.EnqueueAsync(new CampaignEventCommand("tick", new Dictionary<string, object?> { ["clock"] = "Alarm", ["by"] = 3 }));
        await runtime.EnqueueAsync(new CampaignEventCommand("adjust", new Dictionary<string, object?> { ["clock"] = "Ritual", ["by"] = 1 }));

        Assert.Equal(6, CampaignObjects.Clocks(runtime.Snapshot.Campaign)[0].Filled);
        Assert.Equal(new[] { 6, 1 }, (await StoredClocksAsync(campaignId)).Select(c => c.Filled));

        var stored = (await _sessions.GetAsync(session.Id))!;
        var log = stored.Events.Select(e => e.Description).ToList();
        Assert.Contains("Alarm: 0 → 4 of 6.", log);
        Assert.Contains("Alarm: 4 → 6 of 6. Alarm is full.", log);
        Assert.Contains("Ritual: 0 → 1 of 4.", log);
        var seen = SessionEventRedactor.ForViewer(stored.Events, Player(campaignId), Array.Empty<ActorDocument>()).Select(e => e.Description).ToList();
        Assert.Equal(new[] { "Ritual: 0 → 1 of 4." }, seen);
    }

    [Fact]
    public async Task AnActorsEvent_ActsOnTheSheetAndTheCampaign_AndTheWriteBackLeavesOtherKeysAsStored()
    {
        var campaignId = await CampaignAsync();
        var hero = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Vex", Kind = ActorKind.PlayerCharacter };
        hero.Sheet["kind"] = "hero";
        hero.Sheet["stress"] = 0L;
        ActorSheetSync.ApplySheetToState(hero, System, _registry);
        await _actors.SaveAsync(hero);
        var (runtime, _) = await TableAsync(campaignId, hero);
        await using var table = runtime;

        // Changed elsewhere after the table took its copy: the table never touches the factions, so it must not write them back.
        await _objects.WriteKeysAsync(campaignId, new Dictionary<string, object>
        {
            ["factions"] = new List<object> { new Dictionary<string, object> { ["name"] = "The Watch", ["status"] = -2L } }
        }, new[] { "factions" });

        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "alarm", new Dictionary<string, object?>()));

        Assert.Equal(1, runtime.GetEntity(hero.Id)!.Value.Get<ResourcePoolComponent>().Get("stress"));
        var stored = (await _objects.GetDocumentAsync(campaignId))!.State;
        Assert.Equal(1, CampaignObjects.Clocks(stored)[1].Filled);
        Assert.Equal(-2d, Assert.Single(CampaignObjects.Rows(System, stored, "factions")).Fields["status"]);
    }

    [Fact]
    public async Task TheGmsEdits_GoThroughTheTableWhileItIsLive()
    {
        var campaignId = await CampaignAsync();
        var (runtime, session) = await TableAsync(campaignId);
        await using var _ = runtime;

        var command = new EditCampaignObjectsCommand(state => CampaignObjects.AddClock(System, state, "Escape", 4, "players"),
            EventMessage.Of("Campaign.ClockAdded", "Escape", 4));
        await runtime.EnqueueAsync(command);

        Assert.True(command.Changed);
        Assert.Equal(new[] { "Alarm", "Ritual", "Escape" }, (await StoredClocksAsync(campaignId)).Select(c => c.Name));
        Assert.Contains("New clock: Escape, 4 segments.", (await _sessions.GetAsync(session.Id))!.Events.Select(e => e.Description));
    }
}
