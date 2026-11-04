using System.IO.Compression;
using Codex.Core.Book;
using Codex.Persistence;

namespace Codex.Tests;

/// <summary>
/// A book's pictures go through the same masks as its text (docs/BOOK_EXPORT.md 2.3 and 4). Each picture is a PNG whose width says
/// whose it is: 1xx belongs to something the party may not see, 2xx to something it may. The party edition must hold none of the
/// 1xx and all of the 2xx, and the media store must never even be asked for a picture the audience may not see.
/// </summary>
[Collection("RavenDb")]
public class BookArtTests : IClassFixture<RavenDbFixture>
{
    private const string DmUser = "art_dm_user";
    private const string PlayerUser = "art_player_user";

    private readonly CampaignRepository _campaigns;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenNoteRepository _notes;
    private readonly RegionRepository _regions;
    private readonly FactRepository _facts;
    private readonly RavenEncounterRepository _encounters;
    private readonly RavenHomebrewRepository _homebrew;
    private readonly RavenWikiRepository _wiki;
    private readonly BookSourceReader _reader;
    private readonly RecordingMedia _media;

    public BookArtTests(RavenDbFixture fixture)
    {
        var db = new RavenDbService(fixture.DbPath, fixture.DbName, runInMemory: true);
        _actors = new ActorRepository(db);
        _sessions = new RavenSessionRepository(db);
        _notes = new RavenNoteRepository(db);
        _regions = new RegionRepository(db);
        _facts = new FactRepository(db);
        _encounters = new RavenEncounterRepository(db);
        _homebrew = new RavenHomebrewRepository(db);
        _wiki = new RavenWikiRepository(db);
        _campaigns = new CampaignRepository(db, new NoOpSystemCatalog(), _actors, _sessions, _notes, _regions, _facts, _homebrew, _encounters, _wiki);
        _reader = new BookSourceReader(_campaigns, _actors, _sessions, _encounters, _facts, _notes, _regions, _homebrew, _wiki);
        _media = new RecordingMedia(new RavenMediaStore(db));
    }

    private sealed class RecordingMedia(IMediaStore inner) : IMediaStore
    {
        public List<string> Reads { get; } = [];
        public Task PutAsync(CampaignAccess access, string documentId, string name, Stream content, string contentType) => inner.PutAsync(access, documentId, name, content, contentType);
        public Task<MediaFile?> GetAsync(string documentId, string name) { Reads.Add(documentId + "/" + name); return inner.GetAsync(documentId, name); }
        public Task DeleteAsync(CampaignAccess access, string documentId, string name) => inner.DeleteAsync(access, documentId, name);
    }

    /// <summary>A valid PNG of the given width (and one pixel high).</summary>
    private static byte[] Png(int width)
    {
        var raw = new byte[1 + width * 3];
        using var zipped = new MemoryStream();
        using (var z = new ZLibStream(zipped, CompressionLevel.Fastest, true)) z.Write(raw);
        var o = new MemoryStream();
        o.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            o.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            o.Write(t);
            o.Write(data);
            var crc = Crc(t.Concat(data).ToArray());
            o.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        }

        Chunk("IHDR", [0, 0, (byte)(width >> 8), (byte)width, 0, 0, 0, 1, 8, 2, 0, 0, 0]);
        Chunk("IDAT", zipped.ToArray());
        Chunk("IEND", []);
        return o.ToArray();
    }

    private static uint Crc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return ~crc;
    }

    private async Task PutAsync(CampaignAccess dm, string documentId, string name, int width) =>
        await _media.PutAsync(dm, documentId, name, new MemoryStream(Png(width)), "image/png");

    [Fact]
    public async Task PartyEdition_HoldsNoPictureThePartyMayNotSee_AndNeverReadsOne()
    {
        var (dm, id) = await SeedAsync();
        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        _media.Reads.Clear();

        var art = await new BookArtReader(_media).ReadAsync(source);

        var widths = art.AllImages().Select(i => i.Width).Order().ToList();
        Assert.DoesNotContain(widths, w => w is >= 100 and < 200);
        Assert.Equal(new[] { 201, 202, 203, 204, 205 }, widths);

        Assert.NotNull(art.Cover());
        Assert.NotNull(art.Portrait("a-known"));
        Assert.NotNull(art.Wiki("w-party"));
        Assert.NotNull(art.Location("r-coast", "l-known"));
        Assert.NotNull(art.Homebrew("h-pub"));

        Assert.Null(art.Portrait("a-hidden"));
        Assert.Null(art.Portrait("a-unknown"));
        Assert.Null(art.Portrait("a-emptyowner"));
        Assert.Null(art.Wiki("w-dm"));
        Assert.Null(art.Location("r-coast", "l-hidden"));
        Assert.Null(art.Homebrew("h-unpub"));
        Assert.Null(art.Portrait("a-retired"));

        // Not read and then dropped: never asked for.
        var asked = string.Join(", ", _media.Reads);
        foreach (var secret in new[] { "a-hidden", "a-unknown", "a-emptyowner", "w-dm", "h-unpub", "a-retired", "l-hidden" })
        {
            Assert.DoesNotContain(secret, asked, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("CANARY", Convert.ToHexString(art.AllImages().SelectMany(i => i.Bytes).ToArray()));
        Assert.Contains(id, _media.Reads.First(r => r.EndsWith("/cover", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DmEdition_HasEveryPicture_AsTheDmSeesThem()
    {
        var (dm, _) = await SeedAsync();
        var source = (await _reader.ReadAsync(dm, BookAudience.Dm))!;
        var art = await new BookArtReader(_media).ReadAsync(source);

        Assert.NotNull(art.Portrait("a-hidden"));
        Assert.NotNull(art.Portrait("a-unknown"));
        Assert.NotNull(art.Wiki("w-dm"));
        Assert.NotNull(art.Location("r-coast", "l-hidden"));
        Assert.NotNull(art.Homebrew("h-unpub"));
        Assert.Null(art.Portrait("a-retired")); // retired actors are not in the cast in either edition
    }

    [Fact]
    public async Task AssetIds_AreOpaque_AndAnIdThatIsNotInTheMapIsNothing()
    {
        var (dm, _) = await SeedAsync();
        var art = await new BookArtReader(_media).ReadAsync((await _reader.ReadAsync(dm, BookAudience.Party))!);

        var id = art.Portrait("a-known")!;
        Assert.Matches("^a[0-9]+$", id);
        Assert.StartsWith("data:image/png;base64,", art.DataUri(id));
        // A guessable Raven id, another audience's id, or text a model invented resolves to no picture.
        foreach (var guess in new[] { "a-hidden", "portrait", "a999", "A1", "", "../a1", "a1 ", "w-dm" }) Assert.Null(art.DataUri(guess));
    }

    [Fact]
    public async Task APictureThatIsNotAnImage_IsSkippedWithAReason_AndTheBookStillBuilds()
    {
        var (dm, id) = await SeedAsync();
        await _media.PutAsync(dm, "a-known", RavenMediaStore.ActorPortrait, new MemoryStream(System.Text.Encoding.ASCII.GetBytes("<svg onload=alert(1)>")), "image/png");
        var art = await new BookArtReader(_media).ReadAsync((await _reader.ReadAsync(dm, BookAudience.Party))!);
        Assert.Null(art.Portrait("a-known"));
        Assert.Contains(art.Skipped, s => s.Subject == "portrait:a-known" && s.Reason == "malformed");
        Assert.NotNull(art.Cover());
        _ = id;
    }

    private async Task<(CampaignAccess Dm, string CampaignId)> SeedAsync()
    {
        var id = "art-" + Guid.NewGuid().ToString("N");
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = id, Name = "Ashfall", SystemId = "srd5e", OwnerId = DmUser, HasCover = true, CoverVersion = 1,
            Members = { new CampaignMember { UserId = DmUser, Role = CampaignRole.DM }, new CampaignMember { UserId = PlayerUser, Role = CampaignRole.Player } },
            ActivePacks = { "srd5e" }
        });
        var dm = new CampaignAccess(id, DmUser, CampaignRole.DM);
        await PutAsync(dm, id, RavenMediaStore.CampaignCover, 201);

        async Task Actor(string actorId, ActorVisibility visibility, int width, bool retired = false, string? owner = null)
        {
            await _actors.SaveAsync(new ActorDocument
            {
                Id = actorId, CampaignId = id, Name = actorId, Visibility = visibility, HasPortrait = true, PortraitVersion = 1,
                Retired = retired, RetiredAt = retired ? DateTime.UtcNow : null, OwnerUserId = owner
            });
            await PutAsync(dm, actorId, RavenMediaStore.ActorPortrait, width);
        }

        await Actor("a-known", ActorVisibility.Known, 202);
        await Actor("a-hidden", ActorVisibility.Hidden, 101);
        await Actor("a-unknown", ActorVisibility.Unknown, 102);
        await Actor("a-emptyowner", ActorVisibility.Unknown, 103, owner: string.Empty);
        await Actor("a-retired", ActorVisibility.Known, 104, retired: true);

        await _wiki.SaveAsync(new WikiPageDocument { Id = "w-dm", CampaignId = id, Slug = "dm", Title = "DM", Visibility = WikiVisibility.DmOnly, HasArt = true, ArtVersion = 1 });
        await _wiki.SaveAsync(new WikiPageDocument { Id = "w-party", CampaignId = id, Slug = "docks", Title = "Docks", Visibility = WikiVisibility.Party, HasArt = true, ArtVersion = 1 });
        await PutAsync(dm, "w-dm", RavenMediaStore.EntryArt, 105);
        await PutAsync(dm, "w-party", RavenMediaStore.EntryArt, 203);

        Assert.True(await _regions.SaveAsync(new RegionDocument
        {
            Id = "r-coast", CampaignId = id, Name = "Coast",
            Locations =
            {
                new LocationDocument { Id = "l-known", Name = "Saltmere", Visibility = ActorVisibility.Known, HasArt = true },
                new LocationDocument { Id = "l-hidden", Name = "Hidden", Visibility = ActorVisibility.Hidden, HasArt = true }
            }
        }, dm));
        await PutAsync(dm, "r-coast", RavenMediaStore.LocationArtName("l-known"), 204);
        await PutAsync(dm, "r-coast", RavenMediaStore.LocationArtName("l-hidden"), 106);

        await _homebrew.SaveAsync(new HomebrewEntryDocument { Id = "h-unpub", CampaignId = id, EntryId = "unpub", Type = "monster", Name = "U", IsPublished = false, HasArt = true });
        await _homebrew.SaveAsync(new HomebrewEntryDocument { Id = "h-pub", CampaignId = id, EntryId = "beast", Type = "monster", Name = "B", IsPublished = true, HasArt = true });
        await PutAsync(dm, "h-unpub", RavenMediaStore.EntryArt, 107);
        await PutAsync(dm, "h-pub", RavenMediaStore.EntryArt, 205);

        return (dm, id);
    }
}
