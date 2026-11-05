using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Services;

namespace Codex.Web.Book;

/// <summary>One slot as the GM's tray shows it for an edition.</summary>
/// <param name="Own">The subject has its own picture (a portrait, a page's art, the campaign cover) that prints here when the slot is empty.</param>
/// <param name="Placed">The edition whose book picture prints here (<see cref="BookArtDocument.Party"/> or <see cref="BookArtDocument.Dm"/>), or null.</param>
/// <param name="Mine">A picture made for this edition is stored here (the one the GM can remove from this edition's tray).</param>
public sealed record BookTraySlot(string Key, string Kind, string Section, string Label, bool Own, string? Placed, bool Mine);

/// <summary>A chapter's slots, in book order: its header first, then its entries.</summary>
public sealed record BookTrayChapter(string Id, string Title, IReadOnlyList<BookTraySlot> Slots);

public sealed record BookTray(string Edition, BookTraySlot Cover, IReadOnlyList<BookTrayChapter> Chapters);

public enum BookArtUpload
{
    Stored,
    NotAllowed,
    BadImage,
    NoSuchSlot,
    Full
}

/// <summary>
/// The GM's book pictures (docs/BOOK_EXPORT.md 28.3, step 3): the slots of an edition as its own masked source makes them, and putting a picture
/// in one. A key posted from the page is never trusted: the edition's slots are made again from its source and the key must be one of them, so
/// a picture cannot be filed under a party slot the party edition does not have, and the picture is tagged with the edition it was put in.
/// </summary>
public sealed class BookArtService(BookSourceReader reader, IBookArtRepository store, ICampaignAccessResolver resolver)
{
    private static BookAudience? Audience(string edition) => edition switch { "dm" => BookAudience.Dm, "party" => BookAudience.Party, _ => null };

    private async Task<(CampaignAccess Dm, BookSource Source, BookDocument Compiled, IReadOnlyList<BookSlot> Slots)?> SlotsAsync(CampaignAccess asked, string edition, string language)
    {
        if (Audience(edition) is not { } audience) return null;
        if (await resolver.ResolveAsync(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return null;
        if (await reader.ReadAsync(dm, audience) is not { } source) return null;
        var compiled = BookComposer.Compose(source, language, out var entries);
        return (dm, source, compiled, BookSlots.For(source, compiled, entries));
    }

    public async Task<BookTray?> TrayAsync(CampaignAccess asked, string edition, string language)
    {
        if (await SlotsAsync(asked, edition, language) is not { } made) return null;
        var stored = await store.GetAsync(made.Dm);
        var chosen = BookSlots.Choose(made.Slots, stored).ToDictionary(c => c.Slot.Key, c => c.Picture.BuiltFrom, StringComparer.Ordinal);
        var tag = BookSlots.Tag(made.Source.Audience);
        var mine = stored?.Pictures.Where(p => p.BuiltFrom == tag).Select(p => p.Slot).ToHashSet(StringComparer.Ordinal) ?? [];

        BookTraySlot Row(BookSlot slot) => new(slot.Key, slot.Kind, slot.Section, slot.Label,
            slot.Kind == BookSlots.Cover ? made.Source.Campaign.HasCover : slot.Entry?.HasArt == true,
            chosen.GetValueOrDefault(slot.Key), mine.Contains(slot.Key));

        var cover = Row(made.Slots.First(s => s.Kind == BookSlots.Cover));
        var chapters = made.Compiled.Sections.Select(section => new BookTrayChapter(section.Id,
            made.Slots.First(s => s.Kind == BookSlots.Header && s.Section == section.Id).Label,
            Numbered(made.Slots.Where(s => s.Section == section.Id && s.Kind != BookSlots.Cover).Select(Row).ToList()))).ToList();
        return new BookTray(edition, cover, chapters);
    }

    /// <summary>Two entries the edition names alike (two strangers, two "Twin"s) get "(1)", "(2)" in book order, so the GM can tell their slots
    /// apart. The numbers are the tray's only: they come from this edition's labels and nothing else, and never reach the page.</summary>
    private static List<BookTraySlot> Numbered(List<BookTraySlot> rows)
    {
        var repeated = rows.Where(r => r.Kind == BookSlots.Entry).GroupBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (repeated.Count == 0) return rows;
        var seen = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        return rows.Select(r =>
        {
            if (r.Kind != BookSlots.Entry || !repeated.Contains(r.Label)) return r;
            var n = seen[r.Label] = seen.GetValueOrDefault(r.Label) + 1;
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
        return await store.PutAsync(made.Dm, slot.Key, BookSlots.Tag(made.Source.Audience), slot.Label, image)
            ? BookArtUpload.Stored
            : (await store.GetAsync(made.Dm))?.Pictures.Count >= BookArtDocument.MaxPictures ? BookArtUpload.Full : BookArtUpload.NotAllowed;
    }

    /// <summary>Takes this edition's picture out of a slot. Removing needs no slot check: it can only take away.</summary>
    public async Task<bool> RemoveAsync(CampaignAccess asked, string edition, string slotKey)
    {
        if (Audience(edition) is not { } audience) return false;
        if (await resolver.ResolveAsync(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return false;
        return await store.RemoveAsync(dm, slotKey, BookSlots.Tag(audience));
    }
}
