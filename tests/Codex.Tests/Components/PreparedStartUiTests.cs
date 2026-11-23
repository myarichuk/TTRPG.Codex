using Bunit;
using Codex.Core;
using Codex.Core.Components;
using Codex.Localization;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Campaign;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests.Components;

/// <summary>
/// The two places a prepared encounter is started from (the table's picker and the plan's editor), drawn against a real table: a real runtime manager over the embedded
/// database, so the messages are the ones a game master reads, the buttons follow a start that is running, and the editor's button follows whether a table runs.
/// </summary>
[Collection("RavenDb")]
public class PreparedStartUiTests : ComponentTestBase, IClassFixture<RavenDbFixture>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly RavenDbService _db;
    private readonly ActorRepository _actors;
    private readonly List<Flash> _messages = new();
    private int _started;

    private sealed class Hold(SemaphoreSlim gate) : IRuntimeCommand
    {
        public IReadOnlyCollection<string> AffectedActorIds => Array.Empty<string>();
        public void Apply(CampaignRuntime runtime) => gate.Wait(TimeSpan.FromSeconds(10));
        public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("Test", Array.Empty<EventMessage>());
    }

    private sealed record Table(PreparedEncounterService Service, CampaignRuntimeManager Manager, MemoryPlans Plans, CampaignDocument Campaign, CampaignAccess Dm, ActorDocument Pc);

    public PreparedStartUiTests(RavenDbFixture fixture)
    {
        _db = fixture.Open("StartUi_" + Guid.NewGuid());
        _actors = new ActorRepository(_db);
        // The container disposes what it built, so the database goes with the test.
        Services.AddSingleton(_ => _db);
        Services.AddSingleton<ILogger<PreparedEncounterPicker>>(NullLogger<PreparedEncounterPicker>.Instance);
        Services.AddSingleton<ILogger<EncounterList>>(NullLogger<EncounterList>.Instance);
        Services.AddSingleton<ILogger<EncounterEditor>>(NullLogger<EncounterEditor>.Instance);
        Services.AddSingleton(ViewerClock.ForZone("UTC"));
    }

    private async Task<Table> OpenAsync()
    {
        var components = new ComponentRegistry();
        components.Register<ResourcePoolComponent>();
        components.Register<AttributesComponent>();
        components.Register<ActiveEffectsComponent>();
        var sessions = new RavenSessionRepository(_db);
        var encounters = new RavenEncounterRepository(_db);
        var hooks = new CampaignLifecycleHooks();
        var campaigns = new CampaignRepository(_db, new NoOpSystemCatalog(), _actors, sessions, new RavenNoteRepository(_db), new RegionRepository(_db),
            new FactRepository(_db), new RavenHomebrewRepository(_db), encounters, lifecycle: hooks);
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, components, Substitute.For<IContentPackLoader>());
        await loader.LoadAndInitializeAsync(Path.Combine(TestPaths.ContentRoot, "systems"));
        var manager = new CampaignRuntimeManager(_actors, sessions, campaigns, encounters, components, loader, NullLoggerFactory.Instance, hooks);

        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Salt", SystemId = "srd5e" };
        await campaigns.SaveAsync(campaign);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var pc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Mira", Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-1", Visibility = ActorVisibility.Known,
            Sheet = new Dictionary<string, object> { ["HP"] = 12L, ["HP_Max"] = 12L, ["dex"] = 14L } };
        ActorSheetSync.ApplySheetToState(pc, loader.GetDefinition("srd5e"), components);
        await _actors.SaveAsync(pc);
        await manager.GetOrStartAsync(campaign, dm);

        var homebrew = Substitute.For<IHomebrewRepository>();
        var brews = new List<HomebrewEntryDocument>
        {
            new() { CampaignId = campaignId, SystemId = "srd5e", Type = "monster", EntryId = "bandit", Name = "Bandit", Properties = { ["hitPoints"] = 11L, ["dexterity"] = 14L } }
        };
        homebrew.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => brews.AsEnumerable());
        var wiki = Substitute.For<IWikiRepository>();
        wiki.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Array.Empty<WikiPageDocument>().AsEnumerable());
        var regions = Substitute.For<IRegionRepository>();
        regions.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Array.Empty<RegionDocument>().AsEnumerable());
        var registry = new ContentRegistry();
        var library = new CampaignLibrary(registry, homebrew, loader);
        var cast = new CastService(manager, registry, components, loader, _actors, wiki);
        var plans = new MemoryPlans();
        var service = new PreparedEncounterService(plans, regions, _actors, library, registry, cast, manager);
        Services.AddSingleton(service);
        Services.AddSingleton(manager);
        // Resolved once, so the container owns it and disposes it with the test.
        _ = Services.GetRequiredService<RavenDbService>();
        return new Table(service, manager, plans, campaign, dm, pc);
    }

    private static PreparedLine Bandits(int count) => new()
    {
        Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "bandit", IsHomebrew = true, Name = "Bandit", Count = count
    };

    private static async Task<PreparedEncounterDocument> PlanAsync(Table t, string name, params PreparedLine[] lines)
    {
        var plan = new PreparedEncounterDocument { CampaignId = t.Campaign.Id, Name = name, Status = PreparedStatus.Ready, Lines = lines.ToList() };
        Assert.True(await t.Plans.SaveAsync(plan, t.Dm));
        return plan;
    }

    private IRenderedComponent<PreparedEncounterPicker> DrawPicker(Table t)
    {
        var cut = Render<PreparedEncounterPicker>(p => p
            .Add(x => x.Campaign, t.Campaign)
            .Add(x => x.Access, t.Dm)
            .Add(x => x.OnMessage, EventCallback.Factory.Create<Flash>(this, f => _messages.Add(f)))
            .Add(x => x.OnStarted, EventCallback.Factory.Create(this, () => _started++)));
        cut.WaitForState(() => cut.FindAll(Id("start-prepared-plan")).Count > 0, Patience);
        return cut;
    }

    private static void Confirm(IRenderedComponent<PreparedEncounterPicker> cut)
    {
        cut.Find(Id("start-prepared-plan")).Click();
        cut.WaitForState(() => cut.FindAll(Id("confirm-accept")).Count > 0, Patience);
    }

    [Fact]
    public async Task ThePicker_AsksFirst_AndCancellingStartsNothing()
    {
        var t = await OpenAsync();
        await PlanAsync(t, "Ambush", Bandits(2));
        var cut = DrawPicker(t);

        Confirm(cut);

        Assert.Equal("Start Ambush? 2 creatures join the fight and roll initiative.", cut.Find(".dialog-body p").TextContent);
        cut.FindAll(".dialog-actions button")[0].Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(Id("confirm-accept"))), Patience);
        Assert.Empty(_messages);
        Assert.Equal(0, _started);
        Assert.Null(t.Manager.TryGet(t.Campaign.Id)!.ActiveEncounter);
    }

    [Fact]
    public async Task ThePicker_SaysThePlanStarted_AndTellsThePageSoItCanMoveFocus()
    {
        var t = await OpenAsync();
        await PlanAsync(t, "Ambush", Bandits(2));
        var cut = DrawPicker(t);

        Confirm(cut);
        cut.Find(Id("confirm-accept")).Click();

        cut.WaitForAssertion(() => Assert.Equal(1, _started), Patience);
        var said = Assert.Single(_messages);
        Assert.Equal(FlashTone.Success, said.Tone);
        Assert.Equal("Ambush started.", said.Text);
        Assert.Equal(2, t.Manager.TryGet(t.Campaign.Id)!.ActiveEncounter!.Participants.Count);
    }

    [Fact]
    public async Task ThePicker_NamesWhoDidNotJoin_WhenSomeOfThePlanCouldNotBeMade()
    {
        var t = await OpenAsync();
        var gone = new PreparedLine { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "dragon", IsHomebrew = true, Name = "Old dragon", Count = 1 };
        await PlanAsync(t, "Ambush", Bandits(1), gone);
        var cut = DrawPicker(t);

        Confirm(cut);
        cut.Find(Id("confirm-accept")).Click();

        cut.WaitForAssertion(() => Assert.Single(_messages), Patience);
        Assert.Equal(FlashTone.Info, _messages[0].Tone);
        Assert.Equal("Ambush started. These did not join: Old dragon.", _messages[0].Text);
        Assert.Equal(1, _started);
    }

    [Fact]
    public async Task ThePicker_SaysWhyWhenAnEncounterIsAlreadyRunning_AndStartsNothing()
    {
        var t = await OpenAsync();
        await PlanAsync(t, "Ambush", Bandits(2));
        await t.Manager.TryGet(t.Campaign.Id)!.EnqueueAsync(new RollInitiativeCommand(t.Pc.Id, "1d20", "owner"));
        var cut = DrawPicker(t);

        Confirm(cut);
        cut.Find(Id("confirm-accept")).Click();

        cut.WaitForAssertion(() => Assert.Single(_messages), Patience);
        Assert.Equal(FlashTone.Error, _messages[0].Tone);
        Assert.Equal("End the current encounter first.", _messages[0].Text);
        Assert.Equal(0, _started);
    }

    [Fact]
    public async Task ThePickersButtons_AreOffWhileAStartRuns_AndBackAfterIt()
    {
        var t = await OpenAsync();
        await PlanAsync(t, "Ambush", Bandits(1));
        await PlanAsync(t, "Second", Bandits(1));
        var runtime = t.Manager.TryGet(t.Campaign.Id)!;
        using var gate = new SemaphoreSlim(0);
        var cut = DrawPicker(t);
        try
        {
            // The loop is held, so the start waits on its first creature.
            _ = runtime.EnqueueAsync(new Hold(gate));
            Confirm(cut);
            cut.Find(Id("confirm-accept")).Click();

            cut.WaitForAssertion(() => Assert.All(cut.FindAll(Id("start-prepared-plan")), b => Assert.True(b.HasAttribute("disabled"))), Patience);
        }
        finally
        {
            gate.Release();
        }

        cut.WaitForAssertion(() => Assert.Equal(1, _started), Patience);
        // The order is now running; the page that owns the picker removes it, so nothing is left to be pressed twice.
        Assert.Single(_messages);
    }

    // ---- The editor ----

    private IRenderedComponent<CascadingValue<CampaignContext>> DrawEditor(Table t, string route)
    {
        var ctx = new CampaignContext
        {
            Campaign = t.Campaign, Access = t.Dm, UserId = t.Dm.UserId, Actors = Array.Empty<ActorDocument>(), AllActors = Array.Empty<ActorDocument>(),
            ReloadAsync = () => Task.CompletedTask, Notify = _ => { }, AccessChangedAsync = () => Task.CompletedTask
        };
        var cut = Render<CascadingValue<CampaignContext>>(p => p.Add(x => x.Value, ctx).AddChildContent<EncountersSection>(c => c.Add(x => x.Route, route)));
        cut.WaitForState(() => cut.FindAll(Id("encounter-name")).Count > 0, Patience);
        return cut;
    }

    [Fact]
    public async Task TheEditorsStartButton_ShowsWithATableRunning_AndIsOffWhileThereAreUnsavedChanges()
    {
        var t = await OpenAsync();
        var plan = await PlanAsync(t, "Ambush", Bandits(1));
        var cut = DrawEditor(t, PreparedEncounterService.RouteOf(plan.Id));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(Id("encounter-start-table")));
            Assert.False(cut.Find(Id("encounter-start")).HasAttribute("disabled"));
            Assert.Empty(cut.FindAll(Id("encounter-start-hint")));
        }, Patience);

        cut.Find(Id("encounter-name")).Input("Ambush, renamed");

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Find(Id("encounter-start")).HasAttribute("disabled"));
            Assert.Equal("Save your changes to start it.", cut.Find(Id("encounter-start-hint")).TextContent);
        }, Patience);
    }

    [Fact]
    public async Task TheEditorsStartButton_StartsThePlan_AndShowsTheTable()
    {
        var t = await OpenAsync();
        var plan = await PlanAsync(t, "Ambush", Bandits(1));
        var cut = DrawEditor(t, PreparedEncounterService.RouteOf(plan.Id));

        cut.Find(Id("encounter-start")).Click();
        cut.WaitForState(() => cut.FindAll(Id("confirm-accept")).Count > 0, Patience);
        cut.Find(Id("confirm-accept")).Click();

        cut.WaitForAssertion(() => Assert.EndsWith($"/campaigns/{t.Campaign.Id}/table", Services.GetRequiredService<NavigationManager>().Uri), Patience);
        Assert.Single(t.Manager.TryGet(t.Campaign.Id)!.ActiveEncounter!.Participants);
    }

    [Fact]
    public async Task TheEditorsStartButton_SaysWhyWhenItIsRefused_AndGoesWhenTheTableEnds()
    {
        var t = await OpenAsync();
        var plan = await PlanAsync(t, "Ambush", Bandits(1));
        await t.Manager.TryGet(t.Campaign.Id)!.EnqueueAsync(new RollInitiativeCommand(t.Pc.Id, "1d20", "owner"));
        var cut = DrawEditor(t, PreparedEncounterService.RouteOf(plan.Id));

        cut.Find(Id("encounter-start")).Click();
        cut.WaitForState(() => cut.FindAll(Id("confirm-accept")).Count > 0, Patience);
        cut.Find(Id("confirm-accept")).Click();

        cut.WaitForAssertion(() => Assert.Equal("End the current encounter first.", cut.Find(Id("encounter-start-problem")).TextContent), Patience);

        await t.Manager.EvictAsync(t.Campaign.Id);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Id("encounter-start-table"))), Patience);
        Assert.Empty(cut.FindAll(Id("encounter-start")));
    }
}
