using System.Text.Json;
using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Book;

namespace Codex.Tests;

/// <summary>Encounter builder B1 (ENCOUNTER_BUILDER_DESIGN.md 3 and 9): the prepared encounter's storage, its GM-only access, and that it travels with the
/// campaign but never reaches the book or the table's list of fights.</summary>
[Collection("RavenDb")]
public class PreparedEncounterRepositoryTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private const string Canary = "CANARY_ambush_in_the_cellar";

    private readonly RavenDbService _db;
    private readonly RavenPreparedEncounterRepository _plans;
    private readonly RavenEncounterRepository _encounters;
    private readonly RavenSessionRepository _sessions;
    private readonly ActorRepository _actors;
    private readonly RegionRepository _regions;
    private readonly RavenNoteRepository _notes;
    private readonly FactRepository _facts;
    private readonly RavenHomebrewRepository _homebrew;
    private readonly RavenWikiRepository _wiki;
    private readonly CampaignRepository _campaigns;
    private readonly CampaignExportService _export;

    public PreparedEncounterRepositoryTests(RavenDbFixture fixture)
    {
        _db = fixture.Open("PreparedEncounters_" + Guid.NewGuid());
        _plans = new RavenPreparedEncounterRepository(_db);
        _encounters = new RavenEncounterRepository(_db);
        _sessions = new RavenSessionRepository(_db);
        _actors = new ActorRepository(_db);
        _regions = new RegionRepository(_db);
        _notes = new RavenNoteRepository(_db);
        _facts = new FactRepository(_db);
        _homebrew = new RavenHomebrewRepository(_db);
        _wiki = new RavenWikiRepository(_db);
        _campaigns = new CampaignRepository(_db, new NoOpSystemCatalog(), _actors, _sessions, _notes, _regions, _facts, _homebrew, _encounters, _wiki, preparedEncounterRepository: _plans);
        _export = new CampaignExportService(_campaigns, _actors, _sessions, _encounters, _facts, _notes, _regions, _homebrew, _wiki, preparedEncounters: _plans);
    }

    public void Dispose() => _db.Dispose();

    private sealed record Table(string CampaignId, CampaignAccess Gm, CampaignAccess CoGm, CampaignAccess Player);

    private Table NewTable()
    {
        var id = "camp-plans-" + Guid.NewGuid();
        var gm = TestAccess.Member(_db, id, "gm", CampaignRole.DM);
        return new Table(id, gm, TestAccess.Member(_db, id, "cogm", CampaignRole.DM), TestAccess.Member(_db, id, "player", CampaignRole.Player));
    }

    private static PreparedEncounterDocument Plan(string campaignId, string name = "Ambush") => new()
    {
        CampaignId = campaignId,
        Name = name,
        RegionId = "region-1",
        PlaceId = "place-1",
        Notes = "Tactics: the door sticks.",
        Tags = ["ambush", "cellar"],
        Status = PreparedStatus.Ready,
        UpdatedAt = DateTime.UtcNow,
        Lines =
        [
            new PreparedLine { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "bandit", Name = "Bandit", Count = 3, Note = "waits behind the door", StartsHidden = true },
            new PreparedLine { Kind = PreparedLineKind.CastMember, ActorId = "actor-1", Name = "Captain Vell" },
            new PreparedLine { Kind = PreparedLineKind.Hazard, EntryType = "hazard", EntryId = "falling-net", IsHomebrew = true, Name = "Falling net" }
        ]
    };

    private async Task<PreparedEncounterDocument> SavedAsync(Table t, string name = "Ambush")
    {
        var plan = Plan(t.CampaignId, name);
        Assert.True(await _plans.SaveAsync(plan, t.Gm));
        return plan;
    }

    [Fact]
    public async Task APlanWithAllThreeLineKinds_RoundTrips_WithItsPrefixedId_AndItsCreator()
    {
        var t = NewTable();
        var plan = await SavedAsync(t);

        Assert.StartsWith("preparedencounters/", plan.Id);
        Assert.Equal("gm", plan.CreatedBy);
        var back = (await _plans.GetAsync(plan.Id, t.Gm))!;
        Assert.Equal(("Ambush", "region-1", "place-1", PreparedStatus.Ready), (back.Name, back.RegionId, back.PlaceId, back.Status));
        Assert.Equal(["ambush", "cellar"], back.Tags);
        Assert.Equal("Tactics: the door sticks.", back.Notes);
        Assert.Equal([PreparedLineKind.Creature, PreparedLineKind.CastMember, PreparedLineKind.Hazard], back.Lines.Select(l => l.Kind));
        Assert.Equal((3, true, "waits behind the door", "bandit"), (back.Lines[0].Count, back.Lines[0].StartsHidden, back.Lines[0].Note, back.Lines[0].EntryId));
        Assert.Equal("actor-1", back.Lines[1].ActorId);
        Assert.True(back.Lines[2].IsHomebrew);
        Assert.Single(await _plans.ListAsync(t.Gm));
    }

    [Fact]
    public async Task TheRepositoryStoresCountsAsGiven_TheServiceChecksThem()
    {
        var t = NewTable();
        var plan = Plan(t.CampaignId);
        plan.Lines[0].Count = 99;
        Assert.True(await _plans.SaveAsync(plan, t.Gm));
        Assert.Equal(99, (await _plans.GetAsync(plan.Id, t.Gm))!.Lines[0].Count);
    }

    [Fact]
    public async Task APlayer_IsRefusedOnGet_List_Save_AndDelete()
    {
        var t = NewTable();
        var plan = await SavedAsync(t);

        Assert.Null(await _plans.GetAsync(plan.Id, t.Player));
        Assert.Empty(await _plans.ListAsync(t.Player));
        Assert.False(await _plans.SaveAsync(Plan(t.CampaignId, "Player's"), t.Player));
        var edit = (await _plans.GetAsync(plan.Id, t.Gm))!;
        edit.Name = "Player edit";
        Assert.False(await _plans.SaveAsync(edit, t.Player));
        Assert.False(await _plans.DeleteAsync(plan.Id, t.Player));

        Assert.Equal("Ambush", (await _plans.GetAsync(plan.Id, t.Gm))!.Name);
        Assert.Single(await _plans.ListAsync(t.Gm));
    }

    [Fact]
    public async Task ACoGm_ReadsAndEditsLikeTheGm()
    {
        var t = NewTable();
        var plan = await SavedAsync(t);

        var seen = (await _plans.GetAsync(plan.Id, t.CoGm))!;
        Assert.Single(await _plans.ListAsync(t.CoGm));
        seen.Name = "Edited by the co-GM";
        Assert.True(await _plans.SaveAsync(seen, t.CoGm));
        Assert.Equal(("Edited by the co-GM", "gm"), ((await _plans.GetAsync(plan.Id, t.Gm))!.Name, (await _plans.GetAsync(plan.Id, t.Gm))!.CreatedBy));
        Assert.True(await _plans.DeleteAsync(plan.Id, t.CoGm));
        Assert.Empty(await _plans.ListAsync(t.Gm));
    }

    [Fact]
    public async Task ACoGmDemotedAfterTheirPageResolved_IsRefusedFromThenOn()
    {
        var t = NewTable();
        var plan = await SavedAsync(t);
        Assert.True(await _campaigns.UpdateAsync(t.CampaignId, c => c.Members.Single(m => m.UserId == "cogm").Role = CampaignRole.Player) != null);

        Assert.Null(await _plans.GetAsync(plan.Id, t.CoGm));
        Assert.Empty(await _plans.ListAsync(t.CoGm));
        Assert.False(await _plans.SaveAsync(plan, t.CoGm));
        Assert.False(await _plans.DeleteAsync(plan.Id, t.CoGm));
    }

    [Fact]
    public async Task APartyPreview_IsRefusedEverywhere_AndAMissingSystemReadsButWritesNothing()
    {
        var t = NewTable();
        var plan = await SavedAsync(t);
        var preview = t.Gm.PartyPreview()!;

        Assert.Null(await _plans.GetAsync(plan.Id, preview));
        Assert.Empty(await _plans.ListAsync(preview));
        Assert.False(await _plans.SaveAsync(Plan(t.CampaignId, "Preview"), preview));
        Assert.False(await _plans.DeleteAsync(plan.Id, preview));

        var missing = new CampaignAccess(t.CampaignId, "gm", CampaignRole.DM, systemMissing: true);
        Assert.NotNull(await _plans.GetAsync(plan.Id, missing));
        Assert.Single(await _plans.ListAsync(missing));
        Assert.False(await _plans.SaveAsync(Plan(t.CampaignId, "No system"), missing));
        plan.Name = "No system edit";
        Assert.False(await _plans.SaveAsync(plan, missing));
        Assert.False(await _plans.DeleteAsync(plan.Id, missing));
        Assert.Equal("Ambush", (await _plans.GetAsync(plan.Id, t.Gm))!.Name);
        Assert.Single(await _plans.ListAsync(t.Gm));
    }

    [Fact]
    public async Task AGmOfAnotherCampaign_CannotReadWriteOrDeleteAPlanByItsId()
    {
        var a = NewTable();
        var b = NewTable();
        var planOfA = await SavedAsync(a, "A's plan");
        var planOfB = await SavedAsync(b, "B's plan");

        Assert.Null(await _plans.GetAsync(planOfA.Id, b.Gm));
        Assert.Equal(["B's plan"], (await _plans.ListAsync(b.Gm)).Select(p => p.Name));

        // Saving a copy that names A's id and A's campaign, or A's id and B's campaign, writes nothing of A's.
        var asA = (await _plans.GetAsync(planOfA.Id, a.Gm))!;
        asA.Name = "Taken over";
        Assert.False(await _plans.SaveAsync(asA, b.Gm));
        var claimed = Plan(b.CampaignId, "Claimed");
        claimed.Id = planOfA.Id;
        Assert.False(await _plans.SaveAsync(claimed, b.Gm));
        Assert.False(await _plans.DeleteAsync(planOfA.Id, b.Gm));

        Assert.Equal("A's plan", (await _plans.GetAsync(planOfA.Id, a.Gm))!.Name);
        Assert.Equal("B's plan", (await _plans.GetAsync(planOfB.Id, b.Gm))!.Name);
    }

    [Fact]
    public async Task AnIdThatIsAnotherKindOfDocument_IsNotAPlan()
    {
        var t = NewTable();
        await _actors.SaveAsync(new ActorDocument { Id = "actors/" + Guid.NewGuid(), CampaignId = t.CampaignId, Name = "Villain" });
        var actorId = (await _actors.GetVisibleForCampaignAsync(t.Gm)).Single().Id;

        Assert.Null(await _plans.GetAsync(actorId, t.Gm));
        Assert.False(await _plans.DeleteAsync(actorId, t.Gm));
        Assert.False(await _plans.MarkRunAsync(actorId, "enc-1", t.Gm));
        Assert.Single(await _actors.GetVisibleForCampaignAsync(t.Gm));
    }

    [Fact]
    public async Task MarkRun_AfterAnotherSaveOfThePlan_InSequence_KeepsThatSavesEdits_AndOnlyTouchesTheRunFields()
    {
        var t = NewTable();
        var plan = await SavedAsync(t);

        // The start read the plan; then the GM edited and saved it; then the start finished.
        var edit = (await _plans.GetAsync(plan.Id, t.Gm))!;
        edit.Name = "Renamed meanwhile";
        edit.Notes = "New tactics";
        edit.Lines.RemoveAt(2);
        Assert.True(await _plans.SaveAsync(edit, t.Gm));
        Assert.True(await _plans.MarkRunAsync(plan.Id, "encounters/1", t.Gm));

        var back = (await _plans.GetAsync(plan.Id, t.Gm))!;
        Assert.Equal(("Renamed meanwhile", "New tactics", 2), (back.Name, back.Notes, back.Lines.Count));
        Assert.Equal(PreparedStatus.Ran, back.Status);
        Assert.Equal(["encounters/1"], back.RunEncounterIds);
        Assert.Equal(("region-1", "place-1", "gm"), (back.RegionId, back.PlaceId, back.CreatedBy));
    }

    [Fact]
    public async Task MarkRun_StampsTheTime_ASaveOfAStaleCopyKeepsIt_AndANewPlanStartsWithNone()
    {
        var t = NewTable();
        var plan = await SavedAsync(t);
        var stale = (await _plans.GetAsync(plan.Id, t.Gm))!;
        Assert.Null(stale.LastRunAt);

        var before = DateTime.UtcNow.AddSeconds(-1);
        Assert.True(await _plans.MarkRunAsync(plan.Id, "enc-1", t.Gm));
        var ran = (await _plans.GetAsync(plan.Id, t.Gm))!.LastRunAt;
        Assert.NotNull(ran);
        Assert.True(ran >= before);

        stale.Notes = "edited on a copy read before the run";
        Assert.True(await _plans.SaveAsync(stale, t.Gm));
        Assert.Equal(ran, (await _plans.GetAsync(plan.Id, t.Gm))!.LastRunAt);
        Assert.Equal(ran, stale.LastRunAt);

        var fresh = Plan(t.CampaignId, "Pretends");
        fresh.LastRunAt = DateTime.UtcNow;
        Assert.True(await _plans.SaveAsync(fresh, t.Gm));
        Assert.Null((await _plans.GetAsync(fresh.Id, t.Gm))!.LastRunAt);
    }

    [Fact]
    public async Task MarkRun_AppendsNewestLast_OnceForEachFight()
    {
        var t = NewTable();
        var plan = await SavedAsync(t);

        Assert.True(await _plans.MarkRunAsync(plan.Id, "enc-1", t.Gm));
        Assert.True(await _plans.MarkRunAsync(plan.Id, "enc-2", t.Gm));
        Assert.True(await _plans.MarkRunAsync(plan.Id, "enc-1", t.Gm));

        Assert.Equal(["enc-1", "enc-2"], (await _plans.GetAsync(plan.Id, t.Gm))!.RunEncounterIds);
        Assert.False(await _plans.MarkRunAsync("preparedencounters/none", "enc-3", t.Gm));
    }

    [Fact]
    public async Task MarkRun_RunInParallelWithASave_LosesNeitherWrite()
    {
        var t = NewTable();
        var plan = await SavedAsync(t);
        var tasks = new List<Task>();
        // Six writers at once: every round of retries lets at least one through, and the repository retries eight times.
        for (var i = 0; i < 3; i++)
        {
            var fight = "enc-" + i;
            tasks.Add(_plans.MarkRunAsync(plan.Id, fight, t.Gm));
            var copy = (await _plans.GetAsync(plan.Id, t.Gm))!;
            copy.Notes = "note " + i;
            tasks.Add(_plans.SaveAsync(copy, t.Gm));
        }

        await Task.WhenAll(tasks);
        var back = (await _plans.GetAsync(plan.Id, t.Gm))!;
        Assert.Equal(Enumerable.Range(0, 3).Select(i => "enc-" + i).Order(), back.RunEncounterIds.Order());
        Assert.Equal(PreparedStatus.Ran, back.Status);
        Assert.Contains(back.Notes, new[] { "note 0", "note 1", "note 2" }); // one of the written values, whole
    }

    [Fact]
    public async Task OnlyAMarkedRunMakesAPlanRan_AndAStoredRunKeepsItRan_WhateverTheCopySays()
    {
        var t = NewTable();
        var plan = await SavedAsync(t);

        // No stored runs: a copy saying Ran is saved as Ready, and so is a new plan that says Ran.
        var edit = (await _plans.GetAsync(plan.Id, t.Gm))!;
        edit.Status = PreparedStatus.Ran;
        Assert.True(await _plans.SaveAsync(edit, t.Gm));
        Assert.Equal(PreparedStatus.Ready, edit.Status);
        Assert.Equal(PreparedStatus.Ready, (await _plans.GetAsync(plan.Id, t.Gm))!.Status);
        var fresh = Plan(t.CampaignId);
        fresh.Status = PreparedStatus.Ran;
        fresh.RunEncounterIds = ["forged"];
        Assert.True(await _plans.SaveAsync(fresh, t.Gm));
        Assert.Equal((PreparedStatus.Ready, 0), ((await _plans.GetAsync(fresh.Id, t.Gm))!.Status, (await _plans.GetAsync(fresh.Id, t.Gm))!.RunEncounterIds.Count));

        // A copy loaded before the run, carrying no run list at all (an editor need not carry it), and then a copy that says Draft.
        var stale = (await _plans.GetAsync(plan.Id, t.Gm))!;
        stale.RunEncounterIds = [];
        Assert.True(await _plans.MarkRunAsync(plan.Id, "enc-1", t.Gm));
        stale.Name = "Edited on a stale copy";
        stale.RunEncounterIds = ["forged"]; // whatever the copy carries, the run list is the stored one
        Assert.True(await _plans.SaveAsync(stale, t.Gm));
        var back = (await _plans.GetAsync(plan.Id, t.Gm))!;
        Assert.Equal(("Edited on a stale copy", PreparedStatus.Ran), (back.Name, back.Status));
        Assert.Equal(["enc-1"], back.RunEncounterIds);
        Assert.Equal(["enc-1"], stale.RunEncounterIds); // the caller's copy now holds what was stored

        back.Status = PreparedStatus.Draft;
        Assert.True(await _plans.SaveAsync(back, t.Gm));
        Assert.Equal(PreparedStatus.Ran, (await _plans.GetAsync(plan.Id, t.Gm))!.Status);
    }

    [Fact]
    public async Task EverySave_StampsTheTime_AndANewPlanTakesItsCreatorFromTheAccess()
    {
        var t = NewTable();
        var plan = Plan(t.CampaignId);
        plan.CreatedBy = "someone-else";
        plan.UpdatedAt = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(await _plans.SaveAsync(plan, t.CoGm));
        Assert.Equal("cogm", plan.CreatedBy);
        Assert.True(plan.UpdatedAt > DateTime.UtcNow.AddMinutes(-1));

        var first = (await _plans.GetAsync(plan.Id, t.Gm))!.UpdatedAt;
        await Task.Delay(20);
        Assert.True(await _plans.MarkRunAsync(plan.Id, "enc-1", t.Gm));
        var marked = (await _plans.GetAsync(plan.Id, t.Gm))!.UpdatedAt;
        Assert.True(marked > first);
        await Task.Delay(20);
        var edit = (await _plans.GetAsync(plan.Id, t.Gm))!;
        edit.UpdatedAt = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(await _plans.SaveAsync(edit, t.Gm));
        Assert.True((await _plans.GetAsync(plan.Id, t.Gm))!.UpdatedAt > marked);
    }

    [Fact]
    public async Task MarkRun_IsRefusedForAPlayer_APartyPreview_AMissingSystem_AndAGmOfAnotherCampaign()
    {
        var a = NewTable();
        var b = NewTable();
        var planOfB = await SavedAsync(b, "B's plan");

        Assert.False(await _plans.MarkRunAsync(planOfB.Id, "enc-1", a.Gm));
        Assert.False(await _plans.MarkRunAsync(planOfB.Id, "enc-1", b.Player));
        Assert.False(await _plans.MarkRunAsync(planOfB.Id, "enc-1", b.Gm.PartyPreview()!));
        Assert.False(await _plans.MarkRunAsync(planOfB.Id, "enc-1", new CampaignAccess(b.CampaignId, "gm", CampaignRole.DM, systemMissing: true)));

        var back = (await _plans.GetAsync(planOfB.Id, b.Gm))!;
        Assert.Empty(back.RunEncounterIds);
        Assert.Equal(PreparedStatus.Ready, back.Status);
        Assert.True(await _plans.MarkRunAsync(planOfB.Id, "enc-1", b.CoGm));
    }

    [Fact]
    public async Task AnIdOfAnotherKindOfDocument_WhoseFieldsClashWithAPlans_ReturnsNullNotAnException()
    {
        // A session's Status is a SessionStatus and a world chat's Lines are not PreparedLines: read as a plan they would not deserialize.
        var t = NewTable();
        var session = new SessionDocument { Id = "sessions/" + Guid.NewGuid(), CampaignId = t.CampaignId, Title = "S", Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var chat = new WorldChatDocument { Id = "worldchats/" + Guid.NewGuid(), CampaignId = t.CampaignId, OwnerUserId = "gm" };
        using (var s = _db.Store.OpenAsyncSession())
        {
            await s.StoreAsync(chat, chat.Id);
            await s.SaveChangesAsync();
        }

        foreach (var id in new[] { session.Id, chat.Id })
        {
            Assert.Null(await _plans.GetAsync(id, t.Gm));
            Assert.False(await _plans.DeleteAsync(id, t.Gm));
            Assert.False(await _plans.MarkRunAsync(id, "enc-1", t.Gm));
            var crafted = Plan(t.CampaignId, "Crafted");
            crafted.Id = id;
            Assert.False(await _plans.SaveAsync(crafted, t.Gm));
            await _plans.RestoreAsync(crafted);
        }

        Assert.Equal("S", (await _sessions.GetAsync(session.Id))!.Title);
        using var check = _db.Store.OpenAsyncSession();
        Assert.NotNull(await check.LoadAsync<WorldChatDocument>(chat.Id));
        Assert.Empty(await _plans.ListAsync(t.Gm));
    }

    [Fact]
    public async Task ACraftedBundle_WithAPlanAtASessionsOrAWorldChatsId_ImportsEverythingElse_AndWritesNothingForThatPlan()
    {
        var t = NewTable();
        await _campaigns.SaveAsync(new CampaignDocument { Id = t.CampaignId, OwnerId = "gm", Name = "Table", SystemId = "srd5e" });
        var session = new SessionDocument { Id = "sessions/" + Guid.NewGuid(), CampaignId = t.CampaignId, Title = "Kept" };
        await _sessions.SaveAsync(session);
        var bundle = (await _export.ExportAsync(t.Gm))!;
        var crafted = Plan(t.CampaignId, "Crafted");
        crafted.Id = session.Id;
        bundle.PreparedEncounters.Add(crafted);
        var fine = Plan(t.CampaignId, "Fine");
        fine.Id = PreparedEncounterDocument.IdPrefix + Guid.NewGuid();
        bundle.PreparedEncounters.Add(fine);

        Assert.True(await _export.ImportAsync(bundle, "gm"));

        Assert.Equal("Kept", (await _sessions.GetAsync(session.Id))!.Title);
        Assert.Equal(["Fine"], (await _plans.ListAsync(t.Gm)).Select(p => p.Name));
    }

    [Fact]
    public async Task ACraftedBundle_CannotCreateAPlanAtAnotherCampaignsPerCampaignDocumentId()
    {
        var a = NewTable();
        var b = NewTable();
        await _campaigns.SaveAsync(new CampaignDocument { Id = a.CampaignId, OwnerId = "gm", Name = "A", SystemId = "srd5e" });
        var derived = "ConsistencyDismissals/" + b.CampaignId; // nothing is stored there yet, but B's first dismissal will be
        var crafted = Plan(a.CampaignId, "Crafted");
        crafted.Id = derived;
        var bundle = (await _export.ExportAsync(a.Gm))!;
        bundle.PreparedEncounters.Add(crafted);

        Assert.True(await _export.ImportAsync(bundle, "gm"));

        using var session = _db.Store.OpenAsyncSession();
        Assert.False(await session.Advanced.ExistsAsync(derived));
        Assert.Empty(await _plans.ListAsync(a.Gm));
        // The plan ids a plan may have are still taken: the prefix and a bare GUID.
        foreach (var id in new[] { PreparedEncounterDocument.IdPrefix + Guid.NewGuid(), Guid.NewGuid().ToString() })
        {
            var ok = Plan(a.CampaignId, id);
            ok.Id = id;
            await _plans.RestoreAsync(ok);
        }

        Assert.Equal(2, (await _plans.ListAsync(a.Gm)).Count);
    }

    [Theory]
    [InlineData("preparedencounters/new")]
    [InlineData("preparedencounters/")]
    [InlineData("preparedencounters/not-a-guid")]
    public async Task Restore_RefusesAPrefixedIdWhoseSuffixIsNotAGuid(string id)
    {
        var t = NewTable();
        var crafted = Plan(t.CampaignId, "Crafted");
        crafted.Id = id;

        await _plans.RestoreAsync(crafted);

        using var session = _db.Store.OpenAsyncSession();
        Assert.False(await session.Advanced.ExistsAsync(id));
        Assert.Empty(await _plans.ListAsync(t.Gm));
    }

    [Fact]
    public async Task RestoreOverAStoredPlanOfTheSameCampaign_ReplacesIt_AndOverAnotherCampaignsPlan_DoesNot()
    {
        var a = NewTable();
        var b = NewTable();
        var mine = await SavedAsync(a, "before");
        var theirs = await SavedAsync(b, "theirs");

        var replacement = Plan(a.CampaignId, "after");
        replacement.Id = mine.Id;
        replacement.RunEncounterIds = ["enc-9"];
        replacement.Status = PreparedStatus.Ran;
        await _plans.RestoreAsync(replacement);
        var takeover = Plan(a.CampaignId, "takeover");
        takeover.Id = theirs.Id;
        await _plans.RestoreAsync(takeover);

        var back = (await _plans.GetAsync(mine.Id, a.Gm))!;
        Assert.Equal(("after", PreparedStatus.Ran), (back.Name, back.Status));
        Assert.Equal(["enc-9"], back.RunEncounterIds);
        Assert.Equal("theirs", (await _plans.GetAsync(theirs.Id, b.Gm))!.Name);
        Assert.Single(await _plans.ListAsync(a.Gm));
    }

    [Fact]
    public async Task SavingASetButUnstoredId_IsRefused_AndStoresNothing()
    {
        var t = NewTable();
        var plan = Plan(t.CampaignId);
        plan.Id = PreparedEncounterDocument.IdPrefix + Guid.NewGuid();

        Assert.False(await _plans.SaveAsync(plan, t.Gm));

        Assert.Null(await _plans.GetAsync(plan.Id, t.Gm));
        Assert.Empty(await _plans.ListAsync(t.Gm));
    }

    [Fact]
    public async Task DeletingTheCampaign_RemovesItsPlans_AndNoOthers()
    {
        var a = NewTable();
        var b = NewTable();
        await SavedAsync(a, "one");
        await SavedAsync(a, "two");
        var keep = await SavedAsync(b, "kept");
        var owned = "camp-owned-" + Guid.NewGuid();
        await _campaigns.SaveAsync(new CampaignDocument { Id = owned, OwnerId = "owner", Name = "Doomed", SystemId = "srd5e" });
        var ownerAccess = new CampaignAccess(owned, "owner", CampaignRole.DM);
        Assert.True(await _plans.SaveAsync(Plan(owned), ownerAccess));

        Assert.Equal(CampaignDeleteResult.Deleted, await _campaigns.DeleteAsync(owned, "owner"));

        Assert.Empty(await _plans.GetAllForCampaignAsync(owned));
        Assert.Equal(2, (await _plans.ListAsync(a.Gm)).Count);
        Assert.NotNull(await _plans.GetAsync(keep.Id, b.Gm));
    }

    [Fact]
    public async Task APlan_IsNotAnEncounter_ForTheExportsFightsTheBookOrTheTable()
    {
        var t = NewTable();
        var plan = await SavedAsync(t, Canary);

        Assert.Empty(await _encounters.GetAllForCampaignAsync(t.CampaignId));
        // The table restores the last encounter its session lists, and only those; a plan is in no session's list.
        var session = new SessionDocument { Id = "sess-" + Guid.NewGuid(), CampaignId = t.CampaignId, Title = "S" };
        await _sessions.SaveAsync(session);
        Assert.Empty((await _sessions.GetAsync(session.Id))!.EncounterIds);
        Assert.DoesNotContain(plan.Id, (await _sessions.GetAllForCampaignAsync(t.CampaignId)).SelectMany(s => s.EncounterIds));
    }

    [Fact]
    public async Task TheBookReader_NeverSeesAPlan_InEitherEdition()
    {
        var id = "book-" + Guid.NewGuid().ToString("N");
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = id, Name = "Ashfall", SystemId = "srd5e", OwnerId = "gm", ActivePacks = { "srd5e" },
            Members = { new CampaignMember { UserId = "gm", Role = CampaignRole.DM }, new CampaignMember { UserId = "player", Role = CampaignRole.Player } }
        });
        var gm = new CampaignAccess(id, "gm", CampaignRole.DM);
        var plan = Plan(id, Canary);
        plan.Notes = Canary + "_notes";
        plan.Lines[1].Name = Canary + "_line";
        Assert.True(await _plans.SaveAsync(plan, gm));
        var reader = new BookSourceReader(_campaigns, _actors, _sessions, _encounters, _facts, _notes, _regions, _homebrew, _wiki, new BookTestCatalog());

        foreach (var audience in new[] { BookAudience.Party, BookAudience.Dm })
        {
            var source = await reader.ReadAsync(gm, audience);
            Assert.NotNull(source);
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(source);
            Assert.Contains("Ashfall", json); // not an empty source
            Assert.DoesNotContain("CANARY_", json);
            Assert.DoesNotContain(plan.Id, json);
        }
    }

    [Fact]
    public async Task ExportThenRestore_KeepsThePlans_UnderTheirIds_AndOnlyForAGm()
    {
        var t = NewTable();
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = t.CampaignId, OwnerId = "gm", Name = "Table", SystemId = "srd5e",
            Members = { new CampaignMember { UserId = "gm", Role = CampaignRole.DM }, new CampaignMember { UserId = "player", Role = CampaignRole.Player } }
        });
        var plan = await SavedAsync(t);
        Assert.True(await _plans.MarkRunAsync(plan.Id, "enc-1", t.Gm));

        // The player is still a member (so the refusal is the role's, not a missing membership's); the plans are in the file for the GM only.
        Assert.Equal(CampaignRole.Player, CampaignAccessResolver.RoleOf((await _campaigns.GetAsync(t.CampaignId))!, "player"));
        Assert.Null(await _export.ExportAsync(t.Player));
        var bundle = (await _export.ExportAsync(t.Gm))!;
        Assert.Equal([plan.Id], bundle.PreparedEncounters.Select(p => p.Id));
        var file = CampaignExportService.Deserialize(CampaignExportService.Serialize(bundle))!;

        await _plans.DeleteAllForCampaignAsync(t.CampaignId);
        Assert.Empty(await _plans.ListAsync(t.Gm));
        Assert.True(await _export.ImportAsync(file, "gm"));

        var back = (await _plans.ListAsync(t.Gm)).Single();
        Assert.Equal((plan.Id, "Ambush", PreparedStatus.Ran), (back.Id, back.Name, back.Status));
        Assert.Equal(["enc-1"], back.RunEncounterIds);
        Assert.Equal([PreparedLineKind.Creature, PreparedLineKind.CastMember, PreparedLineKind.Hazard], back.Lines.Select(l => l.Kind));
    }

    [Fact]
    public async Task ImportAsACopy_GivesThePlansNewIds_AndMapsTheirRegionAndActorIds()
    {
        var campaignId = "camp-copy-" + Guid.NewGuid();
        await _campaigns.SaveAsync(new CampaignDocument { Id = campaignId, OwnerId = "gm", Name = "Original", SystemId = "srd5e" });
        var gm = new CampaignAccess(campaignId, "gm", CampaignRole.DM);
        var region = new RegionDocument { Id = "region-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Vale" };
        Assert.True(await _regions.SaveAsync(region, gm));
        var cellar = new LocationDocument { Id = Guid.NewGuid().ToString(), Name = "Cellar" };
        Assert.True(await _regions.UpsertLocationAsync(region.Id, cellar, gm));
        var actor = new ActorDocument { Id = "actor-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Captain Vell", Kind = ActorKind.NonPlayerCharacter };
        await _actors.SaveAsync(actor);

        var plan = Plan(campaignId);
        plan.RegionId = region.Id;
        plan.PlaceId = cellar.Id;
        plan.Lines[1].ActorId = actor.Id;
        plan.FromChatId = "worldchats/" + Guid.NewGuid();
        Assert.True(await _plans.SaveAsync(plan, gm));
        Assert.True(await _plans.MarkRunAsync(plan.Id, "enc-original", gm));

        var newId = await _export.ImportAsNewAsync(CampaignExportService.Deserialize(CampaignExportService.Serialize((await _export.ExportAsync(gm))!)), "gm");

        Assert.NotNull(newId);
        Assert.NotEqual(campaignId, newId);
        var copyAccess = new CampaignAccess(newId, "gm", CampaignRole.DM);
        var copy = (await _plans.ListAsync(copyAccess)).Single();
        var newRegion = (await _regions.GetVisibleForCampaignAsync(copyAccess)).Single();
        var newActor = (await _actors.GetVisibleForCampaignAsync(copyAccess)).Single();
        Assert.NotEqual(plan.Id, copy.Id);
        Assert.Equal(newId, copy.CampaignId);
        Assert.NotEqual(region.Id, copy.RegionId);
        Assert.Equal((newRegion.Id, newActor.Id), (copy.RegionId, copy.Lines[1].ActorId));
        // A place keeps its id inside the copied region, as the live encounter's LocationId does, so the plan still points at it.
        Assert.Contains(newRegion.Locations, l => l.Id == copy.PlaceId);
        // The copy keeps a homebrew line as homebrew (a pack entry with the same id is a different choice), and forgets the chat, which is not in the file.
        Assert.Equal((true, "falling-net"), (copy.Lines[2].IsHomebrew, copy.Lines[2].EntryId));
        Assert.Null(copy.FromChatId);
        // Copied plans have bare-GUID ids, not the prefix.
        Assert.True(Guid.TryParseExact(copy.Id, "D", out _));
        // The original is untouched, and still the original's alone.
        Assert.NotNull((await _plans.GetAsync(plan.Id, gm))!.FromChatId);
        Assert.Equal(plan.Id, (await _plans.ListAsync(gm)).Single().Id);
        Assert.Equal(region.Id, (await _plans.ListAsync(gm)).Single().RegionId);
        Assert.Equal(["enc-original"], (await _plans.GetAsync(plan.Id, gm))!.RunEncounterIds);
    }

    [Fact]
    public async Task ABundleFromBeforePlansExisted_ImportsAndLeavesStoredPlansAlone()
    {
        var t = NewTable();
        await _campaigns.SaveAsync(new CampaignDocument { Id = t.CampaignId, OwnerId = "gm", Name = "Table", SystemId = "srd5e" });
        var plan = await SavedAsync(t);
        var json = CampaignExportService.Serialize((await _export.ExportAsync(t.Gm))!);
        var old = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        old.Remove(nameof(CampaignBundle.PreparedEncounters));

        Assert.True(await _export.ImportAsync(CampaignExportService.Deserialize(JsonSerializer.Serialize(old)), "gm"));
        Assert.Equal([plan.Id], (await _plans.ListAsync(t.Gm)).Select(p => p.Id));
    }

    [Fact]
    public async Task ACraftedBundle_CannotWriteAPlanOverAnotherCampaignsDocument()
    {
        var a = NewTable();
        var b = NewTable();
        await _campaigns.SaveAsync(new CampaignDocument { Id = a.CampaignId, OwnerId = "gm", Name = "A", SystemId = "srd5e" });
        var victim = await SavedAsync(b, "B's plan");
        var otherActorId = "actors/" + Guid.NewGuid();
        await _actors.SaveAsync(new ActorDocument { Id = otherActorId, CampaignId = b.CampaignId, Name = "B's villain" });

        var bundle = (await _export.ExportAsync(a.Gm))!;
        foreach (var id in new[] { victim.Id, otherActorId })
        {
            var crafted = Plan(a.CampaignId, "Crafted");
            crafted.Id = id;
            bundle.PreparedEncounters.Add(crafted);
        }

        Assert.True(await _export.ImportAsync(bundle, "gm"));

        Assert.Equal("B's plan", (await _plans.GetAsync(victim.Id, b.Gm))!.Name);
        Assert.Equal("B's villain", (await _actors.GetVisibleForCampaignAsync(b.Gm)).Single().Name);
        Assert.Empty(await _plans.ListAsync(a.Gm));
    }
}
