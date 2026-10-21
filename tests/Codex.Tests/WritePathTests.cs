using Codex.Core;
using Codex.Core.Components;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// One write path per document (audit F2/F3/F8): the live runtime, the character sheet, the
/// session page and the campaign settings page all used to store their own cached copy of a
/// document whole, so whichever saved last silently reverted the others.
/// </summary>
[Collection("RavenDb")]
public class WritePathTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private static readonly CampaignAccess Dm = new("camp", "dm", CampaignRole.DM);

    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenEncounterRepository _encounters;
    private readonly ComponentRegistry _registry;

    public WritePathTests(RavenDbFixture fixture)
    {
        _dbService = new RavenDbService(fixture.DbPath, "WritePath_" + Guid.NewGuid(), runInMemory: true);
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _encounters = new RavenEncounterRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    private async Task<ActorDocument> SeedActorAsync(string campaignId, int hp, string? ownerId = null)
    {
        var pool = new ResourcePoolComponent();
        pool.Set("HP", hp);
        pool.Set("HP_Max", hp);
        var doc = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = campaignId,
            Name = "Aria",
            Kind = ActorKind.PlayerCharacter,
            OwnerUserId = ownerId,
            Sheet = new Dictionary<string, object> { ["HP"] = (long)hp, ["HP_Max"] = (long)hp, ["Level"] = 3L },
            State = _registry.Snapshot(new object[] { pool })
        };
        await _actors.SaveAsync(doc);
        return doc;
    }

    private async Task<SessionDocument> SeedSessionAsync(string campaignId)
    {
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        return session;
    }

    private CampaignRuntime StartRuntime(string campaignId, SessionDocument session, params ActorDocument[] actors)
    {
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters);
        runtime.Hydrate(actors);
        return runtime;
    }

    private Task<ActorDocument?> LoadAsync(ActorDocument actor) =>
        _actors.GetVisibleAsync(actor.Id, new CampaignAccess(actor.CampaignId, "dm", CampaignRole.DM));

    [Fact]
    public async Task NextTurn_DoesNotRevertAnEditSavedToTheActorMeanwhile_Async()
    {
        var actor = await SeedActorAsync("camp", 10);
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);

        // The DM writes notes on the sheet while the table is live.
        var edited = (await LoadAsync(actor))!;
        edited.DmNotes = "Secretly the duke's heir.";
        edited.Sheet["Level"] = 4L;
        Assert.True(await _actors.TrySaveAsync(edited, Dm));

        await runtime.EnqueueAsync(new SetInitiativeCommand(actor.Id, 12));
        await runtime.EnqueueAsync(new NextTurnCommand());

        var stored = (await LoadAsync(actor))!;
        Assert.Equal("Secretly the duke's heir.", stored.DmNotes);
        Assert.Equal(4L, Convert.ToInt64(stored.Sheet["Level"]));
    }

    [Fact]
    public async Task TableDamage_IsMirroredOntoTheSheetTheListsRead_Async()
    {
        var actor = await SeedActorAsync("camp", 34);
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);

        await runtime.EnqueueAsync(new ApplyDamageCommand(actor.Id, "HP", 27, "dragon"));

        var stored = (await LoadAsync(actor))!;
        var pool = _registry.Hydrate(stored.State).OfType<ResourcePoolComponent>().Single();
        Assert.Equal(7, pool.Get("HP"));
        Assert.Equal(7, YamlValues.GetInt(stored.Sheet, "HP"));
    }

    private static ReplaceActorCommand SheetSave(ActorDocument edited, ActorDocument loaded, CampaignAccess? access = null) =>
        new(edited, new Dictionary<string, object>(loaded.Sheet), access ?? Dm, null);

    [Fact]
    public async Task SheetSaveDuringAFight_KeepsTableDamage_AndTheEditSurvivesTheNextTurn_Async()
    {
        var actor = await SeedActorAsync("camp", 10, ownerId: "player");
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);

        // The player opens their sheet at full health...
        var loaded = (await LoadAsync(actor))!;
        var edited = (await LoadAsync(actor))!;
        edited.Sheet["Level"] = 4L;
        edited.Biography = "Raised by wolves.";

        // ...the table hits them while the page is open...
        await runtime.EnqueueAsync(new ApplyDamageCommand(actor.Id, "HP", 3, "wolf"));

        // ...and they save the stale page.
        await runtime.EnqueueAsync(SheetSave(edited, loaded, new CampaignAccess("camp", "player", CampaignRole.Player)));
        await runtime.EnqueueAsync(new SetInitiativeCommand(actor.Id, 12));
        await runtime.EnqueueAsync(new NextTurnCommand());

        var stored = (await LoadAsync(actor))!;
        Assert.Equal(7, YamlValues.GetInt(stored.Sheet, "HP"));
        Assert.Equal(7, _registry.Hydrate(stored.State).OfType<ResourcePoolComponent>().Single().Get("HP"));
        Assert.Equal(4, YamlValues.GetInt(stored.Sheet, "Level"));
        Assert.Equal("Raised by wolves.", stored.Biography);
        Assert.Equal(7, runtime.Snapshot.Actor(actor.Id)!.Pool("HP"));
    }

    [Fact]
    public async Task SheetSave_CanStillChangeHitPoints_WhenTheEditorChangedThem_Async()
    {
        var actor = await SeedActorAsync("camp", 10);
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);

        var loaded = (await LoadAsync(actor))!;
        var edited = (await LoadAsync(actor))!;
        edited.Sheet["HP_Max"] = 14L;
        edited.Sheet["HP"] = 14L;
        ActorSheetSync.ApplySheetToState(edited, null, _registry);
        await runtime.EnqueueAsync(SheetSave(edited, loaded));

        var pool = runtime.GetEntity(actor.Id)!.Value.Get<ResourcePoolComponent>();
        Assert.Equal(14, pool.Get("HP"));
        Assert.Equal(14, pool.Get("HP_Max"));
        Assert.Equal(14, YamlValues.GetInt((await LoadAsync(actor))!.Sheet, "HP_Max"));
    }

    [Fact]
    public async Task SheetSave_FromSomeoneWhoDoesNotOwnTheActor_IsRefused_Async()
    {
        var actor = await SeedActorAsync("camp", 10, ownerId: "player");
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);

        var loaded = (await LoadAsync(actor))!;
        var edited = (await LoadAsync(actor))!;
        edited.Name = "Hijacked";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            runtime.EnqueueAsync(SheetSave(edited, loaded, new CampaignAccess("camp", "stranger", CampaignRole.Player))));
        Assert.Equal("Aria", (await LoadAsync(actor))!.Name);
    }

    [Fact]
    public async Task SessionPage_SavingARecap_KeepsWhatTheTableLoggedMeanwhile_AndTheNextRollKeepsTheRecap_Async()
    {
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session);

        var pageCopy = await _sessions.GetAsync(session.Id);
        await runtime.EnqueueAsync(new RollDiceCommand("1d20", null, "dm", false));
        await runtime.EnqueueAsync(new AddSessionNoteCommand("dm", "The bridge is out.", false));

        // The page opened before the roll and note; saving the recap must not drop them.
        await _sessions.UpdateAsync(pageCopy!.Id, stored => stored.Recap = "They crossed the river.");
        await runtime.EnqueueAsync(new RollDiceCommand("1d6", null, "dm", false));

        var stored = (await _sessions.GetAsync(session.Id))!;
        Assert.Equal("They crossed the river.", stored.Recap);
        Assert.Equal(2, stored.RollLog.Count);
        Assert.Single(stored.Notes);
        Assert.Equal(3, stored.Events.Count);
    }

    [Fact]
    public async Task SessionPage_SavingTitleAndStatus_DoesNotDropEvents_Async()
    {
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session);
        await runtime.EnqueueAsync(new AddSessionNoteCommand("dm", "A note", false));

        await _sessions.UpdateAsync(session.Id, stored => { stored.Title = "Renamed"; stored.DmSummary = "Private."; });

        var reloaded = (await _sessions.GetAsync(session.Id))!;
        Assert.Equal("Renamed", reloaded.Title);
        Assert.Equal("Private.", reloaded.DmSummary);
        Assert.Single(reloaded.Events);
        Assert.Single(reloaded.Notes);
    }

    [Fact]
    public async Task RuntimeSnapshot_IsAnImmutablePictureOfTheTable_Async()
    {
        var actor = await SeedActorAsync("camp", 10);
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);

        await runtime.EnqueueAsync(new SetInitiativeCommand(actor.Id, 15));
        await runtime.EnqueueAsync(new RollDiceCommand("1d20", null, "dm", false));
        var before = runtime.Snapshot;

        await runtime.EnqueueAsync(new RollDiceCommand("1d20", null, "dm", false));
        await runtime.EnqueueAsync(new ApplyDamageCommand(actor.Id, "HP", 4, "trap"));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(actor.Id, "poisoned", "trap", 3, EffectExpiry.EndOfTurn, actor.Id));

        Assert.Single(before.RollLog);
        Assert.Equal(10, before.Actor(actor.Id)!.Pool("HP"));
        Assert.Equal(15, before.Encounter!.Participants.Single().InitiativeRoll);

        var after = runtime.Snapshot;
        Assert.Equal(2, after.RollLog.Count);
        Assert.Equal(6, after.Actor(actor.Id)!.Pool("HP"));
        Assert.Equal("poisoned", after.Actor(actor.Id)!.Effects.Single().EffectId);
    }

    private CampaignRepository NewCampaignRepository() => new(
        _dbService, new NoOpSystemCatalog(), _actors, _sessions, new RavenNoteRepository(_dbService), new RegionRepository(_dbService),
        new FactRepository(_dbService), new RavenHomebrewRepository(_dbService), _encounters);

    [Fact]
    public async Task SettingsSave_FromAStaleCopy_DoesNotDropAMemberWhoJoinedMeanwhile_Async()
    {
        var campaigns = NewCampaignRepository();
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = "dm", Name = "Old name" };
        await campaigns.SaveAsync(campaign);

        var settingsPageCopy = await campaigns.GetAsync(campaign.Id);
        var joined = await campaigns.JoinByInviteCodeAsync(settingsPageCopy!.InviteCode, "player");
        Assert.Equal(CampaignJoinResult.Joined, joined.Result);

        await campaigns.UpdateAsync(settingsPageCopy.Id, stored => stored.Name = "New name");

        var reloaded = (await campaigns.GetAsync(campaign.Id))!;
        Assert.Equal("New name", reloaded.Name);
        Assert.Contains(reloaded.Members, m => m.UserId == "player");
    }

    [Fact]
    public async Task SettingsSave_DoesNotRevertTheLiveSessionPointer_AndTheOtherWayAround_Async()
    {
        var campaigns = NewCampaignRepository();
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = "dm", Name = "Camp" };
        await campaigns.SaveAsync(campaign);
        var bestiaryPageCopy = await campaigns.GetAsync(campaign.Id);

        await campaigns.UpdateAsync(campaign.Id, stored => stored.CurrentSessionId = "session-1");
        await campaigns.UpdateAsync(bestiaryPageCopy!.Id, stored => stored.Bestiary.Add(new BestiaryEntry { EntryId = "goblin", Name = "Goblin" }));

        var reloaded = (await campaigns.GetAsync(campaign.Id))!;
        Assert.Equal("session-1", reloaded.CurrentSessionId);
        Assert.Single(reloaded.Bestiary);
    }

    [Fact]
    public async Task ConcurrentCampaignUpdates_AreAllKept_Async()
    {
        var campaigns = NewCampaignRepository();
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = "dm", Name = "Camp" };
        await campaigns.SaveAsync(campaign);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            campaigns.UpdateAsync(campaign.Id, stored => stored.Members.Add(new CampaignMember { UserId = $"player-{i}", Role = CampaignRole.Player }))));

        var reloaded = (await campaigns.GetAsync(campaign.Id))!;
        Assert.Equal(8, reloaded.Members.Count(m => m.UserId.StartsWith("player-")));
    }

    [Fact]
    public void SameValue_ComparesNumbersByValueAndStructuresElementWise()
    {
        Assert.True(YamlValues.SameValue(4L, 4.0));
        Assert.False(YamlValues.SameValue(4L, 5L));
        Assert.True(YamlValues.SameValue(new List<object> { "a", 1L }, new List<object> { "a", 1L }));
        Assert.False(YamlValues.SameValue(new List<object> { "a" }, new List<object> { "a", "b" }));
        Assert.True(YamlValues.SameValue(new Dictionary<string, object> { ["k"] = 2L }, new Dictionary<string, object> { ["k"] = 2.0 }));
        Assert.True(YamlValues.SameValue(null, null));
        Assert.False(YamlValues.SameValue(null, "x"));
    }
}
