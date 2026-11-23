using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// P11c of docs/COMPANIONS_DESIGN.md at a live table, on the real four-degrees system: a companion at 0 hit points goes down in an encounter like any creature,
/// a session start gives its owner a hero point and the companion none, the daily preparations reach a familiar through its own sheet and leave its picks alone,
/// and the owner's sheet lists the companion and the familiar linked to it.
/// </summary>
[Collection("RavenDb")]
public class CompanionTableTests : IClassFixture<RavenDbFixture>, IAsyncLifetime, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenEncounterRepository _encounters;
    private readonly ComponentRegistry _registry;
    private readonly ContentRegistry _content = new();
    private readonly PluginLoader _loader;

    public CompanionTableTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("CompanionTable_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _encounters = new RavenEncounterRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<AttributesComponent>();
        _registry.Register<ActiveEffectsComponent>();
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_content));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _dbService.Dispose();

    private SystemDefinition System => _loader.GetDefinition("four-degrees")!;

    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "four-degrees-core", "four-degrees-rules" };

    private sealed class PackResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries("four-degrees", type, Packs).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("four-degrees", type, Packs);
    }

    private sealed record Party(CampaignRuntime Runtime, string CampaignId, ActorDocument Owner, ActorDocument Companion, ActorDocument Familiar);

    private async Task<ActorDocument> SeedAsync(string campaignId, string name, string kind, Action<Dictionary<string, object>> sheet)
    {
        var doc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = name, Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-brin" };
        doc.Sheet["kind"] = kind;
        doc.Sheet["level"] = 5L;
        doc.Sheet["HP"] = 8L;
        doc.Sheet["HP_Max"] = 8L;
        doc.Sheet["heroPoints"] = 0L; // a pool every one of them has, so a hero point that arrives is seen
        sheet(doc.Sheet);
        ActorSheetSync.ApplySheetToState(doc, System, _registry);
        await _actors.SaveAsync(doc);
        return doc;
    }

    /// <summary>The familiar's picks, as the sheet page saves them: the sheet is recomputed, which writes the keys that tell the table what its list is for.</summary>
    private List<object> FamiliarPicks => _content.GetEntries("four-degrees", "familiar-ability", Packs).Select(e => (object)e.Id).Take(2).ToList();

    private async Task<Party> PartyAsync()
    {
        var campaignId = Guid.NewGuid().ToString();
        var owner = await SeedAsync(campaignId, "Brin", "character", _ => { });
        var companion = await SeedAsync(campaignId, "Fang", "companion", s => { s["link__owner"] = owner.Id; s["type"] = new List<object> { "wolf" }; });
        var familiar = await SeedAsync(campaignId, "Wisp", "familiar", s =>
        {
            s["link__master"] = owner.Id;
            s["abilities"] = FamiliarPicks;
            new SheetRuleEngine(System, new PackResolver(_content)).Recompute(s);
        });
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, null, System);
        runtime.Hydrate(new[] { owner, companion, familiar });
        return new Party(runtime, campaignId, owner, companion, familiar);
    }

    private static int Pool(Party party, ActorDocument actor, string key) => party.Runtime.Snapshot.Actor(actor.Id)!.Pool(key) ?? 0;

    [Fact]
    public async Task ACompanionBroughtToZero_GoesDownInAnEncounter_AndItsOwnerStaysUp()
    {
        var party = await PartyAsync();
        await using var _ = party.Runtime;
        await party.Runtime.EnqueueAsync(new SetInitiativeCommand(party.Owner.Id, 17));
        await party.Runtime.EnqueueAsync(new SetInitiativeCommand(party.Companion.Id, 12));
        Assert.Equal(new[] { party.Owner.Id, party.Companion.Id }, party.Runtime.ActiveEncounter!.Participants.Select(p => p.ActorId));

        await party.Runtime.EnqueueAsync(new ApplyDamageCommand(party.Companion.Id, "HP", 8, "dm"));

        var fallen = party.Runtime.Snapshot.Actor(party.Companion.Id)!;
        Assert.Equal(0, fallen.Pool("HP"));
        Assert.True(fallen.IsDown);
        Assert.False(fallen.IsDead);
        // A player character's companion is knocked out, not killed: the dying rule gives it Dying 1 as it would its owner.
        Assert.Equal(1L, YamlValues.ToNumber(party.Runtime.GetActorDocument(party.Companion.Id)!.Sheet.GetValueOrDefault("condition_dying")));
        Assert.False(party.Runtime.Snapshot.Actor(party.Owner.Id)!.IsDown);
        Assert.Equal(8, Pool(party, party.Owner, "HP"));
    }

    [Fact]
    public async Task ASessionStart_GivesTheOwnerAHeroPoint_AndTheCompanionAndFamiliarNone()
    {
        var party = await PartyAsync();
        await using var _ = party.Runtime;

        // The party bar sends the moment to a member of the party only.
        Assert.True(SystemMoments.ReachesPartyMember(System, party.Owner.Sheet, "sessionStart"));
        Assert.False(SystemMoments.ReachesPartyMember(System, party.Companion.Sheet, "sessionStart"));
        Assert.False(SystemMoments.ReachesPartyMember(System, party.Familiar.Sheet, "sessionStart"));

        // And the command itself never gives the handler's point to a creature that is not one, even when it is sent there.
        foreach (var actor in new[] { party.Owner, party.Companion, party.Familiar })
        {
            await party.Runtime.EnqueueAsync(new MomentCommand(actor.Id, "sessionStart"));
        }

        Assert.Equal(1, Pool(party, party.Owner, "heroPoints"));
        Assert.Equal(0, Pool(party, party.Companion, "heroPoints"));
        Assert.Equal(0, Pool(party, party.Familiar, "heroPoints"));
    }

    [Fact]
    public async Task TheDailyPreparations_ReachAFamiliarThroughItsAbilityList_AndLeaveItsPicksAlone()
    {
        var party = await PartyAsync();
        await using var _ = party.Runtime;
        var familiar = party.Familiar.Sheet;
        var picks = FamiliarPicks;

        // The familiar's own sheet says the daily preparations are when its picks usually change; a session start is not.
        Assert.True(SystemMoments.Matters(System, familiar, "dailyPrep"));
        Assert.False(SystemMoments.Matters(System, familiar, "sessionStart"));
        Assert.True(SystemMoments.ReachesPartyMember(System, familiar, "dailyPrep"));

        await party.Runtime.EnqueueAsync(new MomentCommand(party.Familiar.Id, "dailyPrep"));

        // The moment is no lock and no clearing: the picks are as they were, and can be changed afterwards like any edit.
        Assert.Equal(picks, party.Runtime.GetActorDocument(party.Familiar.Id)!.Sheet["abilities"]);
        Assert.Equal(new[] { ("abilities", "Abilities") }, SheetRuleEngine.ChoicesToChangeAfter(familiar, "dailyPrep"));
    }

    [Fact]
    public async Task TheOwnersSheet_ListsTheCompanionAndTheFamiliarLinkedToIt_AndNoOtherActor()
    {
        var party = await PartyAsync();
        await using var _ = party.Runtime;
        var stranger = await SeedAsync(party.CampaignId, "Stray", "companion", s => s["link__owner"] = "someone-else");
        var retired = await SeedAsync(party.CampaignId, "Old Dog", "companion", s => s["link__owner"] = party.Owner.Id);
        retired.Retired = true;
        await _actors.SaveAsync(retired);

        var visible = (await _actors.GetVisibleForCampaignAsync(new CampaignAccess(party.CampaignId, "dm-user", CampaignRole.DM))).ToList();
        Assert.Contains(visible, a => a.Id == stranger.Id);

        // The owner's sheet is the generic one, which ends with the members widget; what it lists is who links to the owner.
        var widgets = Assert.Single(SheetLayout.For(System, party.Owner.Sheet)).Widgets;
        Assert.Equal("members", widgets[^1].Widget);
        var members = LinkWidgets.Members(visible, System, "character", party.Owner.Id, true);
        Assert.Equal(new[] { "Fang", "Wisp" }, members.Select(m => m.Name).ToArray());

        // A companion's own sheet lists nobody and has no such widget.
        Assert.DoesNotContain(SheetLayout.For(System, party.Companion.Sheet).SelectMany(s => s.Widgets), w => w.Widget == "members");
        Assert.Empty(LinkWidgets.Members(visible, System, "companion", party.Companion.Id, true));
    }
}
