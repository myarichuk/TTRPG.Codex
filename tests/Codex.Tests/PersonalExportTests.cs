using System.IO.Compression;
using System.Text.Json;
using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>Exporting your own data, and bringing a vault back (docs/SERVER_ACCESS_AND_VAULT.md 9, built in 15 and 16).</summary>
public class PersonalExportTests : IClassFixture<RavenDbFixture>
{
    private readonly RavenDbService _db;
    private readonly ActorRepository _actors;
    private readonly RavenVaultRepository _vault;
    private readonly CampaignRepository _campaigns;
    private readonly CampaignExportService _campaignExport;
    private readonly RavenMediaStore _media;
    private readonly RavenBookDraftRepository _drafts;
    private readonly RavenBookArtRepository _bookArt;
    private readonly PersonalExport _export;

    public PersonalExportTests(RavenDbFixture fixture)
    {
        _db = fixture.Open(fixture.DbName);
        _actors = new ActorRepository(_db);
        _vault = new RavenVaultRepository(_db);
        _campaigns = new CampaignRepository(_db, new NoOpSystemCatalog(), _actors, new RavenSessionRepository(_db), new RavenNoteRepository(_db),
            new RegionRepository(_db), new FactRepository(_db), new RavenHomebrewRepository(_db), new RavenEncounterRepository(_db), vaultRepository: _vault);
        _campaignExport = new CampaignExportService(_campaigns, _actors, new RavenSessionRepository(_db), new RavenEncounterRepository(_db), new FactRepository(_db),
            new RavenNoteRepository(_db), new RegionRepository(_db), new RavenHomebrewRepository(_db));
        _media = new RavenMediaStore(_db);
        _drafts = new RavenBookDraftRepository(_db);
        _bookArt = new RavenBookArtRepository(_db);
        var registry = new Codex.Core.ContentRegistry();
        var mentions = new MentionSources(new RegionRepository(_db), new CampaignLibrary(registry, new RavenHomebrewRepository(_db), new NoOpSystemCatalog()),
            new RavenSessionRepository(_db), new RavenNoteRepository(_db), new RavenJournalRepository(_db), new RavenWikiRepository(_db), Codex.Localization.Localizer.For("en"));
        _export = new PersonalExport(_db, _campaigns, _vault, _campaignExport, new PlayedCharacters(_actors, mentions), _drafts, _bookArt);
    }

    private static string User() => "user-" + Guid.NewGuid().ToString("N");

    private async Task<CampaignDocument> CampaignAsync(string owner, string name, params (string User, CampaignRole Role)[] members)
    {
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = owner, Name = name, SystemId = "srd5e" };
        campaign.Members.AddRange(members.Select(m => new CampaignMember { UserId = m.User, Role = m.Role }));
        await _campaigns.SaveAsync(campaign);
        return campaign;
    }

    private async Task StoreAsync(object document)
    {
        using var session = _db.Store.OpenAsyncSession();
        await session.StoreAsync(document);
        await session.SaveChangesAsync();
    }

    private async Task<ZipArchive> ExportAsync(string userId)
    {
        var buffer = new MemoryStream();
        await _export.WriteAsync(userId, "me", buffer);
        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    private static T Read<T>(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)!.Open();
        return JsonSerializer.Deserialize<T>(stream)!;
    }

    [Fact]
    public async Task TheExportHoldsTheVault_OwnJournals_AndTheCampaignsTheCallerRuns_NothingElse_Async()
    {
        var (me, other) = (User(), User());
        var mine = await CampaignAsync(me, "My Table");
        var coRun = await CampaignAsync(other, "Shared Table", (me, CampaignRole.DM));
        var played = await CampaignAsync(other, "Their Table", (me, CampaignRole.Player));
        await _media.PutAsync(new CampaignAccess(mine.Id, me, CampaignRole.DM), mine.Id, RavenMediaStore.CampaignCover, new MemoryStream(new byte[] { 1, 2, 3 }), "image/png");
        await _actors.SaveAsync(new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = played.Id, Name = "Their secret villain", Kind = ActorKind.Monster, DmNotes = "secret" });
        await _vault.CreateAsync(me, "srd5e", "Mira");
        await _vault.CreateAsync(other, "srd5e", "Not mine");
        await StoreAsync(new JournalEntryDocument { CampaignId = played.Id, OwnerUserId = me, Title = "My note" });
        await StoreAsync(new JournalEntryDocument { CampaignId = played.Id, OwnerUserId = other, Title = "Their note" });

        using var zip = await ExportAsync(me);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        Assert.Equal(new[] { "Mira" }, Read<VaultBundle>(zip, "vault.json").Characters.Select(c => c.Name));
        var journals = Read<List<JournalExport>>(zip, "journals.json");
        Assert.Equal("Their Table", Assert.Single(journals).CampaignName);
        Assert.Equal(new[] { "My note" }, journals.Single().Entries.Select(e => e.Title));

        // The campaigns run, by owner or co-GM, each as the campaign export makes it and with its pictures; not one only played in.
        var about = Read<JsonElement>(zip, "about.json");
        Assert.Equal(new[] { "My Table", "Shared Table" }, about.GetProperty("Campaigns").EnumerateArray().Select(c => c.GetString()));
        Assert.Contains("campaigns/My_Table/campaign.json", names);
        Assert.Contains("campaigns/Shared_Table/campaign.json", names);
        Assert.Contains(names, n => n.StartsWith("campaigns/My_Table/images/", StringComparison.Ordinal) && n.EndsWith("cover.png", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Their_Table", StringComparison.Ordinal));
        Assert.DoesNotContain(zip.Entries, e => new StreamReader(e.Open()).ReadToEnd().Contains("Their secret villain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACampaignExport_GoesByTheRoleAsStoredNow_Async()
    {
        var (owner, demoted) = (User(), User());
        var campaign = await CampaignAsync(owner, "Table", (demoted, CampaignRole.DM));
        var loadedAsGm = new CampaignAccess(campaign.Id, demoted, CampaignRole.DM);
        Assert.NotNull(await _campaignExport.ExportAsync(loadedAsGm));

        await _campaigns.UpdateAsync(campaign.Id, c => c.Members.Single(m => m.UserId == demoted).Role = CampaignRole.Player);
        Assert.Null(await _campaignExport.ExportAsync(loadedAsGm));
        Assert.NotNull(await _campaignExport.ExportAsync(new CampaignAccess(campaign.Id, owner, CampaignRole.DM)));
    }

    [Fact]
    public async Task AVaultComesBack_AsNewCharactersOfTheImporter_FromTheZipOrItsVaultFile_Async()
    {
        var (me, next) = (User(), User());
        await _vault.CreateAsync(me, "srd5e", "Mira", a =>
        {
            a.Sheet["level"] = 3L;
            a.Sheet["species"] = "elf";
            a.Biography = "Sailed with the Red Gull.";
        });
        await _vault.CreateAsync(me, "othersys", "Elsewhere");

        using var zip = await ExportAsync(me);
        var file = new MemoryStream();
        await using (var vaultFile = zip.GetEntry("vault.json")!.Open()) await vaultFile.CopyToAsync(file);
        file.Position = 0;

        Assert.Equal((1, 1), await _export.ImportVaultAsync(next, file, id => id == "srd5e"));
        var brought = Assert.Single(await _vault.GetAllAsync(next));
        Assert.Equal(("Mira", next, "Sailed with the Red Gull."), (brought.Name, brought.OwnerUserId, brought.Biography));
        Assert.Equal(3L, brought.Sheet["level"]);
        Assert.Equal("elf", brought.Sheet["species"]);
        Assert.DoesNotContain(await _vault.GetAllAsync(me), c => c.Id == brought.Id);

        // The whole zip works the same; anything else is not a vault.
        var whole = new MemoryStream();
        await _export.WriteAsync(me, "me", whole);
        whole.Position = 0;
        Assert.Equal((1, 1), await _export.ImportVaultAsync(next, whole, id => id == "srd5e"));
        Assert.Null(await _export.ImportVaultAsync(next, new MemoryStream("{\"Format\":\"codex-campaign/1\"}"u8.ToArray()), _ => true));
        Assert.Null(await _export.ImportVaultAsync(next, new MemoryStream("not json"u8.ToArray()), _ => true));
    }

    [Fact]
    public async Task TheCharactersPlayedElsewhere_ComeAsTheirPlayerReadsThem_AndImportOnTheirOwn_Async()
    {
        var (me, gm, other) = (User(), User(), User());
        var table = await CampaignAsync(gm, "Their Table", (me, CampaignRole.Player), (other, CampaignRole.Player));
        var mine = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = table.Id, OwnerUserId = me, Kind = ActorKind.PlayerCharacter, Name = "Brann",
            DmNotes = "Brann is the lost heir.", Biography = "Grew up on the docks.\n:::dm\nHis father lives.\n:::",
            Sheet = new Dictionary<string, object> { ["level"] = 4L, ["notes"] = "Owes Tam.\n:::dm\nTam is a spy.\n:::" }
        };
        await _actors.SaveAsync(mine);
        await _actors.SaveAsync(new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = table.Id, OwnerUserId = other, Kind = ActorKind.PlayerCharacter, Name = "Not mine" });
        await _media.PutAsync(new CampaignAccess(table.Id, me, CampaignRole.Player), mine.Id, RavenMediaStore.ActorPortrait, new MemoryStream(new byte[] { 7, 7 }), "image/png");

        using var zip = await ExportAsync(me);
        var played = Read<VaultBundle>(zip, "played.json");
        var brann = Assert.Single(played.Characters);
        Assert.Equal(("Brann", "srd5e", me), (brann.Name, brann.SystemId, brann.OwnerUserId));
        Assert.Null(brann.DmNotes);
        Assert.Equal("Grew up on the docks.", brann.Biography!.Trim());
        Assert.Equal("Owes Tam.", brann.Sheet["notes"].ToString()!.Trim());
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("played/images/", StringComparison.Ordinal) && e.FullName.EndsWith("portrait.png", StringComparison.Ordinal));
        Assert.DoesNotContain(zip.Entries, e =>
        {
            var text = new StreamReader(e.Open()).ReadToEnd();
            return text.Contains("lost heir", StringComparison.Ordinal) || text.Contains("father lives", StringComparison.Ordinal) || text.Contains("is a spy", StringComparison.Ordinal);
        });
        Assert.Equal("Brann", Read<System.Text.Json.JsonElement>(zip, "about.json").GetProperty("Played")[0].GetProperty("Character").GetString());

        // played.json imports on its own; the whole zip brings only vault.json, so nothing comes twice.
        using var file = new MemoryStream();
        await using (var entry = zip.GetEntry("played.json")!.Open()) await entry.CopyToAsync(file);
        file.Position = 0;
        Assert.Equal((1, 0), await _export.ImportVaultAsync(me, file, id => id == "srd5e"));
        Assert.Equal("Grew up on the docks.", Assert.Single(await _vault.GetAllAsync(me)).Biography!.Trim());
    }

    [Fact]
    public async Task ACampaignRun_ComesWithItsBook_DraftsAndPictures_Async()
    {
        var me = User();
        var campaign = await CampaignAsync(me, "Book Table");
        var dm = new CampaignAccess(campaign.Id, me, CampaignRole.DM);
        Assert.NotNull(await _drafts.CreateAsync(dm, BookDraftDocument.Dm, "en", [new BookDraftGenerated("intro", "Intro", "# The Gull", "k1")]));
        Assert.True(await _bookArt.PutAsync(dm, "s" + new string('b', 24), BookArtDocument.Dm, "Map", new BookImage(new byte[] { 1, 2, 3 }, "image/png", 10, 10)));

        using var zip = await ExportAsync(me);
        var book = Read<System.Text.Json.JsonElement>(zip, "campaigns/Book_Table/book.json");
        var draft = Assert.Single(book.GetProperty("Drafts").EnumerateArray());
        Assert.Equal("dm", draft.GetProperty("Document").GetProperty("Edition").GetString());
        Assert.Equal("# The Gull", draft.GetProperty("Chapters")[0].GetProperty("Markdown").GetString());
        Assert.Single(book.GetProperty("Art").GetProperty("Pictures").EnumerateArray());
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("campaigns/Book_Table/book-images/", StringComparison.Ordinal));

        // A campaign with no book has no book file.
        await CampaignAsync(me, "Bare Table");
        using var again = await ExportAsync(me);
        Assert.Null(again.GetEntry("campaigns/Bare_Table/book.json"));
    }
}
