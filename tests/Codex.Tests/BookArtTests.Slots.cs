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

    /// <summary>The book as the editor draws it and the PDF prints it: this edition's draft as generated, with each <c>art:</c> reference
    /// resolved through this edition's picture map (<see cref="BookDrafts.ArtAsync"/>: the GM's stored pictures chosen for its slots, read and
    /// mapped). A reference the map does not name stays a reference, which the renderer draws as nothing.</summary>
    private async Task<(string Html, BookArt Art)> BookPageAsync(CampaignAccess dm, BookAudience audience)
    {
        var (source, _, _, slots) = await SlotsAsync(dm, audience);
        var stored = await _bookArt.GetAsync(dm);
        var art = await new BookArtReader(_media).ReadAsync(source, book: BookSlots.Choose(slots, stored));
        var drafts = new BookDrafts(_reader, new RavenBookDraftRepository(_db), _bookArt, new BookArtReader(_media), new CampaignAccessResolver(_campaigns));
        var map = (await drafts.ArtAsync(dm, audience == BookAudience.Dm ? "dm" : "party", BookArtQuality.Print, CancellationToken.None)).Art;
        return (Resolved(BookDraftWriter.Generate(source, "en", stored), map, out _), art);
    }

    /// <summary>The draft's chapters with every <c>art:KEY</c> the map names replaced by its picture; <paramref name="placed"/> counts them.</summary>
    internal static string Resolved(IEnumerable<BookDraftGenerated> chapters, IReadOnlyDictionary<string, string> map, out int placed)
    {
        var count = 0;
        var page = System.Text.RegularExpressions.Regex.Replace(string.Join("\n", chapters.Select(c => c.Markdown)), @"\(art:(?<key>[^)\s]+)\)",
            m =>
            {
                if (!map.TryGetValue(m.Groups["key"].Value, out var uri)) return m.Value;
                count++;
                return "(" + uri + ")";
            });
        placed = count;
        return page;
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
        var source = (await SlotsAsync(dm, BookAudience.Party)).Source;
        var gm = await SlotsAsync(dm, BookAudience.Dm);
        var gmArt = await new BookArtReader(_media).ReadAsync(gm.Source, book: BookSlots.Choose(gm.Slots, await _bookArt.GetAsync(dm)));
        var placed = BookDrafts.Map(source, gmArt).Art;
        // ...and the lookup gives a party entry nothing made for the GM, even from the GM's map.
        Assert.Null(gmArt.Slot(KeyOf(party, BookSlots.Entry, "a-known"), BookAudience.Party));
        Assert.NotNull(gmArt.Slot(KeyOf(party, BookSlots.Entry, "a-known"), BookAudience.Dm));
        var gmOnly = Enumerable.Range(1, gmArt.Count).Select(i => "a" + i).Where(a => gmArt.Image(a)!.Width is >= 151 and <= 153).ToList();
        Assert.Equal(3, gmOnly.Count); // a positive control: the GM's map does hold them
        foreach (var asset in gmOnly)
        {
            Assert.DoesNotContain(gmArt.DataUri(asset)!, placed.Values);
        }

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
        Assert.Matches(@"\{\{entry,#e-" + KeyOf(party, BookSlots.Entry, "a-unknown") + @"\}\}\n## [^\n]+\n\n\{\{portrait\n!\[[^\]\n]*\]\(" + System.Text.RegularExpressions.Regex.Escape(Uri(225)), partyHtml);

        var (gmHtml, _) = await BookPageAsync(dm, BookAudience.Dm);
        foreach (var w in new[] { 221, 222, 124 }) Assert.Contains(Uri(w), gmHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri(223), gmHtml, StringComparison.Ordinal);
        // The mask's picture is the party's; the GM's book shows the character under its own name and key.
        Assert.DoesNotContain(Uri(225), gmHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChapterHeader_GoesUnderTheTitle()
    {
        var (dm, _) = await SeedAsync();
        var party = (await SlotsAsync(dm, BookAudience.Party)).Slots;
        Assert.True(await _bookArt.PutAsync(dm, KeyOf(party, BookSlots.Header, section: "cast"), BookArtDocument.Party, "Cast", Image(231)));

        var (html, _) = await BookPageAsync(dm, BookAudience.Party);
        Assert.Matches(@"\{\{chapter,#ch-cast\n# Cast\n\}\}\n\n\{\{chapter-art\n!\[[^\]\n]*\]\(" + System.Text.RegularExpressions.Regex.Escape(Uri(231)), html);
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
        // The two strangers read alike in the party book; the tray numbers them so the GM can tell their slots apart, from the party's labels only.
        var strangers = tray.Chapters.Single(c => c.Id == "cast").Slots.Where(s => s.Label.EndsWith(')')).Select(s => s.Label).ToList();
        Assert.Equal(2, strangers.Count);
        Assert.Equal(strangers[0][..^4] + " (1)", strangers[0]);
        Assert.Equal(strangers[0][..^4] + " (2)", strangers[1]);

        // Removing from the party tray takes the party's picture only.
        Assert.True(await service.RemoveAsync(dm, "party", KeyOf(party, BookSlots.Entry, "a-unknown")));
        Assert.True(await service.RemoveAsync(dm, "party", KeyOf(gm, BookSlots.Entry, "a-hidden")));
        Assert.Equal(new[] { 143 }, (await _bookArt.GetAsync(dm))!.Pictures.Select(p => p.Width));
    }

    // Keep three (28.3, item 3): a new picture puts the one in use among the kept, only the one in use prints, the GM can print a kept one
    // instead, and a fourth drops the version that went out of use longest ago, its bytes with it.
    [Fact]
    public async Task ASlotKeepsThreeVersions_OnlyTheOneInUsePrints_AndAFourthDropsTheOldestKept()
    {
        var (dm, _) = await SeedAsync();
        var cover = KeyOf((await SlotsAsync(dm, BookAudience.Party)).Slots, BookSlots.Cover);
        foreach (var width in new[] { 271, 272, 273 }) Assert.True(await _bookArt.PutAsync(dm, cover, BookArtDocument.Party, "Ashfall", Image(width)));

        var art = (await _bookArt.GetAsync(dm))!;
        Assert.Equal(273, Assert.Single(art.Pictures).Width);
        Assert.Equal(new[] { 271, 272 }, art.Kept.Select(p => p.Width).Order());
        Assert.Equal(3, art.Pictures.Concat(art.Kept).Select(p => p.Attachment).Distinct().Count());
        var (html, _) = await BookPageAsync(dm, BookAudience.Party);
        Assert.Contains(Uri(273), html, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri(271), html, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri(272), html, StringComparison.Ordinal);

        // The GM prints the first one again; the one in use goes among the kept.
        var first = art.Kept.Single(p => p.Width == 271).Attachment;
        Assert.True(await _bookArt.UseAsync(dm, cover, BookArtDocument.Party, first));
        (html, _) = await BookPageAsync(dm, BookAudience.Party);
        Assert.Contains(Uri(271), html, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri(273), html, StringComparison.Ordinal);
        Assert.Contains(Uri(271), (await BookPageAsync(dm, BookAudience.Dm)).Html, StringComparison.Ordinal);

        // The third back in use: the first, added longest ago, is now the kept one that went out of use last.
        Assert.True(await _bookArt.UseAsync(dm, cover, BookArtDocument.Party, (await _bookArt.GetAsync(dm))!.Kept.Single(p => p.Width == 273).Attachment));

        // A fourth: 272 went out of use longest ago, so it is dropped, bytes and all; the first, added before it but put back since, stays.
        var second = (await _bookArt.GetAsync(dm))!.Kept.Single(p => p.Width == 272).Attachment;
        Assert.True(await _bookArt.PutAsync(dm, cover, BookArtDocument.Party, "Ashfall", Image(274)));
        art = (await _bookArt.GetAsync(dm))!;
        Assert.Equal(274, Assert.Single(art.Pictures).Width);
        Assert.Equal(new[] { 271, 273 }, art.Kept.Select(p => p.Width).Order());
        Assert.Null(await _media.GetAsync(BookArtDocument.IdFor(dm.CampaignId), second));
        Assert.NotNull(await _media.GetAsync(BookArtDocument.IdFor(dm.CampaignId), first));

        // Taking it out of the book keeps it, a picture that may have been paid for; deleting a version takes it and its bytes.
        Assert.True(await _bookArt.RemoveAsync(dm, cover, BookArtDocument.Party));
        art = (await _bookArt.GetAsync(dm))!;
        Assert.Empty(art.Pictures);
        Assert.Equal(new[] { 271, 273, 274 }, art.Kept.Select(p => p.Width).Order());
        Assert.DoesNotContain(Uri(274), (await BookPageAsync(dm, BookAudience.Party)).Html, StringComparison.Ordinal);
        var service = new BookArtService(_reader, _bookArt, new CampaignAccessResolver(_campaigns));
        Assert.Equal((false, 3), ((await service.TrayAsync(dm, "party", "en"))!.Cover.Mine, (await service.TrayAsync(dm, "party", "en"))!.Cover.Kept));
        Assert.True(await _bookArt.RemoveAsync(dm, cover, BookArtDocument.Party, first));
        Assert.Equal(new[] { 273, 274 }, (await _bookArt.GetAsync(dm))!.Kept.Select(p => p.Width).Order());
        Assert.Null(await _media.GetAsync(BookArtDocument.IdFor(dm.CampaignId), first));

        // Deleting the one in use by its name leaves none in use and the kept ones as they are.
        var inUse = (await _bookArt.GetAsync(dm))!.Kept.Single(p => p.Width == 274).Attachment;
        Assert.True(await _bookArt.UseAsync(dm, cover, BookArtDocument.Party, inUse));
        Assert.True(await _bookArt.RemoveAsync(dm, cover, BookArtDocument.Party, inUse));
        art = (await _bookArt.GetAsync(dm))!;
        Assert.Empty(art.Pictures);
        Assert.Equal(273, Assert.Single(art.Kept).Width);
    }

    // A picture stored before versions were kept has the slot's name alone: a new one puts it among the kept under that name, and it can be used
    // and deleted by it. Any other form of name is refused before it is looked up.
    [Fact]
    public async Task APictureStoredUnderTheOldName_IsKept_UsedAndDeletedByIt()
    {
        var (dm, _) = await SeedAsync();
        var cover = KeyOf((await SlotsAsync(dm, BookAudience.Party)).Slots, BookSlots.Cover);
        var id = BookArtDocument.IdFor(dm.CampaignId);
        var legacy = BookArtDocument.AttachmentFor(cover, BookArtDocument.Party);
        var old = Image(281);
        using (var session = _db.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new BookArtDocument
            {
                Id = id, CampaignId = dm.CampaignId,
                Pictures = { new BookArtPicture { Slot = cover, BuiltFrom = BookArtDocument.Party, Attachment = legacy, ContentType = old.ContentType, Width = old.Width, Height = old.Height, Bytes = old.Bytes.Length } }
            }, id);
            session.Advanced.Attachments.Store(id, legacy, new MemoryStream(old.Bytes), old.ContentType);
            await session.SaveChangesAsync();
        }

        Assert.True(await _bookArt.PutAsync(dm, cover, BookArtDocument.Party, "Ashfall", Image(282)));
        var art = (await _bookArt.GetAsync(dm))!;
        Assert.Equal(legacy, Assert.Single(art.Kept).Attachment);
        Assert.NotEqual(legacy, Assert.Single(art.Pictures).Attachment);
        Assert.True(await _bookArt.UseAsync(dm, cover, BookArtDocument.Party, legacy));
        Assert.Contains(Uri(281), (await BookPageAsync(dm, BookAudience.Party)).Html, StringComparison.Ordinal);
        Assert.True(await _bookArt.RemoveAsync(dm, cover, BookArtDocument.Party, legacy));
        Assert.Null(await _media.GetAsync(id, legacy));
        Assert.Equal(282, Assert.Single((await _bookArt.GetAsync(dm))!.Kept).Width);

        Assert.True(BookArtDocument.IsAttachment(legacy, cover, BookArtDocument.Party));
        Assert.True(BookArtDocument.IsAttachment(legacy + "-0a1b2c3d", cover, BookArtDocument.Party));
        foreach (var bad in new[] { legacy + "-0A1B2C3D", legacy + "-0a1b2c3", legacy + "-0a1b2c3d4", legacy + "0a1b2c3d", legacy + "-0a1b2c3g", legacy[..^1], null })
        {
            Assert.False(BookArtDocument.IsAttachment(bad, cover, BookArtDocument.Party));
        }

        Assert.False(BookArtDocument.IsAttachment(legacy, cover, BookArtDocument.Dm));
    }

    // A picture stored for a place this edition no longer has prints nowhere, but counts: the tray says how many, lists them with a picture each,
    // and the GM can delete them; the other edition's are not in the list.
    [Fact]
    public async Task PicturesForAPlaceThatIsGone_AreListedForTheGmToDelete()
    {
        var (dm, _) = await SeedAsync();
        var service = new BookArtService(_reader, _bookArt, new CampaignAccessResolver(_campaigns), media: _media);
        var gone = BookSlots.KeyFor("actor:a-long-gone");
        Assert.True(await _bookArt.PutAsync(dm, gone, BookArtDocument.Party, "Old friend", Image(291)));
        Assert.True(await _bookArt.PutAsync(dm, gone, BookArtDocument.Party, "Old friend", Image(292)));
        Assert.True(await _bookArt.PutAsync(dm, gone, BookArtDocument.Dm, "Old friend", Image(191)));

        Assert.Equal(2, (await service.TrayAsync(dm, "party", "en"))!.Gone);
        var list = (await service.GoneAsync(dm, "party"))!;
        Assert.Equal(2, list.Count);
        Assert.All(list, g => Assert.Equal((gone, "Old friend"), (g.Slot, g.Label)));
        Assert.All(list, g => Assert.StartsWith("data:image/", g.Version.Thumbnail, StringComparison.Ordinal));
        Assert.DoesNotContain(list, g => g.Version.Id.StartsWith("book-dm-", StringComparison.Ordinal));
        Assert.Null(await service.GoneAsync(new CampaignAccess(dm.CampaignId, PlayerUser, CampaignRole.Player), "party"));

        foreach (var g in list) Assert.True(await service.RemoveAsync(dm, "party", g.Slot, g.Version.Id));
        Assert.Equal(0, (await service.TrayAsync(dm, "party", "en"))!.Gone);
        Assert.Equal(1, (await service.TrayAsync(dm, "dm", "en"))!.Gone);
    }

    // The tag holds for versions as for pictures in use: a version kept for the GM's edition is never put in use in the party's, by the store or
    // the service, whatever name is posted; the service's version list is the GM's and the edition's own, with a picture to choose by.
    [Fact]
    public async Task AVersionKeptForTheGmEdition_IsNeverUsedInTheParty_AndTheVersionListIsTheEditionsOwn()
    {
        var (dm, _) = await SeedAsync();
        var service = new BookArtService(_reader, _bookArt, new CampaignAccessResolver(_campaigns), media: _media);
        var cover = KeyOf((await SlotsAsync(dm, BookAudience.Party)).Slots, BookSlots.Cover);
        Assert.True(await _bookArt.PutAsync(dm, cover, BookArtDocument.Dm, "Ashfall", Image(161)));
        Assert.True(await _bookArt.PutAsync(dm, cover, BookArtDocument.Dm, "Ashfall", Image(162)));
        Assert.True(await _bookArt.PutAsync(dm, cover, BookArtDocument.Party, "Ashfall", Image(261)));
        Assert.True(await _bookArt.PutAsync(dm, cover, BookArtDocument.Party, "Ashfall", Image(262)));
        var art = (await _bookArt.GetAsync(dm))!;
        var gmKept = art.Kept.Single(p => p.Width == 161).Attachment;
        var partyKept = art.Kept.Single(p => p.Width == 261).Attachment;

        Assert.False(await _bookArt.UseAsync(dm, cover, BookArtDocument.Party, gmKept));
        Assert.False(await service.UseAsync(dm, "party", cover, gmKept));
        // A party-shaped name for the GM's version, and a made-up one, are not versions of the party's slot either.
        Assert.False(await service.UseAsync(dm, "party", cover, gmKept.Replace("book-dm-", "book-party-", StringComparison.Ordinal)));
        Assert.False(await service.UseAsync(dm, "party", cover, BookArtDocument.AttachmentFor(cover, BookArtDocument.Party) + "-00000000"));
        Assert.False(await _bookArt.RemoveAsync(dm, cover, BookArtDocument.Party, gmKept));
        Assert.False(await service.UseAsync(new CampaignAccess(dm.CampaignId, PlayerUser, CampaignRole.Player), "party", cover, partyKept));
        Assert.Equal(262, (await _bookArt.GetAsync(dm))!.Pictures.Single(p => p.BuiltFrom == BookArtDocument.Party).Width);
        var party = (await BookPageAsync(dm, BookAudience.Party)).Html;
        Assert.DoesNotContain(Uri(161), party, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri(162), party, StringComparison.Ordinal);

        Assert.True(await service.UseAsync(dm, "party", cover, partyKept));
        Assert.Contains(Uri(261), (await BookPageAsync(dm, BookAudience.Party)).Html, StringComparison.Ordinal);

        var versions = (await service.VersionsAsync(dm, "party", cover))!;
        Assert.Equal(new[] { true, false }, versions.Select(v => v.InUse));
        Assert.Equal(new[] { partyKept }, versions.Where(v => v.InUse).Select(v => v.Id));
        Assert.All(versions, v => Assert.StartsWith("data:image/", v.Thumbnail, StringComparison.Ordinal));
        Assert.DoesNotContain(versions, v => v.Id.StartsWith("book-dm-", StringComparison.Ordinal));
        Assert.Equal(2, (await service.VersionsAsync(dm, "dm", cover))!.Count);
        Assert.Null(await service.VersionsAsync(new CampaignAccess(dm.CampaignId, PlayerUser, CampaignRole.Player), "party", cover));
        Assert.Null(await service.VersionsAsync(dm, "party", KeyOf((await SlotsAsync(dm, BookAudience.Dm)).Slots, BookSlots.Entry, "a-hidden")));
        var tray = (await service.TrayAsync(dm, "party", "en"))!;
        Assert.Equal(1, tray.Cover.Kept);
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
