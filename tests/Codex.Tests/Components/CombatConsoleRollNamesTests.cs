using Bunit;
using Codex.Core;
using Codex.Core.Components;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests.Components;

/// <summary>The table's roll log names whoever rolled, also an actor retired since: a complex hazard's actor is retired when its encounter ends, and the GM's log must still read
/// "Rolling Stone" for its secret roll, not the actor's id. The console is drawn against a real table (a real runtime manager over the embedded database).</summary>
[Collection("RavenDb")]
public class CombatConsoleRollNamesTests : ComponentTestBase, IClassFixture<RavenDbFixture>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly RavenDbService _db;
    private readonly ActorRepository _actors;

    public CombatConsoleRollNamesTests(RavenDbFixture fixture)
    {
        _db = fixture.Open("ConsoleNames_" + Guid.NewGuid());
        _actors = new ActorRepository(_db);
        Services.AddSingleton(_ => _db);
        Services.AddSingleton(ViewerClock.ForZone("UTC"));
        Services.AddSingleton<ILogger<CombatConsole>>(NullLogger<CombatConsole>.Instance);
        Services.AddSingleton<ILogger<HazardsTable>>(NullLogger<HazardsTable>.Instance);
    }

    private sealed record Table(CampaignDocument Campaign, CampaignAccess Dm, ActorDocument Pc, ActorDocument Stone, SystemDefinition System);

    /// <summary>A live table with a player character and a hidden hazard actor that rolled its secret initiative and was then retired.</summary>
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
        var system = loader.GetDefinition("srd5e")!;
        var manager = new CampaignRuntimeManager(_actors, sessions, campaigns, encounters, components, loader, NullLoggerFactory.Instance, hooks);

        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Salt", SystemId = "srd5e" };
        await campaigns.SaveAsync(campaign);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var pc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Mira", Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-1", Visibility = ActorVisibility.Known,
            Sheet = new Dictionary<string, object> { ["HP"] = 12L, ["HP_Max"] = 12L, ["dex"] = 14L } };
        var stone = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Rolling Stone", Kind = ActorKind.Monster, Visibility = ActorVisibility.Hidden,
            Hazard = new HazardActorLink { RegionId = "r", LocationId = "p", Entry = "hazard/rolling-stone" },
            Sheet = new Dictionary<string, object> { ["HP"] = 100L, ["HP_Max"] = 100L } };
        foreach (var actor in new[] { pc, stone })
        {
            ActorSheetSync.ApplySheetToState(actor, system, components);
            await _actors.SaveAsync(actor);
        }

        var runtime = await manager.GetOrStartAsync(campaign, dm);
        await runtime.EnqueueAsync(new JoinInitiativeCommand(stone.Id, "1d20+8", "owner"));
        Assert.True(await manager.TryRetireActorAsync(stone.Id, dm));
        stone.Retired = true;

        var homebrew = Substitute.For<IHomebrewRepository>();
        homebrew.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Array.Empty<HomebrewEntryDocument>().AsEnumerable());
        var wiki = Substitute.For<IWikiRepository>();
        wiki.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Array.Empty<WikiPageDocument>().AsEnumerable());
        var regions = Substitute.For<IRegionRepository>();
        regions.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Array.Empty<RegionDocument>().AsEnumerable());
        var registry = new ContentRegistry();
        var library = new CampaignLibrary(registry, homebrew, loader);
        var cast = new CastService(manager, registry, components, loader, _actors, wiki);
        Services.AddSingleton(new PreparedEncounterService(new MemoryPlans(), regions, _actors, library, registry, cast, manager));
        Services.AddSingleton(manager);
        Services.AddSingleton<ISystemCatalog>(new NoOpSystemCatalog());
        Services.AddSingleton<IContentRegistry>(registry);
        Services.AddSingleton(library);
        Services.AddSingleton(components);
        Services.AddSingleton<IActorRepository>(_actors);
        Services.AddSingleton<ISessionRepository>(sessions);
        Services.AddSingleton(regions);
        _ = Services.GetRequiredService<RavenDbService>();
        return new Table(campaign, dm, pc, stone, system);
    }

    private IRenderedComponent<CombatConsole> Draw(Table t, IEnumerable<ActorDocument> inPlay, IEnumerable<ActorDocument>? all) => Render<CombatConsole>(p => p
        .Add(x => x.CampaignId, t.Campaign.Id)
        .Add(x => x.Campaign, t.Campaign)
        .Add(x => x.Access, t.Dm)
        .Add(x => x.CurrentUserId, "owner")
        .Add(x => x.Actors, inPlay)
        .Add(x => x.AllActors, all)
        .Add(x => x.Definition, t.System));

    [Fact]
    public async Task TheGms_RollLog_NamesARetiredActorsRoll_NotItsId()
    {
        var t = await OpenAsync();

        var cut = Draw(t, new[] { t.Pc }, new[] { t.Pc, t.Stone });

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(Id("roll-log-entry"))), Patience);
        var entry = cut.Find(Id("roll-log-entry")).TextContent;
        Assert.Contains("Rolling Stone", entry);
        Assert.DoesNotContain(t.Stone.Id, entry);
    }

    [Fact]
    public async Task APlayersRollLog_NeverShowsTheHiddenHazardsSecretRoll_NorItsNameOrId()
    {
        var t = await OpenAsync();

        // A player's lists leave a Hidden actor out altogether, and a secret roll is the GM's.
        var cut = Render<CombatConsole>(p => p
            .Add(x => x.CampaignId, t.Campaign.Id)
            .Add(x => x.Campaign, t.Campaign)
            .Add(x => x.Access, new CampaignAccess(t.Campaign.Id, "player-1", CampaignRole.Player))
            .Add(x => x.CurrentUserId, "player-1")
            .Add(x => x.Actors, new[] { t.Pc })
            .Add(x => x.AllActors, new[] { t.Pc })
            .Add(x => x.Definition, t.System));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(Id("table-card"))), Patience);
        Assert.Empty(cut.FindAll(Id("roll-log-entry")));
        Assert.DoesNotContain("Rolling Stone", cut.Markup);
        Assert.DoesNotContain(t.Stone.Id, cut.Markup);
    }
}
