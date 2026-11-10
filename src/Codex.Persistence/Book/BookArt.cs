using Codex.Core.Book;

namespace Codex.Persistence;

/// <summary>Why one picture is not in a book, and the key of the slot it was for (a hash, never a raw id), so the editor can name the
/// entry by the label this edition gives it.</summary>
public sealed record BookArtSkipped(string Subject, string Reason, string? Slot = null);

/// <summary>
/// The pictures a book may use, each under an opaque id (<c>a1</c>, <c>a2</c>…) that the renderer's <c>asset:</c> targets name.
/// Ids resolve only through this map: a Raven id (guessable) never addresses an image, and an id from text a model wrote that is not
/// in the map renders as a placeholder, not as someone else's picture.
/// </summary>
public sealed class BookArt
{
    private readonly Dictionary<string, BookImage> _byAsset = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Kind, string Owner, string Sub), string> _ids = new();
    private readonly List<BookArtSkipped> _skipped = new();

    public static readonly BookArt None = new();

    public int Count => _byAsset.Count;

    public IReadOnlyList<BookArtSkipped> Skipped => _skipped;

    /// <summary>The <c>data:</c> URI behind an asset id, or null when there is none: the delegate for <c>BookMarkdownOptions.Image</c>.</summary>
    public string? DataUri(string assetId) => _byAsset.TryGetValue(assetId, out var image) ? image.DataUri() : null;

    /// <summary>Every picture the book holds, for the size accounting and the tests.</summary>
    public IEnumerable<BookImage> AllImages() => _byAsset.Values;

    public BookImage? Image(string assetId) => _byAsset.GetValueOrDefault(assetId);

    /// <summary>The asset id of the cover, an actor's portrait, a wiki page's, a place's or a homebrew entry's picture, or null.</summary>
    public string? Cover() => _ids.GetValueOrDefault(("cover", string.Empty, string.Empty));

    public string? Portrait(string actorId) => _ids.GetValueOrDefault(("portrait", actorId, string.Empty));

    public string? Wiki(string pageId) => _ids.GetValueOrDefault(("wiki", pageId, string.Empty));

    public string? Location(string regionId, string locationId) => _ids.GetValueOrDefault(("location", regionId, locationId));

    public string? Homebrew(string entryId) => _ids.GetValueOrDefault(("homebrew", entryId, string.Empty));

    /// <summary>The picture the GM put in a slot for the book, as <paramref name="audience"/> may have it: the party only one made for the
    /// party, the GM their own or else the party's. The tag is checked here as well as when the pictures were chosen, so a map read for
    /// the GM still gives a party entry nothing that was made for the GM.</summary>
    public string? Slot(string key, BookAudience audience)
    {
        var party = _ids.GetValueOrDefault(("slot", key, BookArtDocument.Party));
        return audience == BookAudience.Party ? party : _ids.GetValueOrDefault(("slot", key, BookArtDocument.Dm)) ?? party;
    }

    internal string Add(string kind, string owner, string sub, BookImage image)
    {
        var id = "a" + (_byAsset.Count + 1);
        _byAsset[id] = image;
        _ids[(kind, owner, sub)] = id;
        return id;
    }

    internal void Skip(string subject, string reason, string? slot = null) => _skipped.Add(new BookArtSkipped(subject, reason, slot));
}

/// <summary>
/// Reads the pictures for a <see cref="BookSource"/>. It asks the media store only for documents that are already in the source,
/// which was read through the access-scoped repositories: the source's actors are the ones this audience may see (a stranger's mask
/// has no portrait flag, a hidden actor is not there at all), its wiki pages, regions' places and homebrew entries likewise. Nothing
/// is fetched by an id that came from anywhere else, so a picture the audience may not see is never read, not read and then dropped.
/// Bytes pass through <see cref="BookImageSanitizer"/> (metadata out, type from the bytes, size checked), then <see cref="BookImageScaler"/>
/// (no bigger than the page can show), and the whole is capped.
/// <para>The cap is decided the same way for the preview and the print (docs/BOOK_EXPORT.md 31.1, item 4): each picture is counted at the
/// size it would print at, from its pixels and before anything is decoded (<see cref="BookImageScaler.PrintEstimate"/>), in the same order,
/// so the preview leaves out exactly the pictures the PDF leaves out. The print also checks what it really holds against the cap, a
/// backstop for a picture that comes out larger than its estimate.</para>
/// </summary>
public sealed class BookArtReader(IMediaStore media, BookImageScaler? scaler = null, long maxTotalBytes = BookArtReader.MaxTotalBytes)
{
    public const int MaxImages = 120;

    public const long MaxTotalBytes = 64L * 1024 * 1024;

    /// <param name="book">The GM's book pictures chosen for this edition's slots (<see cref="BookSlots.Choose"/>); they come first, and an entry or
    /// cover that has one is not read from its own media.</param>
    /// <param name="forUser">The user the book is read for: the decoder's places are shared fairly between users.</param>
    public async Task<BookArt> ReadAsync(BookSource source, BookArtQuality quality = BookArtQuality.Print, int columns = 2, CancellationToken ct = default,
        IReadOnlyList<BookSlotPicture>? book = null, string? forUser = null)
    {
        var art = new BookArt();
        long total = 0;
        long counted = 0;

        async Task TryAddAsync(string kind, string owner, string sub, string subject, string slot, string documentId, string name, string? scaleKind = null)
        {
            if (art.Count >= MaxImages)
            {
                art.Skip(subject, "limit", slot);
                return;
            }

            var file = await media.GetAsync(documentId, name);
            if (file == null)
            {
                art.Skip(subject, "missing", slot);
                return;
            }

            if (file.Bytes.Length > RavenMediaStore.MaxBytes)
            {
                art.Skip(subject, "too-large", slot);
                return;
            }

            var problem = BookImageSanitizer.TrySanitize(file.Bytes, out var image);
            if (problem != BookImageProblem.None || image == null)
            {
                art.Skip(subject, problem.ToString().ToLowerInvariant(), slot);
                return;
            }

            // Counted at what its box prints at, whichever quality this is: the cap counts what the PDF places, not what was uploaded, and
            // the preview leaves out what the PDF would.
            var estimate = scaler == null ? image.Bytes.Length : BookImageScaler.PrintEstimate(image, scaleKind ?? kind, columns);
            if (counted + estimate > maxTotalBytes)
            {
                art.Skip(subject, "limit", slot);
                return;
            }

            if (scaler != null)
            {
                image = await scaler.ScaleAsync(image, scaleKind ?? kind, quality, ct, columns, forUser);
            }

            if (quality == BookArtQuality.Print && total + image.Bytes.Length > maxTotalBytes)
            {
                art.Skip(subject, "limit", slot);
                return;
            }

            counted += estimate;
            total += image.Bytes.Length;
            art.Add(kind, owner, sub, image);
        }

        // Only pictures for this source's own audience and slots: the tag and the slot's audience are checked again, whoever chose them.
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chosen in book ?? [])
        {
            var picture = chosen.Picture;
            if (chosen.Slot.Audience != source.Audience || picture.Slot != chosen.Slot.Key
                || !(picture.BuiltFrom == BookArtDocument.Party || picture.BuiltFrom == BookArtDocument.Dm && source.Audience == BookAudience.Dm))
            {
                continue;
            }

            covered.Add(chosen.Slot.Key);
            await TryAddAsync("slot", chosen.Slot.Key, picture.BuiltFrom, "slot:" + chosen.Slot.Key, chosen.Slot.Key, BookArtDocument.IdFor(source.Campaign.Id), picture.Attachment, chosen.Slot.ScaleKind);
        }

        // The cover is what any member of the campaign sees on the overview, so both editions may carry it.
        var coverKey = BookSlots.KeyFor(BookSlots.CoverSubject);
        if (source.Campaign.HasCover && !covered.Contains(coverKey))
        {
            await TryAddAsync("cover", string.Empty, string.Empty, "cover", coverKey, source.Campaign.Id, RavenMediaStore.CampaignCover);
        }

        foreach (var actor in source.Actors.Where(a => a.HasPortrait))
        {
            var key = BookSlots.KeyFor(BookSlots.ActorSubject(source.Audience, actor));
            if (covered.Contains(key)) continue;
            await TryAddAsync("portrait", actor.Id, string.Empty, "portrait:" + actor.Id, key, actor.Id, RavenMediaStore.ActorPortrait);
        }

        foreach (var page in source.Wiki.Where(p => p.HasArt))
        {
            var key = BookSlots.KeyFor(BookSlots.WikiSubject(page.Id));
            if (covered.Contains(key)) continue;
            await TryAddAsync("wiki", page.Id, string.Empty, "wiki:" + page.Id, key, page.Id, RavenMediaStore.EntryArt);
        }

        foreach (var region in source.Regions)
        {
            foreach (var location in region.Locations.Where(l => l.HasArt))
            {
                var key = BookSlots.KeyFor(BookSlots.LocationSubject(region.Id, location.Id));
                if (covered.Contains(key)) continue;
                await TryAddAsync("location", region.Id, location.Id, "location:" + location.Id, key, region.Id, RavenMediaStore.LocationArtName(location.Id));
            }
        }

        foreach (var entry in source.Homebrew.Where(h => h.HasArt))
        {
            var key = BookSlots.KeyFor(BookSlots.HomebrewSubject(entry.Id));
            if (covered.Contains(key)) continue;
            await TryAddAsync("homebrew", entry.Id, string.Empty, "homebrew:" + entry.Id, key, entry.Id, RavenMediaStore.EntryArt);
        }

        return art;
    }
}
