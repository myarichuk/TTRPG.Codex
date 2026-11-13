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
public partial class BookArtTests : IClassFixture<RavenDbFixture>
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
    private readonly RavenDbService _db;
    private readonly RavenBookArtRepository _bookArt;

    public BookArtTests(RavenDbFixture fixture)
    {
        var db = fixture.Open(fixture.DbName);
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
        _db = db;
        _bookArt = new RavenBookArtRepository(db);
    }

    private sealed class RecordingMedia(IMediaStore inner) : IMediaStore
    {
        public List<string> Reads { get; } = [];
        public Task PutAsync(CampaignAccess access, string documentId, string name, Stream content, string contentType) => inner.PutAsync(access, documentId, name, content, contentType);
        public Task<MediaFile?> GetAsync(string documentId, string name) { Reads.Add(documentId + "/" + name); return inner.GetAsync(documentId, name); }
        public Task DeleteAsync(CampaignAccess access, string documentId, string name) => inner.DeleteAsync(access, documentId, name);
    }

    /// <summary>A valid PNG of the given width (and one pixel high).</summary>
    internal static byte[] Png(int width)
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

    internal static uint Crc(byte[] data)
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

    /// <summary>A noisy photo-like JPEG: big enough that the scaler shrinks it, and not trivially compressible.</summary>
    private static byte[] Noisy(int width, int height, int seed)
    {
        var random = new Random(seed);
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        for (var i = 0; i < pixels.Length; i += 4) { var p = i / 4; pixels[i] = (byte)(p % width / 10 + random.Next(16)); pixels[i + 1] = (byte)(p / width / 12 + random.Next(16)); pixels[i + 2] = (byte)random.Next(40); pixels[i + 3] = 255; }
        return SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 92).ToArray();
    }

    [Fact]
    public async Task NearTheSizeCap_ThePreviewLeavesOutExactlyWhatThePrintLeavesOut_AndSaysWhichSlot()
    {
        var (dm, _) = await SeedAsync();
        var cover = Noisy(2400, 3000, 5);
        var portrait = Noisy(2400, 3000, 6);
        await _media.PutAsync(dm, dm.CampaignId, RavenMediaStore.CampaignCover, new MemoryStream(cover), "image/jpeg");
        await _media.PutAsync(dm, "a-known", RavenMediaStore.ActorPortrait, new MemoryStream(portrait), "image/jpeg");
        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        Assert.True(source.Campaign.HasCover);

        long Estimate(byte[] bytes, string kind)
        {
            Assert.Equal(BookImageProblem.None, BookImageSanitizer.TrySanitize(bytes, out var image));
            return BookImageScaler.PrintEstimate(image!, kind);
        }

        // Room for the cover and not for the portrait after it, counted at print size. The preview's own files are about six times smaller,
        // so a cap that counted them would let the portrait into the preview and not into the PDF.
        var cap = Estimate(cover, "cover") + Estimate(portrait, "portrait") - 1;
        var scaler = new BookImageScaler();
        var print = await new BookArtReader(_media, scaler, cap).ReadAsync(source, BookArtQuality.Print, forUser: dm.UserId);
        var preview = await new BookArtReader(_media, scaler, cap).ReadAsync(source, BookArtQuality.Preview, forUser: dm.UserId);

        Assert.True(preview.AllImages().Sum(i => (long)i.Bytes.Length) + Estimate(portrait, "portrait") <= cap, "the preview's own bytes would have room");
        foreach (var art in new[] { print, preview })
        {
            Assert.NotNull(art.Cover());
            Assert.Null(art.Portrait("a-known"));
            var left = Assert.Single(art.Skipped, s => s.Subject == "portrait:a-known");
            Assert.Equal("limit", left.Reason);
            var actor = source.Actors.Single(a => a.Id == "a-known");
            Assert.Equal(BookSlots.KeyFor(BookSlots.ActorSubject(BookAudience.Party, actor)), left.Slot);
        }

        Assert.Equal(print.Skipped.Select(s => s.Subject), preview.Skipped.Select(s => s.Subject));
        Assert.Equal(print.Count, preview.Count);
    }

    [Fact]
    public async Task ArtReadForTheDm_NeverReachesAPartyDocument_BecauseThePartyHasNoAnchorForIt()
    {
        var (dm, _) = await SeedAsync();
        var party = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        var dmArt = await new BookArtReader(_media).ReadAsync((await _reader.ReadAsync(dm, BookAudience.Dm))!);

        // Even handed the DM's whole picture map, the party's document has no heading for a hidden subject, so nothing of it is placed.
        var (html, placed) = Page(party, dmArt);

        Assert.Equal(5, placed);
        Assert.Equal(4, Count(html, EntryPicture));
        foreach (var hidden in new[] { "a-hidden", "a-unknown", "w-dm", "Hidden" }) Assert.DoesNotContain(hidden, html, StringComparison.Ordinal);

        // The map itself goes to the GM's browser for the preview, so it is held to the same rule as the page: an entry the party knows only
        // as a masked stranger has no picture flag in the party's source, and the DM's picture of them is not named, under any key.
        var map = BookDrafts.Map(party, dmArt).Art;
        var hiddenPictures = dmArt.AllImages().Where(i => i.Width is >= 101 and <= 199).Select(i => i.DataUri()).ToHashSet();
        Assert.NotEmpty(hiddenPictures); // a positive control: the DM's map does hold them
        Assert.DoesNotContain(map.Values, hiddenPictures.Contains);
        Assert.Equal(5, map.Count);
    }

    // The book as the editor draws it: the edition's draft as generated, every art: reference resolved through the picture map made for
    // this source from the pictures given (BookDrafts.Map), which may have been read for another edition.
    private static (string Html, int Placed) Page(BookSource source, BookArt art)
    {
        var page = BookArtTests.Resolved(BookDraftWriter.Generate(source, "en", null), BookDrafts.Map(source, art).Art, out var placed);
        return (page, placed);
    }

    private const string EntryPicture = @"\{\{(portrait|place)\n!\[[^\]\n]*\]\(data:";

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
        Assert.Equal(4, Count(html, EntryPicture));
        Assert.Equal(1, Count(html, @"\{\{cover-art\n!\[[^\]\n]*\]\(data:"));
        Assert.Equal(2, Count(html, @"\{\{portrait\n"));
        Assert.Equal(2, Count(html, @"\{\{place\n"));
        // The picture follows its heading, not some other entry's.
        Assert.Matches(@"\n## a\\-known\n\n\{\{portrait\n", html);
        Assert.Matches(@"\n### Saltmere\n\n\{\{place\n", html);
        // Nothing of the hidden ones: not their names, not their pictures.
        foreach (var hidden in new[] { "a-hidden", "a-unknown", "a-emptyowner", "a-retired", "Hidden" })
        {
            Assert.DoesNotContain(hidden, html, StringComparison.Ordinal);
        }

    }

    [Fact]
    public async Task DmBook_PlacesEveryPicture_EachAfterItsOwnHeading()
    {
        var (dm, _) = await SeedAsync();
        var source = (await _reader.ReadAsync(dm, BookAudience.Dm))!;
        var art = await new BookArtReader(_media).ReadAsync(source);

        var (html, placed) = Page(source, art);

        Assert.Equal(11, placed);
        Assert.Equal(10, Count(html, EntryPicture));
        Assert.Matches(@"\n## a\\-hidden\n\n\{\{portrait\n", html);
        Assert.Matches(@"\n### Hidden\n\n\{\{place\n", html);
    }

    [Fact]
    public async Task ABodyLineThatReadsLikeAnotherEntrysHeading_DoesNotTakeThatEntrysPicture()
    {
        var (dm, id) = await SeedAsync();
        // "History" sorts before "Vera" and its body has a subsection called "Vera"; only the page Vera has art.
        await _wiki.SaveAsync(new WikiPageDocument { Id = "w-history", CampaignId = id, Slug = "history", Title = "History", Visibility = WikiVisibility.Party, Body = "Old days.\n\n## Vera\n\nA queen, long dead.\n\nLine one  \nLine two" });
        await _wiki.SaveAsync(new WikiPageDocument { Id = "w-vera", CampaignId = id, Slug = "vera", Title = "Vera", Visibility = WikiVisibility.Party, HasArt = true, ArtVersion = 1, Body = "The harbourmaster." });
        await PutAsync(dm, "w-vera", RavenMediaStore.EntryArt, 233);
        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        var art = await new BookArtReader(_media).ReadAsync(source);

        var pages = BookArtTests.Resolved(BookDraftWriter.Generate(source, "en", null).Where(c => c.Section == "pages"), BookDrafts.Map(source, art).Art, out _);

        var uri = art.DataUri(art.Wiki("w-vera")!)!;
        var at = pages.IndexOf(uri, StringComparison.Ordinal);
        Assert.True(at > 0);
        // After the page's own heading (the "### Vera" with the entry marker), not after the subsection of History's body.
        var own = pages.LastIndexOf("}}\n### Vera\n", StringComparison.Ordinal);
        Assert.True(own > 0 && at > own);
        Assert.Equal(1, Count(pages, System.Text.RegularExpressions.Regex.Escape(uri)));
        Assert.DoesNotMatch(@"#+ Vera\n\n\{\{", pages.Substring(0, own));
        // History's own "## Vera" is written under History's heading (51), on the line it was on.
        Assert.Contains("Old days.\n\n##### Vera\n\nA queen, long dead.", pages, StringComparison.Ordinal);
        // Markdown hard breaks (two trailing spaces) survive the generator.
        Assert.Contains("Line one  \nLine two", pages, StringComparison.Ordinal);
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
        BookComposer.Compose(source, "en", out var entries);
        var anchors = BookCompiler.ArtAnchors(entries, art).Where(a => a.Entry.Alt == "Twin").ToList();

        Assert.Equal(2, anchors.Count);
        Assert.NotEqual(anchors[0].Asset, anchors[1].Asset);
        Assert.True(anchors[0].Entry.Line < anchors[1].Entry.Line);
        var html = Page(source, art).Html;
        // Not just "a picture each": the first Twin's own picture after the first heading, the second's after the second.
        var shown = System.Text.RegularExpressions.Regex.Matches(html, @"\n## Twin\n\n\{\{portrait\n!\[[^\]\n]*\]\((?<src>data:[^)]+)\)")
            .Select(m => m.Groups["src"].Value).ToList();
        Assert.Equal(anchors.Select(a => art.DataUri(a.Asset)!).ToList(), shown);
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
