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
    private CampaignAccess Dm => Access("camp", "dm", CampaignRole.DM);

    private readonly RavenDbService _dbService;
    private CampaignAccess Access(string campaignId, string userId, CampaignRole role) => TestAccess.Member(_dbService, campaignId, userId, role);
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
        _actors.GetVisibleAsync(actor.Id, Access(actor.CampaignId, "dm", CampaignRole.DM));

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

    private ReplaceActorCommand SheetSave(ActorDocument edited, ActorDocument loaded, CampaignAccess? access = null) =>
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
        await runtime.EnqueueAsync(SheetSave(edited, loaded, Access("camp", "player", CampaignRole.Player)));
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
    public async Task APlayersSheetSaveAtALiveTable_KeepsADmBlockAddedAroundTheTable_Async()
    {
        var actor = await SeedActorAsync("camp", 10, ownerId: "player");
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);
        var player = Access("camp", "player", CampaignRole.Player);

        // The DM adds a secret without going through the table (as the world builder does)...
        var dmCopy = (await LoadAsync(actor))!;
        dmCopy.Biography = "Born at sea.\n:::dm\nHer father leads the cult.\n:::";
        Assert.True(await _actors.TrySaveAsync(dmCopy, Dm));

        // ...and the player saves their sheet, history untouched, while the table is live.
        var mine = (await _actors.GetVisibleAsync(actor.Id, player))!;
        var loaded = (await _actors.GetVisibleAsync(actor.Id, player))!;
        mine.Sheet["Level"] = 4L;
        await runtime.EnqueueAsync(SheetSave(mine, loaded, player));
        await runtime.EnqueueAsync(new SetInitiativeCommand(actor.Id, 12));
        await runtime.EnqueueAsync(new NextTurnCommand());

        var stored = (await LoadAsync(actor))!;
        Assert.Equal(4, YamlValues.GetInt(stored.Sheet, "Level"));
        Assert.Contains("Her father leads the cult.", stored.Biography);
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
    public async Task SheetSaveAtALiveTable_MovesHitPointsAndSlotsByTheEditorsChange_NotOverTheTable_Async()
    {
        var actor = await SeedActorAsync("camp", 30);
        actor.Sheet["slots_1"] = 3L;
        actor.Sheet["slots_1_Max"] = 3L;
        var pool = new ResourcePoolComponent();
        pool.Set("HP", 30);
        pool.Set("HP_Max", 30);
        pool.Set("slots_1", 3);
        pool.Set("slots_1_Max", 3);
        actor.State = _registry.Snapshot(new object[] { pool });
        await _actors.SaveAsync(actor);
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);
        var system = new SystemDefinition
        {
            Resources = { new ResourceDefinition { Key = "HP", MaxKey = "HP_Max" } },
            SheetRules = new SheetRules { SlotPools = { new SlotPoolRule { Key = "slots" } } }
        };

        // Opened at 30/30 with three slots. On the sheet: Con goes up (+5 maximum, and the rules add the same to current)
        // and a slot is used...
        var loaded = (await LoadAsync(actor))!;
        var edited = (await LoadAsync(actor))!;
        edited.Sheet["HP_Max"] = 35L;
        edited.Sheet["HP"] = 35L;
        edited.Sheet["slots_1"] = 2L;

        // ...while the table drops them to 12 and spends another slot.
        await runtime.EnqueueAsync(new ApplyDamageCommand(actor.Id, "HP", 18, "ogre"));
        await runtime.EnqueueAsync(new AdjustResourceCommand(actor.Id, "slots_1", -1, "shield"));

        await runtime.EnqueueAsync(new ReplaceActorCommand(edited, new Dictionary<string, object>(loaded.Sheet), Dm, system));

        var live = runtime.GetEntity(actor.Id)!.Value.Get<ResourcePoolComponent>();
        Assert.Equal(17, live.Get("HP"));
        Assert.Equal(35, live.Get("HP_Max"));
        Assert.Equal(1, live.Get("slots_1"));
    }

    // ---- Feature counters (uses_*) on a sheet saved at a live table ----

    private static readonly SystemDefinition UsesSystem = new() { Resources = { new ResourceDefinition { Key = "HP", MaxKey = "HP_Max" } } };

    /// <summary>A character with a counter of uses (<c>uses_focus</c>, <paramref name="left"/> of 4) on both the sheet and the table.</summary>
    private async Task<ActorDocument> SeedWithCounterAsync(int left, params (string Key, int Left, int Max)[] more)
    {
        var actor = await SeedActorAsync("camp", 10);
        var pool = new ResourcePoolComponent();
        pool.Set("HP", 10);
        pool.Set("HP_Max", 10);
        foreach (var (key, now, max) in new[] { ("uses_focus", left, 4) }.Concat(more))
        {
            actor.Sheet[key] = (long)now;
            actor.Sheet[key + "_Max"] = (long)max;
            pool.Set(key, now);
            pool.Set(key + "_Max", max);
        }

        actor.State = _registry.Snapshot(new object[] { pool });
        await _actors.SaveAsync(actor);
        return actor;
    }

    private async Task<ResourcePoolComponent> SaveSheetAsync(CampaignRuntime runtime, ActorDocument actor, Action<ActorDocument> edit, Func<Task>? atTheTable = null)
    {
        var loaded = (await LoadAsync(actor))!;
        var edited = (await LoadAsync(actor))!;
        edit(edited);
        if (atTheTable != null) await atTheTable();
        await runtime.EnqueueAsync(new ReplaceActorCommand(edited, new Dictionary<string, object>(loaded.Sheet), Dm, UsesSystem));
        return runtime.GetEntity(actor.Id)!.Value.Get<ResourcePoolComponent>();
    }

    [Fact]
    public async Task SheetSaveAtALiveTable_ThatChangesNotesOnly_KeepsWhatTheTableSpentOfACounter_Async()
    {
        var actor = await SeedWithCounterAsync(4, ("uses_rage", 3, 3));
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);

        var live = await SaveSheetAsync(runtime, actor, edited => edited.Sheet["Level"] = 4L, async () =>
        {
            await runtime.EnqueueAsync(new AdjustResourceCommand(actor.Id, "uses_focus", -2, "Focus"));
            await runtime.EnqueueAsync(new AdjustResourceCommand(actor.Id, "uses_rage", -1, "Rage"));
        });

        Assert.Equal(2, live.Get("uses_focus"));
        Assert.Equal(2, live.Get("uses_rage"));
        var stored = (await LoadAsync(actor))!;
        Assert.Equal(2, YamlValues.GetInt(stored.Sheet, "uses_focus"));
        Assert.Equal(4, YamlValues.GetInt(stored.Sheet, "uses_focus_Max"));
    }

    [Fact]
    public async Task SheetSaveAtALiveTable_AddsTheEditorsSpendOfACounterToTheTables_Async()
    {
        var actor = await SeedWithCounterAsync(4);
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);

        // One use spent on the sheet, two at the table: one is left.
        var live = await SaveSheetAsync(runtime, actor, edited => edited.Sheet["uses_focus"] = 3L,
            () => runtime.EnqueueAsync(new AdjustResourceCommand(actor.Id, "uses_focus", -2, "Focus")));

        Assert.Equal(1, live.Get("uses_focus"));
    }

    [Fact]
    public async Task SheetSaveAtALiveTable_ARefillByAChangedPick_ReachesTheTable_NotJustTheDifference_Async()
    {
        var actor = await SeedWithCounterAsync(2);
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);

        // The pick changed on the sheet (refill: back to 4 of 4) while the table spent one more.
        var live = await SaveSheetAsync(runtime, actor, edited => edited.Sheet["uses_focus"] = 4L,
            () => runtime.EnqueueAsync(new AdjustResourceCommand(actor.Id, "uses_focus", -1, "Focus")));

        Assert.Equal(4, live.Get("uses_focus"));
    }

    [Fact]
    public async Task SheetSaveAtALiveTable_AnEmptyingByAChangedPick_ReachesTheTable_Async()
    {
        var actor = await SeedWithCounterAsync(2);
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);

        // The pick changed on the sheet (empty: none left) while the table regained a use through the other tab.
        var live = await SaveSheetAsync(runtime, actor, edited => edited.Sheet["uses_focus"] = 0L,
            () => runtime.EnqueueAsync(new AdjustResourceCommand(actor.Id, "uses_focus", 1, "Focus")));

        Assert.Equal(0, live.Get("uses_focus"));
    }

    [Fact]
    public async Task SheetSaveAtALiveTable_ADroppedCounter_LeavesTheTable_AndANewOneArrives_Async()
    {
        var actor = await SeedWithCounterAsync(3, ("uses_rage", 2, 2));
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);

        var live = await SaveSheetAsync(runtime, actor, edited =>
        {
            edited.Sheet.Remove("uses_rage");
            edited.Sheet.Remove("uses_rage_Max");
            edited.Sheet["uses_surge"] = 1L;
            edited.Sheet["uses_surge_Max"] = 1L;
        });

        Assert.False(live.Pools.ContainsKey("uses_rage"));
        Assert.False(live.Pools.ContainsKey("uses_rage_Max"));
        Assert.Equal(3, live.Get("uses_focus"));
        Assert.Equal(1, live.Get("uses_surge"));
    }

    [Fact]
    public async Task ADawnOnlyRefillsTheCountersThatNameDawn_AndALongRestLeavesThemBe_Async()
    {
        var actor = await SeedWithCounterAsync(0, ("uses_rage", 0, 3));
        actor.Sheet["uses_focus_Rest"] = "dawn";
        actor.Sheet["uses_rage_Rest"] = "Long Rest";
        await _actors.SaveAsync(actor);
        var session = await SeedSessionAsync("camp");
        await using var runtime = StartRuntime("camp", session, actor);

        await runtime.EnqueueAsync(new DawnCommand(actor.Id));

        var live = runtime.GetEntity(actor.Id)!.Value.Get<ResourcePoolComponent>();
        Assert.Equal(4, live.Get("uses_focus"));
        Assert.Equal(0, live.Get("uses_rage"));
    }

    [Fact]
    public async Task ASheetSavedWithoutATable_OverAVersionItDidNotSee_IsRefused_AndKeepsTheOtherEdit_Async()
    {
        var actor = await SeedActorAsync("camp-version", 10, ownerId: "player");
        var dm = Access("camp-version", "dm", CampaignRole.DM);
        var player = Access("camp-version", "player", CampaignRole.Player);

        // The DM and the player both open the sheet between sessions; the player adds a feat and saves first.
        var dmCopy = (await _actors.GetVisibleAsync(actor.Id, dm))!;
        var mine = (await _actors.GetVisibleAsync(actor.Id, player))!;
        mine.Sheet["feats"] = new List<object> { "Alert" };
        Assert.True(await _actors.TrySaveAsync(mine, player));

        dmCopy.DmNotes = "Owes the guild.";
        var refused = await Assert.ThrowsAsync<Codex.Localization.LocalizedException>(() => _actors.TrySaveAsync(dmCopy, dm));
        Assert.Equal("Actor.Sheet.ChangedMeanwhile", refused.Key);
        Assert.Null((await LoadAsync(actor))!.DmNotes);

        // Taking the stored version as the one it replaces lets the DM save on purpose.
        DocumentVersions.Carry(dmCopy, (await _actors.GetVisibleAsync(actor.Id, dm))!);
        Assert.True(await _actors.TrySaveAsync(dmCopy, dm));
        Assert.Equal("Owes the guild.", (await LoadAsync(actor))!.DmNotes);
    }

    [Fact]
    public async Task APortraitChange_WritesOnlyThePortrait_AndLeavesTheSheetsEditsUnsavedButSaveable_Async()
    {
        var actor = await SeedActorAsync("camp-portrait", 10, ownerId: "player");
        var player = Access("camp-portrait", "player", CampaignRole.Player);
        var mine = (await _actors.GetVisibleAsync(actor.Id, player))!;
        mine.Sheet["Level"] = 99L; // half-typed, and against the rules

        Assert.True(await _actors.TrySetPortraitAsync(mine, true, player));
        var stored = (await LoadAsync(actor))!;
        Assert.True(stored.HasPortrait);
        Assert.Equal(1, stored.PortraitVersion);
        Assert.Equal(3, YamlValues.GetInt(stored.Sheet, "Level"));
        Assert.Equal(1, mine.PortraitVersion);

        // Only the portrait moved, so the page's copy is still the latest; and a stranger cannot set one.
        mine.Sheet["Level"] = 4L;
        Assert.True(await _actors.TrySaveAsync(mine, player));
        Assert.True((await LoadAsync(actor))!.HasPortrait);
        Assert.False(await _actors.TrySetPortraitAsync((await LoadAsync(actor))!, false, Access("camp-portrait", "stranger", CampaignRole.Player)));
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
            runtime.EnqueueAsync(SheetSave(edited, loaded, Access("camp", "stranger", CampaignRole.Player))));
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
