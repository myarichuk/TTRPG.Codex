using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Consistency;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests.Consistency;

/// <summary>
/// A sheet edit applied through the review gate to a character who is at a live table (docs/WORLD_BUILDER.md, 9). The sheet page saves such a
/// character through the runtime (<c>ReplaceActorCommand</c>), because the table holds the character's live state and writes it back on its
/// next event; an edit written straight to the store was put back by that write. Real runtime, real actor store (embedded Raven).
/// </summary>
[Collection("RavenDb")]
public class LiveTableSheetEditTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService db;
    private readonly string root = Path.Combine(Path.GetTempPath(), "codex-live-sheet-" + Guid.NewGuid().ToString("N")[..8]);

    public LiveTableSheetEditTests(RavenDbFixture fixture) => db = fixture.Open("LiveSheet_" + Guid.NewGuid());

    public void Dispose()
    {
        db.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed record Table(WorldBuilderService Service, CampaignRuntimeManager Manager, ActorRepository Actors, CampaignDocument Campaign, CampaignAccess Dm, ActorDocument Actor);

    private async Task<Table> OpenAsync(bool live)
    {
        Directory.CreateDirectory(Path.Combine(root, "street"));
        File.WriteAllText(Path.Combine(root, "street", "system.yaml"), """
            id: street
            name: Street
            version: 1.0.0
            attributes:
              - { key: str, label: Strength, default: 10, min: 3, max: 18 }
            resources:
              - { key: Grit, label: Grit, maxKey: Grit_Max, default: 4 }
            """);
        var components = new ComponentRegistry();
        components.Register<ResourcePoolComponent>();
        var actorRepo = new ActorRepository(db);
        var sessions = new RavenSessionRepository(db);
        var encounters = new RavenEncounterRepository(db);
        var hooks = new CampaignLifecycleHooks();
        var campaigns = new CampaignRepository(db, new NoOpSystemCatalog(), actorRepo, sessions, new RavenNoteRepository(db), new RegionRepository(db),
            new FactRepository(db), new RavenHomebrewRepository(db), encounters, lifecycle: hooks);
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, components, Substitute.For<IContentPackLoader>());
        await loader.LoadAndInitializeAsync(root);
        var manager = new CampaignRuntimeManager(actorRepo, sessions, campaigns, encounters, components, loader, NullLoggerFactory.Instance, hooks);

        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Night City", SystemId = "street" };
        await campaigns.SaveAsync(campaign);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var pool = new ResourcePoolComponent();
        pool.Set("Grit", 4);
        pool.Set("Grit_Max", 4);
        var actor = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Rook", Kind = ActorKind.PlayerCharacter, Biography = "A runner.",
            Sheet = new Dictionary<string, object> { ["str"] = 10, ["Grit"] = 4, ["Grit_Max"] = 4 }
        };
        ActorSheetSync.ApplySheetToState(actor, loader.GetDefinition("street"), components);
        await actorRepo.SaveAsync(actor);
        if (live) await manager.GetOrStartAsync(campaign, dm);

        var homebrew = Substitute.For<IHomebrewRepository>();
        homebrew.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Array.Empty<HomebrewEntryDocument>().AsEnumerable());
        var wiki = Substitute.For<IWikiRepository>();
        wiki.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Array.Empty<WikiPageDocument>().AsEnumerable());
        var regions = Substitute.For<IRegionRepository>();
        regions.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Array.Empty<RegionDocument>().AsEnumerable());
        var facts = Substitute.For<IFactRepository>();
        facts.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>(), Arg.Any<IReadOnlySet<string>>()).Returns(_ => Array.Empty<FactDocument>().AsEnumerable());
        var library = new CampaignLibrary(new ContentRegistry(), homebrew, loader);
        var cast = new CastService(manager, new ContentRegistry(), components, loader, actorRepo, wiki);
        var service = new WorldBuilderService(loader, library, homebrew, wiki, facts, cast, regions, campaigns, actorRepo, null!);
        return new Table(service, manager, actorRepo, campaign, dm, actor);
    }

    private static string Edit(string actorId, string sheet) => $$"""{"entries": [{"ref": "s", "kind": "edit", "target": "existing:sheet:{{actorId}}", "sheet": {{sheet}}}]}""";

    private static async Task<WorldBuildResult> ApplyAsync(Table t, string sheet)
    {
        var check = await t.Service.CheckAsync(t.Campaign, t.Dm, Edit(t.Actor.Id, sheet));
        Assert.True(check.Ok, string.Join("; ", check.Errors));
        return await t.Service.SaveAsync(t.Campaign, t.Dm, check.Bundle);
    }

    [Fact]
    public async Task ASheetEdit_OfACharacterAtALiveTable_SurvivesTheTablesNextWrite()
    {
        var t = await OpenAsync(live: true);

        await ApplyAsync(t, """{"str": 14}""");
        // The next thing that happens at the table marks the character dirty; the table writes its copy of them back.
        await t.Manager.TryGet(t.Campaign.Id)!.EnqueueAsync(new AdjustResourceCommand(t.Actor.Id, "Grit", -1, "test"));
        await t.Manager.EvictAsync(t.Campaign.Id);

        var stored = (await t.Actors.GetVisibleAsync(t.Actor.Id, t.Dm))!;
        Assert.Equal(14, Convert.ToInt32(stored.Sheet["str"]));
        Assert.Equal(3, Convert.ToInt32(stored.Sheet["Grit"])); // the table's own change is kept too
    }

    [Fact]
    public async Task ASheetEdit_ShowsAtTheTableAtOnce_NotOnlyAfterTheTableRestarts()
    {
        var t = await OpenAsync(live: true);

        await ApplyAsync(t, """{"str": 15}""");

        var live = t.Manager.TryGet(t.Campaign.Id)!;
        Assert.Equal(15, Convert.ToInt32(live.GetActorDocument(t.Actor.Id)!.Sheet["str"]));
    }

    [Fact]
    public async Task ASheetEdit_OfACharacterWhoIsNotAtATable_IsStoredAsBefore()
    {
        var t = await OpenAsync(live: false);

        await ApplyAsync(t, """{"str": 12}""");

        var stored = (await t.Actors.GetVisibleAsync(t.Actor.Id, t.Dm))!;
        Assert.Equal(12, Convert.ToInt32(stored.Sheet["str"]));
    }
}
