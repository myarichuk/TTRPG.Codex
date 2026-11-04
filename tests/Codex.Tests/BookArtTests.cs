using System.IO.Compression;
using Codex.Core.Book;
using Codex.Web.Book;
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

    [Fact]
    public async Task WithTheScaler_ABigPictureIsSmallerInTheBook_AndTheMasksAreUnchanged()
    {
        var (dm, _) = await SeedAsync();
        var random = new Random(3);
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(2400, 3000, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        for (var i = 0; i < pixels.Length; i += 4) { var p = i / 4; pixels[i] = (byte)(p % 2400 / 10 + random.Next(16)); pixels[i + 1] = (byte)(p / 2400 / 12 + random.Next(16)); pixels[i + 2] = (byte)random.Next(40); pixels[i + 3] = 255; }
        var big = SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 92).ToArray();
        Assert.InRange(big.Length, 100_000, (int)RavenMediaStore.MaxBytes);
        await _media.PutAsync(dm, "a-known", RavenMediaStore.ActorPortrait, new MemoryStream(big), "image/jpeg");
        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;

        var plain = await new BookArtReader(_media).ReadAsync(source);
        var scaled = await new BookArtReader(_media, new BookImageScaler()).ReadAsync(source, BookArtQuality.Print);
        var preview = await new BookArtReader(_media, new BookImageScaler()).ReadAsync(source, BookArtQuality.Preview);

        var bytes = (BookArt a) => a.AllImages().Sum(i => (long)i.Bytes.Length);
        Assert.True(bytes(scaled) < bytes(plain), $"{bytes(scaled)} vs {bytes(plain)}");
        Assert.True(bytes(preview) < bytes(scaled));
        Assert.True(scaled.Image(scaled.Portrait("a-known")!)!.Width < 2400);
        // Same pictures, same masks: only their size changed.
        Assert.Equal(plain.Count, scaled.Count);
        Assert.Null(scaled.Portrait("a-hidden"));
        Assert.Null(scaled.Wiki("w-dm"));
    }

    private static (string Html, int Placed) Page(BookSource source, BookArt art, bool pictures = true)
    {
        var document = BookComposer.Compose(source, "en");
        var chosen = pictures ? art : BookArt.None;
        document = BookComposer.PlaceArt(document, source, chosen, out var placed);
        var html = BookHtml.Build(document, BookThemes.Plain, new BookHtmlOptions { BoxSecrets = source.Audience == BookAudience.Dm, Mention = BookCompiler.Mentions(source), Image = chosen.DataUri }).Html!;
        return (html, placed);
    }

    private static int Count(string html, string needle) => System.Text.RegularExpressions.Regex.Matches(html, needle).Count;

    [Fact]
    public async Task PartyBook_PlacesOnlyThePicturesThePartyMaySee_AndTheCoverGoesOnTheTitlePage()
    {
        var (dm, _) = await SeedAsync();
        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        var art = await new BookArtReader(_media).ReadAsync(source);

        var (html, placed) = Page(source, art);

        // Known actor, the party page, the known place and the published entry, each after its own heading; plus the cover.
        Assert.Equal(5, placed);
        Assert.Equal(4, Count(html, "<img class=\"art\""));
        Assert.Equal(1, Count(html, "<img class=\"cover-art\""));
        Assert.Contains("class=\"title-page has-cover\"", html, StringComparison.Ordinal);
        Assert.Equal(2, Count(html, "class=\"art-portrait\""));
        Assert.Equal(2, Count(html, "class=\"art-place\""));
        // The picture follows its heading, not some other entry's.
        Assert.Matches(@"(?s)>a-known</h2>.{0,80}class=""art-portrait""", html);
        Assert.Matches(@"(?s)>Saltmere</h3>.{0,80}class=""art-place""", html);
        // Nothing of the hidden ones: not their names, not their pictures.
        foreach (var hidden in new[] { "a-hidden", "a-unknown", "a-emptyowner", "a-retired", "Hidden" })
        {
            Assert.DoesNotContain(hidden, html, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(art.AllImages().First(i => i.Width == 202).DataUri(), html.Replace(art.DataUri(art.Portrait("a-known")!)!, "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DmBook_PlacesEveryPicture_EachAfterItsOwnHeading()
    {
        var (dm, _) = await SeedAsync();
        var source = (await _reader.ReadAsync(dm, BookAudience.Dm))!;
        var art = await new BookArtReader(_media).ReadAsync(source);

        var (html, placed) = Page(source, art);

        Assert.Equal(11, placed);
        Assert.Equal(10, Count(html, "<img class=\"art\""));
        Assert.Matches(@"(?s)>a-hidden</h2>.{0,80}class=""art-portrait""", html);
        Assert.Matches(@"(?s)>Hidden</h3>.{0,80}class=""art-place""", html);
    }

    [Fact]
    public async Task APictureIsPlacedAfterARewrite_AndAChapterWhoseHeadingChangedGetsNone()
    {
        var (dm, _) = await SeedAsync();
        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        var art = await new BookArtReader(_media).ReadAsync(source);
        var document = BookComposer.Compose(source, "en");

        // A rewrite that kept the headings keeps its picture; one that renamed the entry's heading loses it, rather than misplacing it.
        var kept = document with { Sections = document.Sections.Select(s => s.Id == "cast" ? s with { Markdown = s.Markdown.Replace("known", "known") } : s).ToList() };
        var renamed = document with { Sections = document.Sections.Select(s => s.Id == "cast" ? s with { Markdown = s.Markdown.Replace("## a\\-known", "## Somebody else") } : s).ToList() };

        Assert.Contains("asset:", BookComposer.PlaceArt(kept, source, art, out _).Sections.First(s => s.Id == "cast").Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("asset:", BookComposer.PlaceArt(renamed, source, art, out _).Sections.First(s => s.Id == "cast").Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PicturesOff_PlacesNothing_AndTheTitlePageIsTypeAlone()
    {
        var (dm, _) = await SeedAsync();
        var source = (await _reader.ReadAsync(dm, BookAudience.Dm))!;
        var art = await new BookArtReader(_media).ReadAsync(source);

        var (html, placed) = Page(source, art, pictures: false);

        Assert.Equal(0, placed);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("has-cover", html.Substring(html.IndexOf("<body", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoEntriesWithOneName_EachGetTheirOwnPicture()
    {
        var (dm, id) = await SeedAsync();
        foreach (var (actorId, width) in new[] { ("twin-1", 211), ("twin-2", 212) })
        {
            await _actors.SaveAsync(new ActorDocument { Id = actorId, CampaignId = id, Name = "Twin", Visibility = ActorVisibility.Known, HasPortrait = true, PortraitVersion = 1 });
            await PutAsync(dm, actorId, RavenMediaStore.ActorPortrait, width);
        }

        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        var art = await new BookArtReader(_media).ReadAsync(source);
        var anchors = BookCompiler.ArtAnchors(source, art).Where(a => a.Alt == "Twin").ToList();

        Assert.Equal([0, 1], anchors.Select(a => a.Repeat).ToArray());
        Assert.NotEqual(anchors[0].Asset, anchors[1].Asset);
        var html = Page(source, art).Html;
        Assert.Equal(2, Count(html, ">Twin</h2>.{0,120}?class=\"art-portrait\""));
        // Not just "a picture each": the first Twin's own picture after the first heading, the second's after the second.
        var shown = System.Text.RegularExpressions.Regex.Matches(html, @"(?s)>Twin</h2>.{0,120}?<img class=""art"" src=""(?<src>data:[^""]+)""")
            .Select(m => m.Groups["src"].Value).ToList();
        Assert.Equal(anchors.Select(a => art.DataUri(a.Asset)).ToList(), shown);
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
