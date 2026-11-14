using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>Links between sheets (docs/CREW_LINKS_DESIGN.md, CL2: the hosts and the widgets). The system is a fixture with no game's names in it:
/// a <c>member</c> sheet links to a <c>group</c> sheet, whose abilities and upgrades carry <c>members</c> grants.</summary>
internal static class LinkFixture
{
    internal sealed class Entries(params ContentEntry[] entries) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => entries.Where(e => e.Type == type);
    }

    internal static readonly ContentEntry Composed = new()
    {
        Type = "upgrade", Id = "composed", Name = "Composed", SystemId = "link-hosts", PackId = "test",
        Properties = { ["members"] = new Dictionary<string, object> { ["modifiers"] = new List<object> { new Dictionary<string, object> { ["target"] = "stressBoxes", ["amount"] = 1L } } } }
    };

    internal static readonly ContentEntry Resolve = new()
    {
        Type = "ability", Id = "resolve", Name = "Resolve", SystemId = "link-hosts", PackId = "test",
        Properties =
        {
            ["members"] = new Dictionary<string, object>
            {
                ["uses"] = new Dictionary<string, object>
                {
                    ["key"] = "resolve", ["max"] = 2L,
                    ["recovers"] = new List<object> { new Dictionary<string, object> { ["on"] = "downtime" } }
                }
            }
        }
    };

    // Each member picks their own action: the choice is stored on the member's sheet, scoped by this entry's id.
    internal static readonly ContentEntry Deadly = new()
    {
        Type = "ability", Id = "deadly", Name = "Deadly", SystemId = "link-hosts", PackId = "test",
        Properties =
        {
            ["members"] = new Dictionary<string, object>
            {
                ["adds"] = new List<object>
                {
                    new Dictionary<string, object> { ["to"] = "actions", ["choice"] = "deadly", ["choose"] = 1L, ["from"] = new List<object> { "hunt", "prowl" } }
                }
            }
        }
    };

    internal static readonly Entries All = new(Composed, Resolve, Deadly);

    internal static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: link-hosts
        name: Link hosts
        entityTypes:
          - { type: ability, label: Ability }
          - { type: upgrade, label: Upgrade }
        moments: [{ key: downtime, label: Downtime }]
        sheetKinds:
          - kind: member
            label: Member
            links:
              - { key: group, kind: group, label: Group }
            features:
              - { list: abilities }
              - { link: group, list: abilities, property: members }
              - { link: group, list: upgrades, property: members }
            values:
              - { key: tier, input: true, default: 1 }
            lists:
              - { key: abilities, label: Abilities, type: ability }
            keyedLists:
              - key: actions
                label: Actions
                itemFields: [{ key: rating }]
                keys: [{ key: hunt, label: Hunt }, { key: prowl, label: Prowl }]
            tracks:
              - { key: stress, max: "9 + modifiers('stressBoxes')" }
            layout:
              - { section: Standing, widgets: [{ widget: link, key: group }] }
          - kind: group
            label: Group
            values:
              - { key: rank, input: true, default: 1 }
            lists:
              - { key: abilities, label: Abilities, type: ability }
              - { key: upgrades, label: Upgrades, type: upgrade }
            layout:
              - { section: Standing, widgets: [{ widget: members }] }
        """);

    internal static ActorDocument Group(string campaignId, string name = "The Group", string[]? upgrades = null, string[]? abilities = null)
    {
        var doc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = name, Kind = ActorKind.NonPlayerCharacter };
        doc.Sheet["kind"] = "group";
        doc.Sheet["upgrades"] = (upgrades ?? Array.Empty<string>()).Cast<object>().ToList();
        doc.Sheet["abilities"] = (abilities ?? Array.Empty<string>()).Cast<object>().ToList();
        return doc;
    }

    internal static ActorDocument Member(string campaignId, string name, string? groupId, params (string Key, object Value)[] more)
    {
        var doc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = name, Kind = ActorKind.PlayerCharacter };
        doc.Sheet["kind"] = "member";
        if (groupId != null) doc.Sheet["link__group"] = groupId;
        foreach (var (key, value) in more) doc.Sheet[key] = value;
        return doc;
    }
}

/// <summary>The link and members widgets' helpers, the layout check, and what the join drops: no database.</summary>
public class CrewLinkWidgetsTests
{
    private static readonly SystemDefinition System = LinkFixture.System;
    private static SheetLinkDefinition GroupLink => System.SheetKinds[0].Links.Single();

    [Fact]
    public void TheLayoutCheck_AcceptsTheLinkAndMembersWidgets_AndRefusesAWrongOne()
    {
        Assert.Empty(SheetLayout.Problems(System));
        Assert.Contains("link", SheetLayout.GenericWidgets);
        Assert.Contains("members", SheetLayout.GenericWidgets);

        var wrongLink = SystemDefinitionLoader.Parse(YamlWith("{ widget: link, key: nowhere }", "{ widget: members }"));
        Assert.Contains(SheetLayout.Problems(wrongLink), p => p.Contains("link: nowhere is not a link of member"));
        var noKey = SystemDefinitionLoader.Parse(YamlWith("{ widget: link }", "{ widget: members }"));
        Assert.Contains(SheetLayout.Problems(noKey), p => p.Contains("link needs a key"));
        // Members on a kind nothing links to shows nothing.
        var membersOnMember = SystemDefinitionLoader.Parse(YamlWith("{ widget: members }", "{ widget: members }"));
        Assert.Contains(SheetLayout.Problems(membersOnMember), p => p.Contains("members: no sheet kind links to member"));
    }

    private static string YamlWith(string memberWidget, string groupWidget) => """
        id: link-hosts
        name: Link hosts
        entityTypes:
          - { type: ability, label: Ability }
        sheetKinds:
          - kind: member
            links:
              - { key: group, kind: group, label: Group }
            values:
              - { key: tier, input: true, default: 1 }
            lists:
              - { key: abilities, label: Abilities, type: ability }
            layout:
              - { section: Standing, widgets: [MEMBERWIDGET] }
          - kind: group
            values:
              - { key: rank, input: true, default: 1 }
            layout:
              - { section: Standing, widgets: [GROUPWIDGET] }
        """.Replace("MEMBERWIDGET", memberWidget, StringComparison.Ordinal).Replace("GROUPWIDGET", groupWidget, StringComparison.Ordinal);

    [Fact]
    public void TheLinkSelect_OffersTheVisibleActorsOfTheLinksKind_ByName_AndNotItself()
    {
        var campaign = "c";
        var alpha = LinkFixture.Group(campaign, "Alpha");
        var bravo = LinkFixture.Group(campaign, "bravo");
        var retired = LinkFixture.Group(campaign, "Old"); retired.Retired = true;
        var aMember = LinkFixture.Member(campaign, "Zed", null);
        var me = LinkFixture.Member(campaign, "Me", null);
        var noKind = new ActorDocument { Id = "plain", CampaignId = campaign, Name = "Plain" };

        var options = LinkWidgets.Options(new[] { bravo, alpha, retired, aMember, me, noKind }, GroupLink, me.Id, null, viewerIsDm: true);

        Assert.Equal(new[] { alpha.Id, bravo.Id }, options.Select(o => o.Id));
        Assert.Equal(new[] { "Alpha", "bravo" }, options.Select(o => o.Name));
    }

    [Fact]
    public void TheLinkSelect_KeepsTheStoredActorOffered_AndAnUnseenOneIsNotNamed()
    {
        var campaign = "c";
        var seen = LinkFixture.Group(campaign, "Seen");
        var hidden = LinkFixture.Group(campaign, "Secret Crew");
        var oldOne = LinkFixture.Group(campaign, "Disbanded"); oldOne.Retired = true;

        // A retired actor already linked still shows, so what is stored is visible; a stranger to this viewer is simply not there.
        var options = LinkWidgets.Options(new[] { seen, oldOne }, GroupLink, "me", oldOne.Id, viewerIsDm: false);
        Assert.Equal(new[] { oldOne.Id, seen.Id }.OrderBy(i => i), options.Select(o => o.Id).OrderBy(i => i));

        var visible = new[] { seen };
        Assert.Null(LinkWidgets.Chosen(visible, hidden.Id, false));
        Assert.Equal("Seen", LinkWidgets.Chosen(visible, seen.Id, false)!.Name);
        Assert.Null(LinkWidgets.Chosen(visible, null, false));

        // The stored id stays on the sheet whether or not the viewer sees that actor: a save does not drop it.
        var sheet = new Dictionary<string, object> { ["kind"] = "member", ["link__group"] = hidden.Id };
        Assert.Equal(hidden.Id, LinkWidgets.Stored(sheet, "group"));
        Assert.DoesNotContain(LinkWidgets.Options(visible, GroupLink, "me", hidden.Id, false), o => o.Name.Contains("Secret"));
    }

    [Fact]
    public void TheMembersList_NamesTheVisibleActorsWhoseSheetsLinkHere_AndNoOtherLink()
    {
        var campaign = "c";
        var group = LinkFixture.Group(campaign, "Group");
        var other = LinkFixture.Group(campaign, "Other");
        var one = LinkFixture.Member(campaign, "Quinn", group.Id);
        var two = LinkFixture.Member(campaign, "Ash", group.Id);
        var elsewhere = LinkFixture.Member(campaign, "Rho", other.Id);
        var unlinked = LinkFixture.Member(campaign, "Tam", null);
        var gone = LinkFixture.Member(campaign, "Vale", group.Id); gone.Retired = true;

        var members = LinkWidgets.Members(new[] { one, two, elsewhere, unlinked, gone, group, other }, System, "group", group.Id, viewerIsDm: true);

        Assert.Equal(new[] { "Ash", "Quinn" }, members.Select(m => m.Name));
        Assert.Empty(LinkWidgets.Members(new[] { elsewhere }, System, "group", group.Id, true));
    }

    [Fact]
    public void TheMembersList_LeavesOutAMaskedMember_ForAPlayer()
    {
        var group = LinkFixture.Group("c", "Group");
        var known = LinkFixture.Member("c", "Quinn", group.Id);
        // What the repository hands a player for an actor the GM keeps unidentified: a mask with a name and no sheet, so it cannot be told to link here.
        var mask = new ActorDocument { Id = "masked", CampaignId = "c", Name = "A hooded figure", Visibility = ActorVisibility.Unknown, PublicName = "A hooded figure" };

        var members = LinkWidgets.Members(new[] { known, mask }, System, "group", group.Id, viewerIsDm: false);

        Assert.Equal("Quinn", Assert.Single(members).Name);
    }

    [Fact]
    public void TheJoin_DropsEveryLinkKey_AndAFeatureNameCarriesTheLinksLabelNotTheActorsName()
    {
        var sheet = new Dictionary<string, object> { ["kind"] = "member", ["link__group"] = "g1", ["link__other"] = "g2", ["tier"] = 2L };

        var dropped = SheetLinks.WithoutLinks(sheet);

        Assert.Equal(new[] { "kind", "tier" }, dropped.Keys.OrderBy(k => k));
        Assert.True(SheetLinks.IsLinkKey("link__group"));
        Assert.False(SheetLinks.IsLinkKey("choice__link__x"));

        // A link feature is named by its entry and the link's label; the linked actor's name is nowhere in it.
        var group = LinkFixture.Group("c", "The Secret Crew", upgrades: new[] { "composed" });
        var member = new Dictionary<string, object> { ["kind"] = "member", ["link__group"] = group.Id };
        var features = new SheetRuleEngine(System, LinkFixture.All).ActiveFeatures(member, new ActorLinkedSheets(new[] { group }));
        Assert.Equal("Composed (Group)", Assert.Single(features).Name);
    }

    [Fact]
    public void ARetiredOrMissingLinkedActor_IsHeld_NotLeaked_AndTheWidgetSaysWhy()
    {
        var group = LinkFixture.Group("c", "Old Crew", upgrades: new[] { "composed" });
        var member = new Dictionary<string, object> { [Codex.Core.Rules.SheetModel.KindKey] = "member", ["link__group"] = group.Id };
        var engine = new SheetRuleEngine(System, LinkFixture.All);

        // In the trash: its sheet is not handed out, the link is unknown, the widget says deleted.
        group.Retired = true;
        var trashed = new ActorLinkedSheets(new[] { group });
        Assert.Null(trashed.Sheet(group.Id));
        Assert.Equal(LinkState.Unknown, SheetLinks.State(member, "group", trashed));
        Assert.Empty(engine.ActiveFeatures(member, trashed));
        Assert.True(engine.Evaluate(member, trashed).LinksUnknown);
        Assert.Equal(LinkedActorStatus.Deleted, LinkWidgets.Hold(member, "group", trashed));

        // Restored: it counts again, and the widget has nothing to say.
        group.Retired = false;
        var restored = new ActorLinkedSheets(new[] { group });
        Assert.Equal(LinkState.Known, SheetLinks.State(member, "group", restored));
        Assert.Equal("Composed (Group)", Assert.Single(engine.ActiveFeatures(member, restored)).Name);
        Assert.Null(LinkWidgets.Hold(member, "group", restored));

        // Not in the campaign at all: not found. No linked sheets supplied, or no link: nothing to say.
        Assert.Equal(LinkedActorStatus.NotFound, LinkWidgets.Hold(member, "group", new ActorLinkedSheets(Array.Empty<ActorDocument>())));
        Assert.Null(LinkWidgets.Hold(member, "group", null));
        Assert.Null(LinkWidgets.Hold(new Dictionary<string, object>(), "group", trashed));
    }

    [Fact]
    public void SheetReferences_IgnoresLinkKeys()
    {
        var sheet = new Dictionary<string, object> { ["kind"] = "member", ["link__group"] = "an-actor-id" };
        Assert.Empty(SheetReferences.Of(System, sheet, LinkFixture.All));
        Assert.Empty(SheetReferences.Missing(System, sheet, LinkFixture.All));
    }
}

/// <summary>The live table with links: the runtime resolves a member's linked sheet from its cached documents, keeps a reverse index of who links
/// to whom, and names the linkers in <c>StateChanged</c>.</summary>
[Collection("RavenDb")]
public class CrewLinkRuntimeTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly ComponentRegistry _registry;

    public CrewLinkRuntimeTests(RavenDbFixture fixture)
    {
        _dbService = new RavenDbService(fixture.DbPath, "CrewLinks_" + Guid.NewGuid(), runInMemory: true);
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    private async Task<ActorDocument> StoreAsync(ActorDocument doc)
    {
        var pool = new ResourcePoolComponent();
        pool.Set("HP", 10);
        pool.Set("HP_Max", 10);
        doc.State = _registry.Snapshot(new object[] { pool });
        await _actors.SaveAsync(doc);
        return doc;
    }

    [Fact]
    public async Task ARetiredCrew_LoadedForTheSheetPage_IsDeletedAndUnknown_ThenKnownAgainOnceRestored()
    {
        var campaign = Guid.NewGuid().ToString();
        var group = await StoreAsync(LinkFixture.Group(campaign, abilities: new[] { "resolve" }));
        var member = await StoreAsync(LinkFixture.Member(campaign, "Quinn", group.Id));
        var ids = ActorLinkedSheets.LinkedIds(member.Sheet);

        group.Retired = true;
        await _actors.SaveAsync(group);
        var trashed = new ActorLinkedSheets(await _actors.GetForRulesAsync(Gm(campaign), ids));
        Assert.Null(trashed.Sheet(group.Id));
        Assert.Equal(LinkedActorStatus.Deleted, trashed.Status(group.Id));
        Assert.Equal(LinkState.Unknown, SheetLinks.State(member.Sheet, "group", trashed));

        group.Retired = false;
        await _actors.SaveAsync(group);
        var restored = new ActorLinkedSheets(await _actors.GetForRulesAsync(Gm(campaign), ids));
        Assert.NotNull(restored.Sheet(group.Id));
        Assert.Equal(LinkState.Known, SheetLinks.State(member.Sheet, "group", restored));
    }

    private async Task<CampaignRuntime> TableAsync(string campaignId, params ActorDocument[] actors)
    {
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: LinkFixture.System) { Resolver = LinkFixture.All };
        runtime.Hydrate(actors);
        return runtime;
    }

    private static CampaignAccess Gm(string campaignId) => new(campaignId, "owner", CampaignRole.DM);

    [Fact]
    public async Task AMembersLinkMadeCounter_JoinsTheTableFull_EvenWhenTheGroupComesAfterItInTheList()
    {
        var campaign = Guid.NewGuid().ToString();
        var group = await StoreAsync(LinkFixture.Group(campaign, abilities: new[] { "resolve" }));
        var member = await StoreAsync(LinkFixture.Member(campaign, "Quinn", group.Id));

        await using var runtime = await TableAsync(campaign, member, group); // the member is hydrated first

        var pool = runtime.GetEntity(member.Id)!.Value.Get<ResourcePoolComponent>();
        Assert.Equal(2, Assert.Single(pool.Pools, p => SheetRuleEngine.IsUsesKey(p.Key) && !p.Key.EndsWith(SheetRuleEngine.UsesMaxSuffix, StringComparison.Ordinal)).Value);
        Assert.NotNull(runtime.Linked.Sheet(group.Id));
        Assert.Null(runtime.Linked.Sheet("nobody"));
    }

    [Fact]
    public async Task ALinkToAnActorTheTableDoesNotHold_IsUnknown_NotAnEmptyLink_SoWhatItMadeIsKept()
    {
        var campaign = Guid.NewGuid().ToString();
        var group = await StoreAsync(LinkFixture.Group(campaign, abilities: new[] { "resolve" }));
        var quinn = await StoreAsync(LinkFixture.Member(campaign, "Quinn", group.Id, ("uses_resolve", 1L), ("uses_resolve_Max", 2L)));

        await using var runtime = await TableAsync(campaign, quinn); // the group is not at the table (retired, say)

        Assert.Null(runtime.LinkedFor(quinn.Sheet));
        var sheet = runtime.GetActorDocument(quinn.Id)!.Sheet;
        Assert.Equal(1L, Convert.ToInt64(sheet["uses_resolve"]));
        Assert.Equal(2L, Convert.ToInt64(sheet["uses_resolve_Max"]));
        Assert.Null(runtime.Linked.Sheet(group.Id));
    }

    [Fact]
    public async Task APageReadsACopyOfTheLinkedSheet_RepublishedAfterEachCommand()
    {
        var campaign = Guid.NewGuid().ToString();
        var group = await StoreAsync(LinkFixture.Group(campaign));
        var quinn = await StoreAsync(LinkFixture.Member(campaign, "Quinn", group.Id));
        await using var runtime = await TableAsync(campaign, group, quinn);
        var before = runtime.Linked.Sheet(group.Id)!;
        Assert.NotSame(runtime.GetActorDocument(group.Id)!.Sheet, before);

        var edited = new ActorDocument { Id = group.Id, CampaignId = campaign, Name = "The Group", Kind = ActorKind.NonPlayerCharacter, Sheet = new Dictionary<string, object>(group.Sheet) };
        edited.Sheet["upgrades"] = new List<object> { "composed" };
        await runtime.EnqueueAsync(new ReplaceActorCommand(edited, group.Sheet, Gm(campaign), LinkFixture.System));

        Assert.Empty((IEnumerable<object>)before["upgrades"]);
        Assert.Equal("composed", Assert.Single((IEnumerable<object>)runtime.Linked.Sheet(group.Id)!["upgrades"]));
    }

    [Fact]
    public async Task TheReverseIndex_IsBuiltOnHydrate_AndFollowsAReplace_AnAdd_AndARemove()
    {
        var campaign = Guid.NewGuid().ToString();
        var group = await StoreAsync(LinkFixture.Group(campaign));
        var otherGroup = await StoreAsync(LinkFixture.Group(campaign, "Rivals"));
        var quinn = await StoreAsync(LinkFixture.Member(campaign, "Quinn", group.Id));
        var ash = await StoreAsync(LinkFixture.Member(campaign, "Ash", group.Id));
        var tam = await StoreAsync(LinkFixture.Member(campaign, "Tam", null));
        await using var runtime = await TableAsync(campaign, group, otherGroup, quinn, ash, tam);

        Assert.Equal(new[] { ash.Id, quinn.Id }.OrderBy(i => i), runtime.ActorsLinkingTo(group.Id).OrderBy(i => i));
        Assert.Empty(runtime.ActorsLinkingTo(otherGroup.Id));

        // A sheet replace that moves a link moves the index.
        var moved = new ActorDocument { Id = quinn.Id, CampaignId = campaign, Name = "Quinn", Kind = ActorKind.PlayerCharacter, Sheet = new Dictionary<string, object>(quinn.Sheet) };
        moved.Sheet["link__group"] = otherGroup.Id;
        await runtime.EnqueueAsync(new ReplaceActorCommand(moved, quinn.Sheet, Gm(campaign), LinkFixture.System));
        Assert.Equal(new[] { ash.Id }, runtime.ActorsLinkingTo(group.Id));
        Assert.Equal(new[] { quinn.Id }, runtime.ActorsLinkingTo(otherGroup.Id));

        // Removing a link by a replace leaves it too.
        var cleared = new ActorDocument { Id = ash.Id, CampaignId = campaign, Name = "Ash", Kind = ActorKind.PlayerCharacter, Sheet = new Dictionary<string, object>(ash.Sheet) };
        cleared.Sheet.Remove("link__group");
        await runtime.EnqueueAsync(new ReplaceActorCommand(cleared, ash.Sheet, Gm(campaign), LinkFixture.System));
        Assert.Empty(runtime.ActorsLinkingTo(group.Id));

        // An actor added mid-table joins the index; one removed leaves it.
        var late = await StoreAsync(LinkFixture.Member(campaign, "Late", group.Id));
        await runtime.EnqueueAsync(new HydrateActorCommand(late));
        Assert.Equal(new[] { late.Id }, runtime.ActorsLinkingTo(group.Id));
        await runtime.EnqueueAsync(new RemoveActorCommand(late.Id));
        Assert.Empty(runtime.ActorsLinkingTo(group.Id));
    }

    [Fact]
    public async Task WhenACommandChangesAnActor_StateChangedAlsoNamesTheActorsThatLinkToIt()
    {
        var campaign = Guid.NewGuid().ToString();
        var group = await StoreAsync(LinkFixture.Group(campaign));
        var quinn = await StoreAsync(LinkFixture.Member(campaign, "Quinn", group.Id));
        var ash = await StoreAsync(LinkFixture.Member(campaign, "Ash", group.Id));
        var tam = await StoreAsync(LinkFixture.Member(campaign, "Tam", null));
        await using var runtime = await TableAsync(campaign, group, quinn, ash, tam);
        IReadOnlyCollection<string>? named = null;
        runtime.StateChanged += ids => named = ids;

        await runtime.EnqueueAsync(new ApplyDamageCommand(group.Id, "HP", 1, "test"));

        Assert.NotNull(named);
        Assert.Equal(new[] { group.Id, quinn.Id, ash.Id }.OrderBy(i => i), named!.OrderBy(i => i));

        // A command on a member names only the member: nothing links to it.
        await runtime.EnqueueAsync(new ApplyDamageCommand(tam.Id, "HP", 1, "test"));
        Assert.Equal(new[] { tam.Id }, named);
    }

    [Fact]
    public async Task ARemovedGroup_NamesItsMembersInTheNextStateChanged()
    {
        var campaign = Guid.NewGuid().ToString();
        var group = await StoreAsync(LinkFixture.Group(campaign));
        var quinn = await StoreAsync(LinkFixture.Member(campaign, "Quinn", group.Id));
        await using var runtime = await TableAsync(campaign, group, quinn);
        IReadOnlyCollection<string>? named = null;
        runtime.StateChanged += ids => named = ids;

        await runtime.EnqueueAsync(new RemoveActorCommand(group.Id));

        Assert.Contains(quinn.Id, named!);
    }

    [Fact]
    public async Task ASaveThroughTheLiveRuntime_KeepsAMembersLinkMadePicks_AndTheirCounterAndStress()
    {
        var campaign = Guid.NewGuid().ToString();
        var group = await StoreAsync(LinkFixture.Group(campaign, upgrades: new[] { "composed" }, abilities: new[] { "resolve", "deadly" }));
        var quinn = await StoreAsync(LinkFixture.Member(campaign, "Quinn", group.Id,
            ("choice__deadly__deadly", new List<object> { "hunt" }), ("uses_resolve", 1L), ("uses_resolve_Max", 2L), ("stress", 10L)));
        await using var runtime = await TableAsync(campaign, group, quinn);

        // The page works the sheet out with the live group before it saves, as it does (prune, then recompute), with one ordinary edit.
        var edited = new ActorDocument { Id = quinn.Id, CampaignId = campaign, Name = "Quinn", Kind = ActorKind.PlayerCharacter, Sheet = new Dictionary<string, object>(quinn.Sheet) };
        edited.Sheet["tier"] = 2L;
        var engine = new SheetRuleEngine(LinkFixture.System, LinkFixture.All);
        engine.PruneChoices(edited.Sheet, runtime.Linked);
        engine.Recompute(edited.Sheet, runtime.Linked);
        await runtime.EnqueueAsync(new ReplaceActorCommand(edited, quinn.Sheet, Gm(campaign), LinkFixture.System));

        foreach (var sheet in new[] { runtime.GetActorDocument(quinn.Id)!.Sheet, (await _actors.GetVisibleAsync(quinn.Id, Gm(campaign)))!.Sheet })
        {
            Assert.Equal(2L, Convert.ToInt64(sheet["tier"]));
            Assert.Equal(group.Id, sheet["link__group"]);
            Assert.Equal("hunt", Assert.Single((IEnumerable<object>)sheet["choice__deadly__deadly"]));
            Assert.Equal(1L, Convert.ToInt64(sheet["uses_resolve"]));
            Assert.Equal(10L, Convert.ToInt64(sheet["stress"]));
        }

        // And the rules, given the live group, still count Composed: stress 10 is within the box count, so nothing clamps it.
        var worked = new Dictionary<string, object>(runtime.GetActorDocument(quinn.Id)!.Sheet);
        var evaluation = new SheetRuleEngine(LinkFixture.System, LinkFixture.All).Recompute(worked, runtime.Linked);
        Assert.Equal(10L, Convert.ToInt64(worked["stress"]));
        Assert.Contains(evaluation.Features, f => f.Name == "Composed (Group)");
    }
}

/// <summary>The join review: a link names an actor of one campaign, so neither the campaign copy nor the vault copy keeps one.</summary>
public class CrewLinkJoinTests : IClassFixture<RavenDbFixture>
{
    private readonly RavenDbService _db;
    private readonly ActorRepository _actors;
    private readonly RavenVaultRepository _vault;
    private readonly CampaignRepository _campaigns;

    public CrewLinkJoinTests(RavenDbFixture fixture)
    {
        _db = fixture.Open(fixture.DbName);
        _actors = new ActorRepository(_db);
        _vault = new RavenVaultRepository(_db);
        _campaigns = new CampaignRepository(_db, new NoOpSystemCatalog(), _actors, new RavenSessionRepository(_db), new RavenNoteRepository(_db),
            new RegionRepository(_db), new FactRepository(_db), new RavenHomebrewRepository(_db), new RavenEncounterRepository(_db), vaultRepository: _vault);
    }

    private static string User() => "user-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task AcceptingAVaultCharacter_DropsEveryLinkKeyFromTheCampaignCopy_AndTheVaultCopyHasNone_Async()
    {
        var (gm, player) = (User(), User());
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = gm, Name = "Camp", SystemId = "srd5e" };
        campaign.Members.Add(new CampaignMember { UserId = player, Role = CampaignRole.Player });
        await _campaigns.SaveAsync(campaign);
        var asGm = new CampaignAccess(campaign.Id, gm, CampaignRole.DM);
        var asPlayer = new CampaignAccess(campaign.Id, player, CampaignRole.Player);

        // A vault character made from a campaign copy carries no link.
        var character = await _vault.CreateAsync(player, "srd5e", "Quinn", actor => { actor.Sheet["link__crew"] = "another-campaigns-actor"; actor.Sheet["tier"] = 2L; });
        Assert.DoesNotContain(character.Sheet.Keys, SheetLinks.IsLinkKey);
        Assert.Equal(2L, Convert.ToInt64(character.Sheet["tier"]));

        // A request, and the GM's reviewed sheet, drop them too.
        var request = (await _vault.AskAsync(asPlayer, new CharacterRequestDocument
        {
            VaultActorId = character.Id, Name = "Quinn", Sheet = new Dictionary<string, object> { ["link__crew"] = "x", ["tier"] = 2L }
        }))!;
        Assert.DoesNotContain(request.Sheet.Keys, SheetLinks.IsLinkKey);
        var accepted = await _vault.AcceptAsync(request.Id, asGm, new Dictionary<string, object> { ["link__crew"] = "y", ["link__other"] = "z", ["tier"] = 2L });
        var copy = accepted!.Value.Added!;
        Assert.DoesNotContain(copy.Sheet.Keys, SheetLinks.IsLinkKey);
        Assert.Equal(2L, Convert.ToInt64(copy.Sheet["tier"]));

        // Taking the campaign copy back to the vault leaves its link behind.
        copy.Sheet["link__crew"] = "this-campaigns-crew";
        Assert.True(await _vault.UpdateFromCopyAsync(character.Id, player, "Quinn", copy.Sheet, null));
        Assert.DoesNotContain((await _vault.GetAsync(character.Id, player))!.Sheet.Keys, SheetLinks.IsLinkKey);
    }
}
