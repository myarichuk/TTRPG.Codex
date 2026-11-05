using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Book;

namespace Codex.Tests;

/// <summary>
/// The GM's own book pictures (docs/BOOK_EXPORT.md 28.3, step 3). Widths say whose a picture is, as in the rest of this class: 1xx was made
/// for the GM's edition and must never print in the party's, 2xx was made for the party and may print in both.
/// </summary>
public partial class BookArtTests
{
    private async Task<(BookSource Source, BookDocument Compiled, IReadOnlyList<BookEntryPosition> Entries, IReadOnlyList<BookSlot> Slots)> SlotsAsync(CampaignAccess dm, BookAudience audience)
    {
        var source = (await _reader.ReadAsync(dm, audience))!;
        var compiled = BookComposer.Compose(source, "en", out var entries);
        return (source, compiled, entries, BookSlots.For(source, compiled, entries));
    }

    private static BookImage Image(int width)
    {
        Assert.Equal(BookImageProblem.None, BookImageSanitizer.TrySanitize(Png(width), out var image));
        return image!;
    }

    /// <summary>The page as the route makes it: this edition's slots, the GM's stored pictures chosen for them, read and placed.</summary>
    private async Task<(string Html, BookArt Art)> BookPageAsync(CampaignAccess dm, BookAudience audience, Func<BookDocument, BookDocument>? rewrite = null)
    {
        var (source, compiled, entries, slots) = await SlotsAsync(dm, audience);
        var art = await new BookArtReader(_media).ReadAsync(source, book: BookSlots.Choose(slots, await _bookArt.GetAsync(dm)));
        var document = rewrite?.Invoke(compiled) ?? compiled;
        document = BookComposer.PlaceArt(document, compiled, entries, art, out _, slots);
        var html = BookHtml.Build(document, BookThemes.Parchment, new BookHtmlOptions { BoxSecrets = audience == BookAudience.Dm, Mention = BookCompiler.Mentions(source), Image = art.DataUri }).Html!;
        return (html, art);
    }

    private static string Uri(int width) => Image(width).DataUri();

    private static string KeyOf(IReadOnlyList<BookSlot> slots, string kind, string? owner = null, string? section = null) =>
        slots.Single(s => s.Kind == kind && (owner == null || s.Entry?.Owner == owner) && (section == null || s.Section == section)).Key;

    [Fact]
    public async Task SlotKeys_AreTheSubjectAsTheAudienceSeesIt_AndAHiddenSubjectHasNoSlot()
    {
        var (dm, _) = await SeedAsync();
        var gm = (await SlotsAsync(dm, BookAudience.Dm)).Slots;
        var party = (await SlotsAsync(dm, BookAudience.Party)).Slots;

        Assert.All(gm.Concat(party), s => Assert.True(BookSlots.IsKey(s.Key), s.Key));
        Assert.Equal(gm.Count, gm.Select(s => s.Key).Distinct().Count());
        // A character the party knows is the same subject in both books.
        Assert.Equal(KeyOf(gm, BookSlots.Entry, "a-known"), KeyOf(party, BookSlots.Entry, "a-known"));
        // A stranger: the party's slot is the mask, never the character, so the GM's picture of who it really is cannot land in it.
        var mask = KeyOf(party, BookSlots.Entry, "a-unknown");
        Assert.NotEqual(KeyOf(gm, BookSlots.Entry, "a-unknown"), mask);
        Assert.DoesNotContain(mask, gm.Select(s => s.Key));
        Assert.Equal(BookSlots.KeyFor("mask:a-unknown"), mask);
        // A hidden character, a GM-only page, a hidden place and an unpublished entry have no slot in the party book at all.
        foreach (var hidden in new[] { "a-hidden", "w-dm", "h-unpub" }) Assert.DoesNotContain(party, s => s.Entry?.Owner == hidden);
        Assert.DoesNotContain(party, s => s.Entry?.Sub == "l-hidden");
        Assert.Contains(gm, s => s.Entry?.Owner == "a-hidden");
        // Cover and one header per chapter in each.
        Assert.Single(party, s => s.Kind == BookSlots.Cover);
        Assert.Equal((await SlotsAsync(dm, BookAudience.Party)).Compiled.Sections.Count, party.Count(s => s.Kind == BookSlots.Header));
    }

    [Fact]
    public async Task AGmEditionPicture_NeverPrintsInThePartyBook_EvenStoredUnderAKeyThePartyHas()
    {
        var (dm, _) = await SeedAsync();
        var party = (await SlotsAsync(dm, BookAudience.Party)).Slots;
        // Stored straight through the repository, past the service's own check: the tag alone must keep these out of the party's book.
        Assert.True(await _bookArt.PutAsync(dm, KeyOf(party, BookSlots.Entry, "a-known"), BookArtDocument.Dm, "x", Image(151)));
        Assert.True(await _bookArt.PutAsync(dm, KeyOf(party, BookSlots.Header, section: "cast"), BookArtDocument.Dm, "x", Image(152)));
        Assert.True(await _bookArt.PutAsync(dm, KeyOf(party, BookSlots.Cover), BookArtDocument.Dm, "x", Image(153)));
        Assert.True(await _bookArt.PutAsync(dm, KeyOf(party, BookSlots.Entry, "a-unknown"), BookArtDocument.Dm, "x", Image(154)));

        // Each layer on its own. The choice takes no GM picture for a party slot...
        var stored = await _bookArt.GetAsync(dm);
        Assert.Empty(BookSlots.Choose(party, stored));
        // ...the reader refuses one handed to it for a party source anyway...
        var forged = party.Where(s => s.Kind != BookSlots.Header).Select(s => stored!.Pictures.FirstOrDefault(p => p.Slot == s.Key) is { } p ? new BookSlotPicture(s, p) : null).OfType<BookSlotPicture>().ToList();
        Assert.Equal(3, forged.Count);
        var forgedArt = await new BookArtReader(_media).ReadAsync((await SlotsAsync(dm, BookAudience.Party)).Source, book: forged);
        Assert.DoesNotContain(forgedArt.AllImages(), i => i.Width is >= 151 and <= 154);

        var (partyHtml, _) = await BookPageAsync(dm, BookAudience.Party);
        foreach (var w in new[] { 151, 152, 153, 154 }) Assert.DoesNotContain(Uri(w), partyHtml, StringComparison.Ordinal);
        // The party still gets the pictures it had: the known character's own portrait and the campaign cover.
        Assert.Contains(Uri(202), partyHtml, StringComparison.Ordinal);
        Assert.Contains(Uri(201), partyHtml, StringComparison.Ordinal);

        // The GM's book has them (the stranger's mask slot is not one of the GM's, so 154 is not there either).
        var (gmHtml, _) = await BookPageAsync(dm, BookAudience.Dm);
        foreach (var w in new[] { 151, 152, 153 }) Assert.Contains(Uri(w), gmHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri(154), gmHtml, StringComparison.Ordinal);

        // Even handed the map read for the GM, the party's entries and slots ask for party pictures only.
        var (source, compiled, entries, slots) = await SlotsAsync(dm, BookAudience.Party);
        var gm = await SlotsAsync(dm, BookAudience.Dm);
        var gmArt = await new BookArtReader(_media).ReadAsync(gm.Source, book: BookSlots.Choose(gm.Slots, await _bookArt.GetAsync(dm)));
        var placed = BookComposer.PlaceArt(compiled, compiled, entries, gmArt, out _, slots);
        var md = string.Join("\n", placed.Sections.Select(s => s.Markdown));
        // ...and the lookup gives a party entry nothing made for the GM, even from the GM's map.
        Assert.Null(gmArt.Slot(KeyOf(party, BookSlots.Entry, "a-known"), BookAudience.Party));
        Assert.NotNull(gmArt.Slot(KeyOf(party, BookSlots.Entry, "a-known"), BookAudience.Dm));
        var gmOnly = Enumerable.Range(1, gmArt.Count).Select(i => "a" + i).Where(a => gmArt.Image(a)!.Width is >= 151 and <= 153).ToList();
        Assert.Equal(3, gmOnly.Count); // a positive control: the GM's map does hold them
        foreach (var asset in gmOnly)
        {
            Assert.DoesNotContain("asset:" + asset + ")", md, StringComparison.Ordinal);
            Assert.NotEqual(asset, placed.CoverAsset);
        }

        _ = source;
    }

    [Fact]
    public async Task APartyPicture_PrintsInBothBooks_TheGmsOwnWinsInTheGmBook_AndBothReplaceTheSubjectsOwnPicture()
    {
        var (dm, _) = await SeedAsync();
        var party = (await SlotsAsync(dm, BookAudience.Party)).Slots;
        Assert.True(await _bookArt.PutAsync(dm, KeyOf(party, BookSlots.Entry, "a-known"), BookArtDocument.Party, "a-known", Image(221)));
        Assert.True(await _bookArt.PutAsync(dm, KeyOf(party, BookSlots.Header, section: "cast"), BookArtDocument.Party, "Cast", Image(222)));
        Assert.True(await _bookArt.PutAsync(dm, KeyOf(party, BookSlots.Cover), BookArtDocument.Party, "Ashfall", Image(223)));
        Assert.True(await _bookArt.PutAsync(dm, KeyOf(party, BookSlots.Cover), BookArtDocument.Dm, "Ashfall", Image(124)));
        Assert.True(await _bookArt.PutAsync(dm, KeyOf(party, BookSlots.Entry, "a-unknown"), BookArtDocument.Party, "stranger", Image(225)));

        _media.Reads.Clear();
        var (partyHtml, _) = await BookPageAsync(dm, BookAudience.Party);
        foreach (var w in new[] { 221, 222, 223, 225 }) Assert.Contains(Uri(w), partyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri(124), partyHtml, StringComparison.Ordinal);
        // The book picture stands in for the subject's own, which is then not read at all.
        Assert.DoesNotContain(Uri(202), partyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri(201), partyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(_media.Reads, r => r.StartsWith("a-known/", StringComparison.Ordinal) || r.EndsWith("/" + RavenMediaStore.CampaignCover, StringComparison.Ordinal));
        // The stranger's picture is placed under the stranger's heading.
        Assert.Matches(@"(?s)class=""art-portrait""><p[^>]*><img class=""art"" src=""" + System.Text.RegularExpressions.Regex.Escape(Uri(225)), partyHtml);

        var (gmHtml, _) = await BookPageAsync(dm, BookAudience.Dm);
        foreach (var w in new[] { 221, 222, 124 }) Assert.Contains(Uri(w), gmHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri(223), gmHtml, StringComparison.Ordinal);
        // The mask's picture is the party's; the GM's book shows the character under its own name and key.
        Assert.DoesNotContain(Uri(225), gmHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChapterHeader_GoesUnderTheTitle_InARewriteToo_AndTheDropCapStillFindsTheFirstParagraph()
    {
        var (dm, _) = await SeedAsync();
        var party = (await SlotsAsync(dm, BookAudience.Party)).Slots;
        Assert.True(await _bookArt.PutAsync(dm, KeyOf(party, BookSlots.Header, section: "cast"), BookArtDocument.Party, "Cast", Image(231)));

        var (html, _) = await BookPageAsync(dm, BookAudience.Party);
        Assert.Matches(@"(?s)<h1[^>]*>Cast</h1>\s*<div class=""art-header""><p[^>]*><img class=""art"" src=""" + System.Text.RegularExpressions.Regex.Escape(Uri(231)), html);
        Assert.Contains(".chapter > h1 + .art-header + p", html, StringComparison.Ordinal);

        // A rewritten chapter starts with the title code wrote, so its header is placed the same way.
        BookDocument Cast(BookDocument d, string markdown) => d with { Sections = d.Sections.Select(s => s.Id == "cast" ? s with { Markdown = markdown } : s).ToList() };
        var (rewritten, _) = await BookPageAsync(dm, BookAudience.Party, d => Cast(d, "# Cast\n\nThe people of the coast, newly told."));
        Assert.Matches(@"(?s)<h1[^>]*>Cast</h1>\s*<div class=""art-header"">", rewritten);
        // A chapter that does not begin with its title gets no header rather than one in the wrong place.
        var (untitled, _) = await BookPageAsync(dm, BookAudience.Party, d => Cast(d, "Just text.\n\n# Cast"));
        Assert.DoesNotContain("art-header\"", untitled, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheService_TagsByEdition_AndRefusesASlotTheEditionDoesNotHave()
    {
        var (dm, _) = await SeedAsync();
        var service = new BookArtService(_reader, _bookArt, new CampaignAccessResolver(_campaigns));
        var gm = (await SlotsAsync(dm, BookAudience.Dm)).Slots;
        var party = (await SlotsAsync(dm, BookAudience.Party)).Slots;

        // The GM's key for the stranger's real self, and the hidden character's, are not party slots.
        Assert.Equal(BookArtUpload.NoSuchSlot, await service.UploadAsync(dm, "party", KeyOf(gm, BookSlots.Entry, "a-unknown"), Png(241), "image/png"));
        Assert.Equal(BookArtUpload.NoSuchSlot, await service.UploadAsync(dm, "party", KeyOf(gm, BookSlots.Entry, "a-hidden"), Png(241), "image/png"));
        Assert.Equal(BookArtUpload.NoSuchSlot, await service.UploadAsync(dm, "party", "s000000000000000000000000", Png(241), "image/png"));
        Assert.Equal(BookArtUpload.NoSuchSlot, await service.UploadAsync(dm, "party", "../cover", Png(241), "image/png"));
        Assert.Equal(BookArtUpload.BadImage, await service.UploadAsync(dm, "party", KeyOf(party, BookSlots.Cover), System.Text.Encoding.ASCII.GetBytes("<svg onload=alert(1)>"), "image/png"));
        Assert.Equal(BookArtUpload.BadImage, await service.UploadAsync(dm, "party", KeyOf(party, BookSlots.Cover), Png(241), "image/svg+xml"));
        Assert.Equal(BookArtUpload.NotAllowed, await service.UploadAsync(new CampaignAccess(dm.CampaignId, PlayerUser, CampaignRole.Player), "party", KeyOf(party, BookSlots.Cover), Png(241), "image/png"));
        Assert.Equal(BookArtUpload.NotAllowed, await service.UploadAsync(dm, "both", KeyOf(party, BookSlots.Cover), Png(241), "image/png"));
        Assert.Null((await _bookArt.GetAsync(dm))?.Pictures.FirstOrDefault());

        Assert.Equal(BookArtUpload.Stored, await service.UploadAsync(dm, "party", KeyOf(party, BookSlots.Entry, "a-unknown"), Png(242), "image/png"));
        Assert.Equal(BookArtUpload.Stored, await service.UploadAsync(dm, "dm", KeyOf(gm, BookSlots.Entry, "a-hidden"), Png(143), "image/png"));
        var stored = (await _bookArt.GetAsync(dm))!.Pictures;
        Assert.Equal(BookArtDocument.Party, stored.Single(p => p.Width == 242).BuiltFrom);
        Assert.Equal(BookArtDocument.Dm, stored.Single(p => p.Width == 143).BuiltFrom);

        var tray = (await service.TrayAsync(dm, "party", "en"))!;
        var stranger = tray.Chapters.SelectMany(c => c.Slots).Single(s => s.Key == KeyOf(party, BookSlots.Entry, "a-unknown"));
        Assert.True(stranger.Mine);
        Assert.Equal(BookArtDocument.Party, stranger.Placed);
        Assert.DoesNotContain(tray.Chapters.SelectMany(c => c.Slots), s => s.Label == "a-hidden" || s.Label == "a-unknown");
        Assert.Null(await service.TrayAsync(new CampaignAccess(dm.CampaignId, PlayerUser, CampaignRole.Player), "party", "en"));

        // Removing from the party tray takes the party's picture only.
        Assert.True(await service.RemoveAsync(dm, "party", KeyOf(party, BookSlots.Entry, "a-unknown")));
        Assert.True(await service.RemoveAsync(dm, "party", KeyOf(gm, BookSlots.Entry, "a-hidden")));
        Assert.Equal(new[] { 143 }, (await _bookArt.GetAsync(dm))!.Pictures.Select(p => p.Width));
    }

    [Fact]
    public async Task BookArt_IsTheGmsAlone_LeavesWithTheCampaign_IsInTheBackup_AndNotInTheExport()
    {
        var (dm, id) = await SeedAsync();
        var key = KeyOf((await SlotsAsync(dm, BookAudience.Party)).Slots, BookSlots.Cover);
        Assert.True(await _bookArt.PutAsync(dm, key, BookArtDocument.Party, "Ashfall", Image(251)));
        var player = new CampaignAccess(id, PlayerUser, CampaignRole.Player);
        Assert.Null(await _bookArt.GetAsync(player));
        Assert.False(await _bookArt.PutAsync(player, key, BookArtDocument.Party, "x", Image(252)));
        Assert.False(await _bookArt.RemoveAsync(player, key, BookArtDocument.Party));
        Assert.False(await _bookArt.PutAsync(dm, key, "both", "x", Image(252)));
        Assert.False(await _bookArt.PutAsync(dm, "cover", BookArtDocument.Party, "x", Image(252)));
        // The media routes ask for fixed names; none of them is a book picture's.
        foreach (var name in new[] { RavenMediaStore.CampaignCover, RavenMediaStore.ActorPortrait, RavenMediaStore.EntryArt })
        {
            Assert.Null(await _media.GetAsync(BookArtDocument.IdFor(id), name));
        }

        var export = new CampaignExportService(_campaigns, _actors, _sessions, _encounters, _facts, _notes, _regions, _homebrew, _wiki);
        var json = CampaignExportService.Serialize((await export.ExportAsync(dm))!);
        Assert.DoesNotContain("bookart", json, StringComparison.Ordinal);
        Assert.DoesNotContain(key, json, StringComparison.Ordinal);

        var root = Path.Combine(Path.GetTempPath(), "ba-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new JsonBackups(_db, _campaigns, export).WriteAsync(root, DateTime.UtcNow);
            var mine = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => f.Contains(id.Replace('/', '_'), StringComparison.Ordinal)).ToList();
            Assert.Contains(mine, f => Path.GetFileName(f).StartsWith(BookArtDocument.AttachmentFor(key, BookArtDocument.Party), StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        await _campaigns.DeleteAsync(id, DmUser);
        using var session = _db.Store.OpenAsyncSession();
        Assert.Null(await session.LoadAsync<BookArtDocument>(BookArtDocument.IdFor(id)));
    }
}
