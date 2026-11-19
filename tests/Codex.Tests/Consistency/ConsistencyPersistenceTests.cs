using Codex.Core.Consistency;
using Codex.Persistence;

namespace Codex.Tests.Consistency;

/// <summary>How the stored documents become a snapshot (no database), and how the DM's dismissals are kept (embedded Raven).</summary>
public class ConsistencySnapshotMapperTests
{
    private static CampaignDocument Campaign(string? enforcement = null) => new() { Id = "campaigns/1", SystemId = "demo", RulesEnforcement = enforcement };

    [Fact]
    public void TheDocumentsBecomeViews_AndTheCheckerFindsWhatIsWrongInThem()
    {
        var pages = new[]
        {
            new WikiPageDocument { Id = "wikipages/1-A", Slug = "road", Title = "Road", Body = "Past [[The Mere of Dead Men]].", Visibility = WikiVisibility.Party, ActorId = "" },
            new WikiPageDocument { Id = "wikipages/2-A", Slug = "hidden", Title = "Hidden", Body = "x", Visibility = WikiVisibility.DmOnly, PlaceId = "p-gone" }
        };
        var actors = new[]
        {
            new ActorDocument { Id = "actors/1-A", CampaignId = "campaigns/1", Name = "Old Tam", Kind = ActorKind.NonPlayerCharacter, Biography = "" },
            new ActorDocument { Id = "actors/2-A", CampaignId = "campaigns/1", Name = "Wolf", Kind = ActorKind.Monster },
            new ActorDocument { Id = "actors/3-A", CampaignId = "campaigns/1", Name = "Trap", Kind = ActorKind.NonPlayerCharacter, Hazard = new HazardActorLink() },
            new ActorDocument { Id = "actors/4-A", CampaignId = "", Name = "Vault hero", Kind = ActorKind.PlayerCharacter }
        };
        var region = new RegionDocument { Id = "regions/1", Locations = { new LocationDocument { Id = "p-1", Name = "Docks", Description = "Wet. TODO" } } };
        var fact = new FactDocument { Id = "facts/1-A", Summary = "The keeper lies." };

        var snapshot = ConsistencySnapshots.From(pages, actors, new[] { region }, new[] { fact }, Campaign(), null, Codex.Core.Rules.NullEntryResolver.Instance);

        Assert.Equal(new[] { true, false }, snapshot.Pages.Select(p => p.PartyVisible));
        Assert.Null(snapshot.Pages[0].ActorId);
        Assert.Equal(new[] { "actors/1-A", "actors/2-A", "actors/3-A" }, snapshot.Actors.Select(a => a.Id));
        Assert.Equal(new[] { ActorRole.NonPlayerCharacter, ActorRole.Monster, ActorRole.Monster }, snapshot.Actors.Select(a => a.Role));
        Assert.Equal("regions/1", Assert.Single(snapshot.Places).RegionId);

        var ids = ConsistencyChecker.Run(snapshot).Select(i => i.Id).ToList();
        Assert.Contains("broken-link:page:wikipages/1-A:body:the-mere-of-dead-men", ids);
        Assert.Contains("dangling-reference:page:wikipages/2-A:placeId", ids);
        Assert.Contains("empty-bio:actor:actors/1-A:biography", ids);
        Assert.Contains("todo-marker:place:p-1:description", ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("empty-bio:actor:actors/2-A") || id.StartsWith("empty-bio:actor:actors/3-A") || id.Contains("actors/4-A"));
    }

    [Theory]
    [InlineData(null, "off")] // the demo system is not loaded, so it has no sheet rules to enforce
    [InlineData("strict", "strict")]
    [InlineData("warn", "warn")]
    [InlineData("off", "off")]
    public void TheCampaignsHouseRules_DecideWhetherTheSheetRulesAreReported(string? setting, string expected)
    {
        var system = Fx.System(); // enforcement: strict in the system itself
        var snapshot = ConsistencySnapshots.From(Array.Empty<WikiPageDocument>(), Array.Empty<ActorDocument>(), Array.Empty<RegionDocument>(), Array.Empty<FactDocument>(),
            Campaign(setting), setting == null ? null : system, Codex.Core.Rules.NullEntryResolver.Instance);
        Assert.Equal(expected, snapshot.Enforcement);
    }
}

[Collection("RavenDb")]
public class ConsistencyDismissalTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _db;
    private readonly RavenConsistencyDismissalRepository _repo;

    public ConsistencyDismissalTests(RavenDbFixture fixture)
    {
        _db = fixture.Open("ConsistencyDismissals_" + Guid.NewGuid());
        _repo = new RavenConsistencyDismissalRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private static CampaignAccess Dm(string campaignId) => new(campaignId, "gm", CampaignRole.DM);
    private static CampaignAccess Player(string campaignId) => new(campaignId, "p1", CampaignRole.Player);

    private async Task<string> CampaignAsync()
    {
        var campaign = new CampaignDocument { Id = "campaigns/" + Guid.NewGuid(), OwnerId = "gm", Name = "Dismissals", SystemId = "demo" };
        campaign.Members.Add(new CampaignMember { UserId = "gm", Role = CampaignRole.DM });
        campaign.Members.Add(new CampaignMember { UserId = "p1", Role = CampaignRole.Player });
        using var session = _db.Store.OpenAsyncSession();
        await session.StoreAsync(campaign);
        await session.SaveChangesAsync();
        return campaign.Id;
    }

    [Fact]
    public async Task TheDmDismissesAndRestores_InTheOrderTheyDidIt_AndDismissingTwiceChangesNothing()
    {
        var id = await CampaignAsync();
        Assert.Empty(await _repo.ListAsync(Dm(id)));

        Assert.True(await _repo.DismissAsync(Dm(id), "broken-link:page:a:body:x", "a red link on purpose"));
        Assert.True(await _repo.DismissAsync(Dm(id), "empty-bio:actor:b:biography", null));
        Assert.False(await _repo.DismissAsync(Dm(id), "broken-link:page:a:body:x", "again"));

        var list = await _repo.ListAsync(Dm(id));
        Assert.Equal(new[] { "broken-link:page:a:body:x", "empty-bio:actor:b:biography" }, list.Select(d => d.IssueId));
        Assert.Equal("a red link on purpose", list[0].Note);
        Assert.Equal("gm", list[0].DismissedBy);

        Assert.True(await _repo.RestoreAsync(Dm(id), "broken-link:page:a:body:x"));
        Assert.False(await _repo.RestoreAsync(Dm(id), "broken-link:page:a:body:x"));
        Assert.Equal(new[] { "empty-bio:actor:b:biography" }, (await _repo.ListAsync(Dm(id))).Select(d => d.IssueId));
    }

    [Fact]
    public async Task APlayer_OrSomeoneNotInTheCampaign_CannotDismissRestoreOrRead()
    {
        var id = await CampaignAsync();
        Assert.True(await _repo.DismissAsync(Dm(id), "x:y:z:w", null));

        Assert.False(await _repo.DismissAsync(Player(id), "a:b:c:d", null));
        Assert.False(await _repo.RestoreAsync(Player(id), "x:y:z:w"));
        Assert.Empty(await _repo.ListAsync(Player(id)));
        Assert.False(await _repo.DismissAsync(new CampaignAccess(id, "stranger", CampaignRole.DM), "a:b:c:d", null));

        Assert.Equal(new[] { "x:y:z:w" }, (await _repo.ListAsync(Dm(id))).Select(d => d.IssueId));
    }

    [Fact]
    public async Task AnEmptyOrOverlongId_IsRefused_AndOneCampaignsDismissalsAreNotAnothers()
    {
        var a = await CampaignAsync();
        var b = await CampaignAsync();
        Assert.False(await _repo.DismissAsync(Dm(a), "  ", null));
        Assert.False(await _repo.DismissAsync(Dm(a), new string('x', RavenConsistencyDismissalRepository.MaxIdLength + 1), null));

        Assert.True(await _repo.DismissAsync(Dm(a), "same:id:in:both", null));
        Assert.Empty(await _repo.ListAsync(Dm(b)));
    }

    [Fact]
    public async Task DeletingTheCampaign_DeletesItsDismissals()
    {
        var id = await CampaignAsync();
        await _repo.DismissAsync(Dm(id), "x:y:z:w", null);

        var actors = new ActorRepository(_db);
        var campaigns = new CampaignRepository(_db, new NoOpSystemCatalog(), actors, new RavenSessionRepository(_db), new RavenNoteRepository(_db),
            new RegionRepository(_db), new FactRepository(_db), new RavenHomebrewRepository(_db), new RavenEncounterRepository(_db), consistencyDismissalRepository: _repo);
        Assert.Equal(CampaignDeleteResult.Deleted, await campaigns.DeleteAsync(id, "gm"));

        using var session = _db.Store.OpenAsyncSession();
        Assert.Null(await session.LoadAsync<ConsistencyDismissalsDocument>(ConsistencyDismissalsDocument.IdFor(id)));
    }
}
