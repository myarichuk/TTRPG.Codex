using System.Text.Json;
using Codex.Persistence;

namespace Codex.Tests.Consistency;

/// <summary>
/// The DM's dismissals travel with the campaign (export, restore, import as a copy). They are the only consistency state there is: results are derived
/// each time and never stored. An older bundle with none restores as it always did; an id whose problem is gone is kept, as in the app.
/// </summary>
[Collection("RavenDb")]
public class ConsistencyExportTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService db;
    private readonly RavenConsistencyDismissalRepository dismissals;
    private readonly ActorRepository actors;
    private readonly CampaignRepository campaigns;
    private readonly CampaignExportService export;

    public ConsistencyExportTests(RavenDbFixture fixture)
    {
        db = fixture.Open("ConsistencyExport_" + Guid.NewGuid());
        dismissals = new RavenConsistencyDismissalRepository(db);
        actors = new ActorRepository(db);
        var sessions = new RavenSessionRepository(db);
        var notes = new RavenNoteRepository(db);
        var regions = new RegionRepository(db);
        var facts = new FactRepository(db);
        var encounters = new RavenEncounterRepository(db);
        var homebrew = new RavenHomebrewRepository(db);
        var wiki = new RavenWikiRepository(db);
        campaigns = new CampaignRepository(db, new NoOpSystemCatalog(), actors, sessions, notes, regions, facts, homebrew, encounters, wiki, consistencyDismissalRepository: dismissals);
        export = new CampaignExportService(campaigns, actors, sessions, encounters, facts, notes, regions, homebrew, wiki, dismissals: dismissals);
    }

    public void Dispose() => db.Dispose();

    private async Task<(string Id, CampaignAccess Dm)> CampaignAsync()
    {
        var id = "camp-dismissals-" + Guid.NewGuid();
        await campaigns.SaveAsync(new CampaignDocument { Id = id, OwnerId = "owner", Name = "Dismissals", SystemId = "srd5e" });
        return (id, new CampaignAccess(id, "owner", CampaignRole.DM));
    }

    private async Task<string[]> IdsAsync(CampaignAccess dm) => (await dismissals.ListAsync(dm)).Select(d => d.IssueId).ToArray();

    [Fact]
    public async Task TheDismissals_AreInTheExport_WithTheirNotes_AndComeBackWhenTheCampaignIsRestored()
    {
        var (id, dm) = await CampaignAsync();
        Assert.True(await dismissals.DismissAsync(dm, "broken-link:page:wikipages/1-A:body:the-mere", "red on purpose"));
        Assert.True(await dismissals.DismissAsync(dm, "empty-bio:actor:actors/9-Z:biography", null));

        var bundle = (await export.ExportAsync(dm))!;
        Assert.Equal(new[] { "broken-link:page:wikipages/1-A:body:the-mere", "empty-bio:actor:actors/9-Z:biography" }, bundle.Dismissals!.Dismissed.Select(d => d.IssueId));
        Assert.Equal("red on purpose", bundle.Dismissals.Dismissed[0].Note);

        // Through the file, as a person would carry it, then lose them and restore.
        var file = CampaignExportService.Deserialize(CampaignExportService.Serialize(bundle))!;
        await dismissals.DeleteAllForCampaignAsync(id);
        Assert.Empty(await IdsAsync(dm));
        Assert.True(await export.ImportAsync(file, "owner"));

        var back = await dismissals.ListAsync(dm);
        Assert.Equal(new[] { "broken-link:page:wikipages/1-A:body:the-mere", "empty-bio:actor:actors/9-Z:biography" }, back.Select(d => d.IssueId));
        Assert.Equal(("red on purpose", "owner"), (back[0].Note, back[0].DismissedBy));
        // The one whose page is long gone was kept, not pruned: it applies again if the problem comes back.
        Assert.Contains("empty-bio:actor:actors/9-Z:biography", back.Select(d => d.IssueId));
    }

    [Fact]
    public async Task ACampaignWithNoDismissals_ExportsNone_AndAnOlderBundle_RestoresAsItAlwaysDid_LeavingStoredOnesAlone()
    {
        var (id, dm) = await CampaignAsync();
        Assert.Null((await export.ExportAsync(dm))!.Dismissals);

        // A bundle from before there were dismissals: no such property in the file at all.
        var json = CampaignExportService.Serialize((await export.ExportAsync(dm))!);
        var old = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        old.Remove(nameof(CampaignBundle.Dismissals));
        var file = CampaignExportService.Deserialize(JsonSerializer.Serialize(old))!;
        Assert.Null(file.Dismissals);

        Assert.True(await dismissals.DismissAsync(dm, "x:y:z:w", "kept"));
        Assert.True(await export.ImportAsync(file, "owner"));

        Assert.Equal(new[] { "x:y:z:w" }, await IdsAsync(dm));
        Assert.Equal("srd5e", (await campaigns.GetAsync(id))!.SystemId);
    }

    [Fact]
    public async Task ImportedAsACopy_TheDismissalsFollowTheNewIdsOfTheDocumentsTheyNameAndTheOriginalIsUntouched()
    {
        var (id, dm) = await CampaignAsync();
        var actorId = Guid.NewGuid().ToString();
        await actors.SaveAsync(new ActorDocument { Id = actorId, CampaignId = id, Name = "Vex" });
        Assert.True(await dismissals.DismissAsync(dm, $"empty-bio:actor:{actorId}:biography", "on purpose"));
        Assert.True(await dismissals.DismissAsync(dm, "broken-link:page:wikipages/1-A:body:the-mere", null)); // a page the campaign never had: kept as it is

        var file = CampaignExportService.Deserialize(CampaignExportService.Serialize((await export.ExportAsync(dm))!))!;
        var newId = await export.ImportAsNewAsync(file, "owner");

        Assert.NotNull(newId);
        var newDm = new CampaignAccess(newId!, "owner", CampaignRole.DM);
        var copy = await dismissals.ListAsync(newDm);
        var newActor = Assert.Single(await actors.GetVisibleForCampaignAsync(newDm));
        Assert.NotEqual(actorId, newActor.Id);
        Assert.Equal(new[] { $"empty-bio:actor:{newActor.Id}:biography", "broken-link:page:wikipages/1-A:body:the-mere" }, copy.Select(d => d.IssueId));
        Assert.Equal("on purpose", copy[0].Note);
        Assert.Equal(new[] { $"empty-bio:actor:{actorId}:biography", "broken-link:page:wikipages/1-A:body:the-mere" }, await IdsAsync(dm));
    }

    [Fact]
    public async Task WhatAFileHolds_IsTidiedLikeWhatTheAppWrites_AndAnotherCampaignsDismissalsAreNotTaken()
    {
        var (id, dm) = await CampaignAsync();
        var bundle = (await export.ExportAsync(dm))!;
        bundle.Dismissals = new ConsistencyDismissalsDocument
        {
            Id = "ConsistencyDismissals/whatever", CampaignId = id,
            Dismissed =
            {
                new IssueDismissal { IssueId = "  a:b:c:d  ", DismissedBy = "x", Note = new string('n', 900) },
                new IssueDismissal { IssueId = "a:b:c:d", DismissedBy = "y", Note = "second" },
                new IssueDismissal { IssueId = "  ", Note = "empty" },
                new IssueDismissal { IssueId = new string('i', RavenConsistencyDismissalRepository.MaxIdLength + 1) }
            }
        };

        Assert.True(await export.ImportAsync(bundle, "owner"));

        var list = await dismissals.ListAsync(dm);
        var only = Assert.Single(list);
        Assert.Equal(("a:b:c:d", "x"), (only.IssueId, only.DismissedBy));
        Assert.Equal(RavenConsistencyDismissalRepository.MaxNoteLength, only.Note!.Length);
        using (var session = db.Store.OpenAsyncSession())
        {
            Assert.NotNull(await session.LoadAsync<ConsistencyDismissalsDocument>(ConsistencyDismissalsDocument.IdFor(id)));
        }

        // A document that names another campaign is not this campaign's: nothing of it is written.
        var (other, otherDm) = await CampaignAsync();
        var foreign = (await export.ExportAsync(otherDm))!;
        foreign.Dismissals = new ConsistencyDismissalsDocument { CampaignId = id, Dismissed = { new IssueDismissal { IssueId = "stolen:id:x:y" } } };
        Assert.True(await export.ImportAsync(foreign, "owner"));
        Assert.Empty(await dismissals.ListAsync(otherDm));
        Assert.Equal(new[] { "a:b:c:d" }, await IdsAsync(dm));
        Assert.NotEqual(id, other);
    }

    [Fact]
    public async Task OnlyTheDm_CanExportThem()
    {
        var (id, dm) = await CampaignAsync();
        await dismissals.DismissAsync(dm, "x:y:z:w", "secret reason");
        Assert.Null(await export.ExportAsync(new CampaignAccess(id, "p1", CampaignRole.Player)));
    }
}
