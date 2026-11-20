using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Consistency;
using Codex.Core.Rules;
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

    private const string Street = """
            id: street
            name: Street
            version: 1.0.0
            attributes:
              - { key: str, label: Strength, default: 10, min: 3, max: 18 }
            resources:
              - { key: Grit, label: Grit, maxKey: Grit_Max, default: 4 }
            """;

    private async Task<Table> OpenAsync(bool live, string yaml = Street, string systemId = "street", ContentRegistry? registry = null, Func<string, IEnumerable<ActorDocument>>? extraActors = null,
        ActorKind kind = ActorKind.PlayerCharacter)
    {
        Directory.CreateDirectory(Path.Combine(root, systemId));
        File.WriteAllText(Path.Combine(root, systemId, "system.yaml"), yaml);
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
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Night City", SystemId = systemId };
        await campaigns.SaveAsync(campaign);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
        var pool = new ResourcePoolComponent();
        pool.Set("Grit", 4);
        pool.Set("Grit_Max", 4);
        var actor = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Rook", Kind = kind, Biography = "A runner.",
            Sheet = new Dictionary<string, object> { ["str"] = 10, ["Grit"] = 4, ["Grit_Max"] = 4 }
        };
        ActorSheetSync.ApplySheetToState(actor, loader.GetDefinition(systemId), components);
        await actorRepo.SaveAsync(actor);
        foreach (var other in extraActors?.Invoke(campaignId) ?? Array.Empty<ActorDocument>()) await actorRepo.SaveAsync(other);
        if (live) await manager.GetOrStartAsync(campaign, dm);

        var homebrew = Substitute.For<IHomebrewRepository>();
        homebrew.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Array.Empty<HomebrewEntryDocument>().AsEnumerable());
        var wiki = Substitute.For<IWikiRepository>();
        wiki.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Array.Empty<WikiPageDocument>().AsEnumerable());
        var regions = Substitute.For<IRegionRepository>();
        regions.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Array.Empty<RegionDocument>().AsEnumerable());
        var facts = Substitute.For<IFactRepository>();
        facts.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>(), Arg.Any<IReadOnlySet<string>>()).Returns(_ => Array.Empty<FactDocument>().AsEnumerable());
        var library = new CampaignLibrary(registry ??= new ContentRegistry(), homebrew, loader);
        var cast = new CastService(manager, registry, components, loader, actorRepo, wiki);
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

    // ---- a creature's stat block laid over a cast member (an actor edit with "stats") ----

    private const string StreetWithCreatures = Street + """

            entityTypes:
              - { type: monster, label: Creature, actorBlueprint: true }
            """;

    /// <summary>A pack creature of the street system whose block prints Strength 17.</summary>
    private static ContentRegistry BrutePack()
    {
        var registry = new ContentRegistry();
        var brute = new ContentEntry { Id = "brute", Type = "monster", SystemId = "street", PackId = "street-pack", Name = "Brute", Properties = { ["str"] = 17L } };
        registry.RegisterEntry(brute, 0);
        registry.RegisterActor(YamlContentPackLoader.ToActorBlueprint(brute, new ContentTypeDefinition { Type = "monster", ActorBlueprint = true }), 0);
        return registry;
    }

    private static async Task RestatAsync(Table t)
    {
        var check = await t.Service.CheckAsync(t.Campaign, t.Dm, $$"""{"entries": [{"ref": "r", "kind": "edit", "target": "existing:actor:{{t.Actor.Id}}", "stats": "monster/brute"}]}""");
        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.NotNull(check.Bundle.Drafts.Single().Stats);
        await t.Service.SaveAsync(t.Campaign, t.Dm, check.Bundle);
    }

    [Fact]
    public async Task ARestat_OfACreatureAtALiveTable_ShowsThereAtOnce_AndSurvivesTheTablesWrites_AndALaterSheetPageSave()
    {
        var t = await OpenAsync(live: true, StreetWithCreatures, registry: BrutePack(), kind: ActorKind.NonPlayerCharacter);

        await RestatAsync(t);

        var live = t.Manager.TryGet(t.Campaign.Id)!;
        Assert.Equal(17, Convert.ToInt32(live.GetActorDocument(t.Actor.Id)!.Sheet["str"])); // the table plays the new block at once
        // The table does something to the creature, and marks it dirty.
        await live.EnqueueAsync(new AdjustResourceCommand(t.Actor.Id, "Grit", -1, "test"));
        // The sheet page opened at the table loads the table's copy and saves an unrelated change through it, against what it loaded.
        var page = (await t.Actors.GetVisibleAsync(t.Actor.Id, t.Dm))!;
        page.Sheet = new Dictionary<string, object>(live.GetActorDocument(t.Actor.Id)!.Sheet);
        var loaded = new Dictionary<string, object>(page.Sheet);
        page.Sheet["Grit_Max"] = 6L;
        Assert.True(await t.Manager.TrySaveActorAsync(page, loaded, t.Dm, null));
        await t.Manager.EvictAsync(t.Campaign.Id);

        var stored = (await t.Actors.GetVisibleAsync(t.Actor.Id, t.Dm))!;
        Assert.Equal(17, Convert.ToInt32(stored.Sheet["str"])); // the restat
        Assert.Equal(3, Convert.ToInt32(stored.Sheet["Grit"])); // the table's own change
        Assert.Equal(6, Convert.ToInt32(stored.Sheet["Grit_Max"])); // the sheet page's change
        Assert.Equal("street:brute", stored.BlueprintId);
    }

    [Fact]
    public async Task ARestat_OfACreatureNotAtATable_IsStoredAsBefore()
    {
        var t = await OpenAsync(live: false, StreetWithCreatures, registry: BrutePack(), kind: ActorKind.NonPlayerCharacter);

        await RestatAsync(t);

        var stored = (await t.Actors.GetVisibleAsync(t.Actor.Id, t.Dm))!;
        Assert.Equal(17, Convert.ToInt32(stored.Sheet["str"]));
        Assert.Equal(4, Convert.ToInt32(stored.Sheet["Grit"])); // what the block does not print stays
        Assert.Equal("A runner.", stored.Biography);
        Assert.Equal("street:brute", stored.BlueprintId);
    }

    // ---- a character whose sheet depends on the sheet its link points to ----

    private const string Crew = """
        id: link-hosts
        name: Link hosts
        actorFields:
          - { key: body, label: Body, type: Number, min: 0, max: 10 }
        entityTypes:
          - { type: upgrade, label: Upgrade }
        sheetKinds:
          - kind: member
            label: Member
            links:
              - { key: group, kind: group, label: Group }
            features:
              - { link: group, list: upgrades, property: members }
            tracks:
              - { key: stress, max: "9 + body + modifiers('stressBoxes')" }
            layout:
              - { section: Standing, widgets: [{ widget: link, key: group }] }
          - kind: group
            label: Group
            lists:
              - { key: upgrades, label: Upgrades, type: upgrade }
            layout:
              - { section: Standing, widgets: [{ widget: members }] }
        """;

    [Fact]
    public async Task ASheetEdit_RecomputesWhatTheCharactersLinkBrings_AsTheSheetPageDoes()
    {
        var registry = new ContentRegistry();
        registry.RegisterEntry(LinkFixture.Composed, 0);
        var group = default(ActorDocument)!;
        var t = await OpenAsync(live: false, Crew, "link-hosts", registry, campaignId =>
        {
            group = LinkFixture.Group(campaignId, upgrades: new[] { "composed" });
            return new[] { group };
        });
        // The member is the one the harness made; link it to the group and give it the values the sheet page would have stored.
        t.Actor.Sheet["kind"] = "member";
        t.Actor.Sheet["link__group"] = group.Id;
        t.Actor.Sheet["body"] = 2L;
        t.Actor.Sheet["stress_Max"] = 12L; // 9 + body 2 + the upgrade's one box
        await t.Actors.SaveAsync(t.Actor);

        await ApplyAsync(t, """{"body": 5}""");

        var stored = (await t.Actors.GetVisibleAsync(t.Actor.Id, t.Dm))!;
        Assert.Equal(5, Convert.ToInt32(stored.Sheet["body"]));
        Assert.Equal(15, Convert.ToInt32(stored.Sheet["stress_Max"])); // not the stale 12 a recompute without the group's sheet leaves
    }
}

public class SnapshotLinkedSheetsTests
{
    [Fact]
    public void TheSnapshotsActors_AreWhatALinkResolvesTo_AsTheSheetPagesAre()
    {
        var group = Fx.Actor("actors/g", "Group", sheet: new Dictionary<string, object> { ["kind"] = "group" });
        var gone = Fx.Actor("actors/r", "Old", retired: true, sheet: new Dictionary<string, object> { ["kind"] = "group" });
        var linked = new SnapshotLinkedSheets(new[] { group, gone });

        Assert.Equal("group", linked.Sheet("actors/g")!["kind"]);
        Assert.Equal(LinkedActorStatus.Available, linked.Status("actors/g"));
        Assert.Null(linked.Sheet("actors/r")); // a retired actor's sheet is never handed out
        Assert.Equal(LinkedActorStatus.Deleted, linked.Status("actors/r"));
        Assert.Null(linked.Sheet("actors/nope"));
        Assert.Equal(LinkedActorStatus.NotFound, linked.Status("actors/nope"));
    }
}
