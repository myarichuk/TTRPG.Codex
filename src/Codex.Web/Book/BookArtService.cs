using Codex.Core.Book;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Services;

namespace Codex.Web.Book;

/// <summary>One slot as the GM's tray shows it for an edition.</summary>
/// <param name="Own">The subject has its own picture (a portrait, a page's art, the campaign cover) that prints here when the slot is empty.</param>
/// <param name="Placed">The edition whose book picture prints here (<see cref="BookArtDocument.Party"/> or <see cref="BookArtDocument.Dm"/>), or null.</param>
/// <param name="Mine">A picture made for this edition is stored here (the one the GM can remove from this edition's tray).</param>
/// <param name="Suggestion">The art director's suggestion for this slot, made from this edition's source; never one made from another edition's.</param>
/// <param name="Drawn">This edition's picture here was drawn by a picture model; null for an upload or no picture.</param>
/// <param name="Kept">How many earlier versions of this edition's picture are kept here (docs/BOOK_EXPORT.md 28.3, item 3).</param>
public sealed record BookTraySlot(string Key, string Kind, string Section, string Label, bool Own, string? Placed, bool Mine, BookTraySuggestion? Suggestion = null, BookTrayDrawn? Drawn = null,
    int Kept = 0);

/// <summary>One version of a slot's picture, for the GM to see and choose from. <paramref name="Id"/> is its attachment name, which the store
/// checks against the slot and edition again; <paramref name="Thumbnail"/> is a small data image, null when it could not be read.</summary>
/// <param name="Stale">Drawn from an older description, mood or style than the slot's suggestion now; false for an upload.</param>
public sealed record BookTrayVersion(string Id, bool InUse, string Origin, DateTime AddedAt, string? Thumbnail, bool Stale);

/// <summary>A picture stored for a place this edition no longer has (an entry deleted, hidden, renamed or revealed), in use there or kept. It
/// prints nowhere but counts against the book's cap and the owner's storage, so the tray lists it for the GM to delete.</summary>
public sealed record BookTrayGone(string Slot, string Label, BookTrayVersion Version);

/// <param name="Stale">The slot's text, its suggestion's mood or the book's style changed since the picture was drawn. The picture is kept.</param>
public sealed record BookTrayDrawn(string Visual, bool Stale);

/// <param name="Stale">The slot's text has changed since the suggestion was made.</param>
public sealed record BookTraySuggestion(string Depicts, string Visual, string Mood, bool Stale);

/// <summary>A chapter's slots, in book order: its header first, then its entries.</summary>
public sealed record BookTrayChapter(string Id, string Title, IReadOnlyList<BookTraySlot> Slots);

/// <param name="Guide">The GM's own style guide (41.23), used in place of <paramref name="Style"/> when set.</param>
/// <param name="Gone">How many of this edition's pictures are stored for places it no longer has (<see cref="BookArtService.GoneAsync"/>).</param>
public sealed record BookTray(string Edition, BookTraySlot Cover, IReadOnlyList<BookTrayChapter> Chapters, BookArtStyle Style, string? Guide = null, int Gone = 0);

public enum BookArtUpload
{
    Stored,
    NotAllowed,
    BadImage,
    NoSuchSlot,
    Full,

    /// <summary>The campaign's owner has no room left for it (docs/SERVER_ACCESS_AND_VAULT.md 15).</summary>
    StorageFull
}

/// <summary>
/// The GM's book pictures (docs/BOOK_EXPORT.md 28.3, step 3): the slots of an edition as its own masked source makes them, and putting a picture
/// in one. A key posted from the page is never trusted: the edition's slots are made again from its source and the key must be one of them, so
/// a picture cannot be filed under a party slot the party edition does not have, and the picture is tagged with the edition it was put in.
/// </summary>
public sealed class BookArtService(BookSourceReader reader, IBookArtRepository store, ICampaignAccessResolver resolver, IBookImages? images = null,
    IMediaStore? media = null, BookImageScaler? scaler = null)
{
    private static BookAudience? Audience(string edition) => edition switch { "dm" => BookAudience.Dm, "party" => BookAudience.Party, _ => null };

    private async Task<(CampaignAccess Dm, BookSource Source, BookDocument Compiled, IReadOnlyList<BookEntryPosition> Entries, IReadOnlyList<BookSlot> Slots)?> SlotsAsync(CampaignAccess asked, string edition, string language)
    {
        if (Audience(edition) is not { } audience) return null;
        if (await resolver.ResolveAsync(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return null;
        if (await reader.ReadAsync(dm, audience) is not { } source) return null;
        var compiled = BookComposer.Compose(source, language, out var entries);
        return (dm, source, compiled, entries, BookSlots.For(source, compiled, entries));
    }

    public async Task<BookTray?> TrayAsync(CampaignAccess asked, string edition, string language)
    {
        if (await SlotsAsync(asked, edition, language) is not { } made) return null;
        var stored = await store.GetAsync(made.Dm);
        var chosen = BookSlots.Choose(made.Slots, stored).ToDictionary(c => c.Slot.Key, c => c.Picture.BuiltFrom, StringComparer.Ordinal);
        var tag = BookSlots.Tag(made.Source.Audience);
        var pictures = stored?.Pictures.Where(p => p.BuiltFrom == tag).ToDictionary(p => p.Slot, StringComparer.Ordinal) ?? [];
        var kept = stored?.Kept.Where(p => p.BuiltFrom == tag).GroupBy(p => p.Slot).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal) ?? [];
        // Only this edition's suggestions: one made from the GM's source may describe what the party may not know, even in a slot both share.
        var suggested = stored?.Suggestions.Where(x => x.BuiltFrom == tag).ToDictionary(x => x.Slot, StringComparer.Ordinal) ?? [];
        var outline = suggested.Count == 0 && !pictures.Values.Any(p => p.Origin == "drawn") ? new Dictionary<string, BookOutlineSlot>()
            : BookArtOutline.For(made.Source, made.Compiled, made.Entries, made.Slots).SelectMany(c => c.Slots).ToDictionary(o => o.Slot.Key, StringComparer.Ordinal);
        var style = BookArtStyles.Normalize(stored?.Style);
        var guide = images == null ? null : await images.StyleAsync(made.Dm.UserId);

        BookTraySlot Row(BookSlot slot)
        {
            var here = outline.GetValueOrDefault(slot.Key);
            var idea = suggested.GetValueOrDefault(slot.Key);
            var drawn = pictures.TryGetValue(slot.Key, out var picture) && picture.Origin == "drawn"
                ? new BookTrayDrawn(picture.Visual ?? string.Empty, Stale(picture, here, idea, style, guide))
                : null;
            return new(slot.Key, slot.Kind, slot.Section, slot.Label,
                slot.Kind == BookSlots.Cover ? made.Source.Campaign.HasCover : slot.Entry?.HasArt == true,
                chosen.GetValueOrDefault(slot.Key), pictures.ContainsKey(slot.Key),
                idea == null ? null : new BookTraySuggestion(idea.Depicts, idea.Visual, idea.Mood, here?.Inputs != idea.Inputs), drawn, kept.GetValueOrDefault(slot.Key));
        }

        var cover = Row(made.Slots.First(s => s.Kind == BookSlots.Cover));
        var chapters = made.Compiled.Sections.Select(section => new BookTrayChapter(section.Id,
            made.Slots.First(s => s.Kind == BookSlots.Header && s.Section == section.Id).Label,
            Numbered(made.Slots.Where(s => s.Section == section.Id && s.Kind != BookSlots.Cover).Select(Row)
                    // Each entry's spot right after the entry, in book order.
                    .OrderBy(r => r.Kind == BookSlots.Header ? -1 : EntryOrder(made.Slots, r)).ToList())
                // Numbered before this, so a spot carries its entry's number. A spot is shown once it is more than a possible place:
                // suggested, or holding a picture (in use or kept).
                .Where(r => r.Kind != BookSlots.Spot || r.Suggestion != null || r.Mine || r.Kept > 0).ToList())).ToList();
        var present = made.Slots.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var gone = stored == null ? 0 : stored.Pictures.Concat(stored.Kept).Count(p => p.BuiltFrom == tag && !present.Contains(p.Slot));
        return new BookTray(edition, cover, chapters, style, guide, gone);
    }

    // Where a row sits among its chapter's: an entry's place, and its spot just after it.
    private static int EntryOrder(IReadOnlyList<BookSlot> slots, BookTraySlot row)
    {
        var slot = slots.First(s => s.Key == row.Key);
        return (slot.Entry?.Line ?? 0) * 2 + (slot.Kind == BookSlots.Spot ? 1 : 0);
    }

    // Drawn from an older text when its suggestion is; from an older mood or style when the same visual would hash differently now.
    private static bool Stale(BookArtPicture picture, BookOutlineSlot? here, BookArtSuggestion? idea, BookArtStyle style, string? guide) =>
        idea == null || here == null || here.Inputs != idea.Inputs
        || BookArtDrawing.InputsOf(here.Kind, picture.Visual ?? string.Empty, idea.Mood, style, guide) != picture.Inputs;

    /// <summary>A slot's versions for this edition, the one in use first, each with a small picture to choose by. Null for anyone who is not
    /// the GM now, or a slot this edition does not have.</summary>
    public async Task<IReadOnlyList<BookTrayVersion>?> VersionsAsync(CampaignAccess asked, string edition, string slotKey, string language = "en", CancellationToken ct = default)
    {
        if (!BookSlots.IsKey(slotKey) || await SlotsAsync(asked, edition, language) is not { } made) return null;
        var slot = made.Slots.FirstOrDefault(s => s.Key == slotKey);
        if (slot == null) return null;
        var stored = await store.GetAsync(made.Dm);
        if (stored == null) return [];
        var tag = BookSlots.Tag(made.Source.Audience);
        var versions = stored.Versions(slotKey, tag).ToList();
        if (versions.Count == 0) return [];
        var idea = stored.Suggestions.FirstOrDefault(x => x.Slot == slotKey && x.BuiltFrom == tag);
        var here = versions.Any(v => v.Origin == "drawn")
            ? BookArtOutline.For(made.Source, made.Compiled, made.Entries, made.Slots).SelectMany(c => c.Slots).FirstOrDefault(o => o.Slot.Key == slotKey)
            : null;
        var style = BookArtStyles.Normalize(stored.Style);
        var guide = images == null ? null : await images.StyleAsync(made.Dm.UserId);
        var list = new List<BookTrayVersion>();
        foreach (var version in versions)
        {
            list.Add(new BookTrayVersion(version.Attachment, stored.Pictures.Contains(version), version.Origin, version.AddedAt,
                await ThumbnailAsync(stored.Id, version, slot.ScaleKind, made.Dm.UserId, ct), version.Origin == "drawn" && Stale(version, here, idea, style, guide)));
        }

        return list;
    }

    /// <summary>This edition's pictures stored for places it no longer has, newest first, each with a small picture. Null for anyone who is not
    /// the GM now.</summary>
    public async Task<IReadOnlyList<BookTrayGone>?> GoneAsync(CampaignAccess asked, string edition, string language = "en", CancellationToken ct = default)
    {
        if (await SlotsAsync(asked, edition, language) is not { } made) return null;
        var stored = await store.GetAsync(made.Dm);
        if (stored == null) return [];
        var tag = BookSlots.Tag(made.Source.Audience);
        var present = made.Slots.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var list = new List<BookTrayGone>();
        foreach (var picture in stored.Pictures.Concat(stored.Kept).Where(p => p.BuiltFrom == tag && !present.Contains(p.Slot)).OrderByDescending(p => p.AddedAt))
        {
            var thumb = await ThumbnailAsync(stored.Id, picture, "place", made.Dm.UserId, ct);
            list.Add(new BookTrayGone(picture.Slot, picture.Label, new BookTrayVersion(picture.Attachment, stored.Pictures.Contains(picture), picture.Origin, picture.AddedAt, thumb, false)));
        }

        return list;
    }

    // The picture scaled for the live preview of its box: small enough to send to the page, as the book would show it.
    private async Task<string?> ThumbnailAsync(string documentId, BookArtPicture version, string scaleKind, string userId, CancellationToken ct)
    {
        if (media == null) return null;
        var file = await media.GetAsync(documentId, version.Attachment);
        if (file == null || file.Bytes.Length > RavenMediaStore.MaxBytes) return null;
        if (BookImageSanitizer.TrySanitize(file.Bytes, out var image) != BookImageProblem.None || image == null) return null;
        if (scaler != null) image = await scaler.ScaleAsync(image, scaleKind, BookArtQuality.Preview, ct, owner: userId);
        return image.DataUri();
    }

    /// <summary>Prints a kept version of this edition's picture in a slot in place of the one in use. The store checks the version is this slot's
    /// and this edition's.</summary>
    public async Task<bool> UseAsync(CampaignAccess asked, string edition, string slotKey, string version)
    {
        if (Audience(edition) is not { } audience) return false;
        if (await resolver.ResolveAsync(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return false;
        return await store.UseAsync(dm, slotKey, BookSlots.Tag(audience), version);
    }

    /// <summary>Two entries the edition names alike (two strangers, two "Twin"s) get "(1)", "(2)" in book order, so the GM can tell their slots
    /// apart. The numbers are the tray's only: they come from this edition's labels and nothing else, and never reach the page.</summary>
    private static List<BookTraySlot> Numbered(List<BookTraySlot> rows)
    {
        var repeated = rows.Where(r => r.Kind == BookSlots.Entry).GroupBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (repeated.Count == 0) return rows;
        // Counted per kind: an entry's spot, which follows it, has the entry's number.
        var seen = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        return rows.Select(r =>
        {
            if (r.Kind is not (BookSlots.Entry or BookSlots.Spot) || !repeated.Contains(r.Label)) return r;
            var n = seen[r.Kind + "|" + r.Label] = seen.GetValueOrDefault(r.Kind + "|" + r.Label) + 1;
            return r with { Label = r.Label + " (" + n + ")" };
        }).ToList();
    }

    public async Task<BookArtUpload> UploadAsync(CampaignAccess asked, string edition, string slotKey, byte[] bytes, string contentType, string language = "en")
    {
        if (!BookSlots.IsKey(slotKey)) return BookArtUpload.NoSuchSlot;
        if (ArtService.Problem(contentType, bytes.Length) != null
            || BookImageSanitizer.TrySanitize(bytes, out var image) != BookImageProblem.None || image == null
            || !RavenMediaStore.AllowedContentTypes.Contains(image.ContentType))
        {
            return BookArtUpload.BadImage;
        }

        // The language only names the chapters (the label kept beside the picture); the keys do not depend on it.
        if (await SlotsAsync(asked, edition, language) is not { } made) return BookArtUpload.NotAllowed;
        var slot = made.Slots.FirstOrDefault(s => s.Key == slotKey);
        if (slot == null) return BookArtUpload.NoSuchSlot;
        bool stored;
        try
        {
            stored = await store.PutAsync(made.Dm, slot.Key, BookSlots.Tag(made.Source.Audience), slot.Label, image);
        }
        catch (LocalizedException ex) when (ex.Key == "Service.Storage.Full")
        {
            return BookArtUpload.StorageFull;
        }

        return stored
            ? BookArtUpload.Stored
            : (await store.GetAsync(made.Dm)) is { } full && full.Pictures.Count + full.Kept.Count >= BookArtDocument.MaxPictures ? BookArtUpload.Full : BookArtUpload.NotAllowed;
    }

    /// <summary>Takes this edition's picture out of a slot: the one in use (<paramref name="version"/> null; the kept ones stay) or one version.
    /// Removing needs no slot check: it can only take away.</summary>
    public async Task<bool> RemoveAsync(CampaignAccess asked, string edition, string slotKey, string? version = null)
    {
        if (Audience(edition) is not { } audience) return false;
        if (await resolver.ResolveAsync(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return false;
        return await store.RemoveAsync(dm, slotKey, BookSlots.Tag(audience), version);
    }
}
