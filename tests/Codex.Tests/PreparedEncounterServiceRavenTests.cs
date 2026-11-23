using Codex.Persistence;

namespace Codex.Tests;

/// <summary>The service over the real repository: the id a new plan is given reaches the editor, and a plan is found by the URL's argument whether it is stored under
/// <c>preparedencounters/{guid}</c> or, as an imported copy of a campaign has it, under a bare GUID. The repository looks an id up in the plans' collection only, which is what makes
/// trying the bare id safe.</summary>
[Collection("RavenDb")]
public class PreparedEncounterServiceRavenTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _db;
    private readonly RavenPreparedEncounterRepository _plans;
    private readonly EncounterWorld _w;
    private readonly CampaignAccess _gm;
    private readonly CampaignAccess _player;

    public PreparedEncounterServiceRavenTests(RavenDbFixture fixture)
    {
        _db = fixture.Open("EncounterService_" + Guid.NewGuid());
        _plans = new RavenPreparedEncounterRepository(_db);
        _w = new EncounterWorld(_plans);
        _gm = TestAccess.Member(_db, EncounterWorld.CampaignId, "gm", CampaignRole.DM);
        _player = TestAccess.Member(_db, EncounterWorld.CampaignId, "p1", CampaignRole.Player);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ANewPlan_GetsItsIdFromTheRepository_AndIsFoundByTheGuidPartOfItAlone()
    {
        var plan = _w.Plan("Ambush", "cellar", EncounterWorld.Bandits(2));

        var saved = await _w.Service.SaveAsync(_w.Campaign, _gm, plan);

        Assert.True(saved.Saved);
        Assert.StartsWith("preparedencounters/", saved.Id);
        var route = PreparedEncounterServiceRoute(saved.Id!);
        Assert.False(route.Contains('/'));
        var view = await _w.Service.ViewAsync(_w.Campaign, _gm, route);
        Assert.Equal("Ambush", view!.Plan.Name);
        Assert.Equal(2, Assert.Single(view.Plan.Lines).Count);
        Assert.Equal(new[] { "Spiked pit", "Rolling stone" }, view.Hazards.Select(h => h.Name));
    }

    [Fact]
    public async Task ACopiedCampaignsPlan_UnderABareGuid_IsFoundAndDeletedByThatGuid()
    {
        var bare = Guid.NewGuid().ToString();
        await _plans.RestoreAsync(new PreparedEncounterDocument { Id = bare, CampaignId = EncounterWorld.CampaignId, Name = "Copied", Status = PreparedStatus.Ready });

        var rows = await _w.Service.ListAsync(_w.Campaign, _gm);
        var view = await _w.Service.ViewAsync(_w.Campaign, _gm, bare);

        Assert.Equal(bare, Assert.Single(rows).Route);
        Assert.Equal("Copied", view!.Plan.Name);
        // The edit saves under the same bare id, not a new one.
        view.Plan.Name = "Copied and edited";
        Assert.True((await _w.Service.SaveAsync(_w.Campaign, _gm, view.Plan)).Saved);
        Assert.Equal(bare, Assert.Single(await _w.Service.ListAsync(_w.Campaign, _gm)).Plan.Id);
        Assert.True(await _w.Service.DeleteAsync(_w.Campaign, _gm, bare));
        Assert.Null(await _w.Service.ViewAsync(_w.Campaign, _gm, bare));
    }

    [Fact]
    public async Task AnArgumentThatNamesAnotherKindOfDocument_FindsNoPlan()
    {
        // The campaign's own document id: the lookup is in the plans' collection only.
        Assert.Null(await _w.Service.ViewAsync(_w.Campaign, _gm, EncounterWorld.CampaignId));
        Assert.Null(await _w.Service.ViewAsync(_w.Campaign, _gm, "../" + EncounterWorld.CampaignId));
    }

    [Fact]
    public async Task APlayer_ReadsNothing_AndSavesNothing_ThroughTheRealRepository()
    {
        var saved = await _w.Service.SaveAsync(_w.Campaign, _gm, _w.Plan("Ambush"));
        var route = PreparedEncounterServiceRoute(saved.Id!);

        Assert.Empty(await _w.Service.ListAsync(_w.Campaign, _player));
        Assert.Null(await _w.Service.ViewAsync(_w.Campaign, _player, route));
        Assert.False((await _w.Service.SaveAsync(_w.Campaign, _player, _w.Plan("Sneaky"))).Saved);
        Assert.False(await _w.Service.DeleteAsync(_w.Campaign, _player, route));
        Assert.Single(await _w.Service.ListAsync(_w.Campaign, _gm));
    }

    [Fact]
    public async Task AGmOfAnotherCampaign_GivenThisCampaignsPlanGuid_BareOrPrefixed_GetsNothing_AndDeletesNothing()
    {
        var saved = await _w.Service.SaveAsync(_w.Campaign, _gm, _w.Plan("Ambush"));
        var bare = Guid.NewGuid().ToString();
        await _plans.RestoreAsync(new PreparedEncounterDocument { Id = bare, CampaignId = EncounterWorld.CampaignId, Name = "Copied" });
        var otherCampaign = new CampaignDocument { Id = "camp-b", SystemId = "demo", Name = "Other" };
        var otherGm = TestAccess.Member(_db, "camp-b", "gm-b", CampaignRole.DM);
        var guid = PreparedEncounterServiceRoute(saved.Id!);

        foreach (var arg in new[] { guid, saved.Id!, bare })
        {
            Assert.Null(await _w.Service.ViewAsync(otherCampaign, otherGm, arg));
            Assert.False(await _w.Service.DeleteAsync(otherCampaign, otherGm, arg));
        }

        Assert.Empty(await _w.Service.ListAsync(otherCampaign, otherGm));
        Assert.Equal(2, (await _w.Service.ListAsync(_w.Campaign, _gm)).Count);
    }

    [Fact]
    public async Task ADemotedCoGm_WithAStaleAccess_ReadsAndWritesNothing()
    {
        var saved = await _w.Service.SaveAsync(_w.Campaign, _gm, _w.Plan("Ambush"));
        var route = PreparedEncounterServiceRoute(saved.Id!);
        var coGm = TestAccess.Member(_db, EncounterWorld.CampaignId, "co-gm", CampaignRole.DM);
        Assert.NotNull(await _w.Service.ViewAsync(_w.Campaign, coGm, route));
        using (var session = _db.Store.OpenSession())
        {
            session.Load<CampaignDocument>(EncounterWorld.CampaignId).Members.First(m => m.UserId == "co-gm").Role = CampaignRole.Player;
            session.SaveChanges();
        }

        Assert.Empty(await _w.Service.ListAsync(_w.Campaign, coGm));
        Assert.Null(await _w.Service.ViewAsync(_w.Campaign, coGm, route));
        Assert.False((await _w.Service.SaveAsync(_w.Campaign, coGm, _w.Plan("Sneaky"))).Saved);
        Assert.False(await _w.Service.DeleteAsync(_w.Campaign, coGm, route));
        Assert.Single(await _w.Service.ListAsync(_w.Campaign, _gm));
    }

    [Fact]
    public async Task SavingStatusRanThroughTheService_WithNoRuns_StoresReady()
    {
        var plan = _w.Plan("Ambush");
        plan.Status = PreparedStatus.Ran;

        var saved = await _w.Service.SaveAsync(_w.Campaign, _gm, plan);

        Assert.True(saved.Saved);
        Assert.Equal(PreparedStatus.Ready, (await _w.Service.ViewAsync(_w.Campaign, _gm, PreparedEncounterServiceRoute(saved.Id!)))!.Plan.Status);
        Assert.Equal(PreparedStatus.Ready, plan.Status);
    }

    private static string PreparedEncounterServiceRoute(string id) => Codex.Web.Services.PreparedEncounterService.RouteOf(id);
}
