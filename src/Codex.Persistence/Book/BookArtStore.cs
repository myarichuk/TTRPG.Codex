using Codex.Core.Book;
using Raven.Client.Exceptions;

namespace Codex.Persistence;

/// <summary>
/// The pictures a GM made for the book itself, one document per campaign (<c>bookart/{campaign}</c>) with the pictures as its attachments.
/// It is the GM's: only the campaign's GM, as the campaign stores it now, reads or writes it; no media route serves it (they name fixed
/// attachments of other documents), the campaign export leaves it out, the backup copies its pictures, and it goes with the campaign.
/// <para>Each picture is tagged with the edition it was made for (<see cref="BookArtPicture.BuiltFrom"/>). The party edition places only
/// pictures tagged <see cref="Party"/>: a picture made while looking at the GM's edition can show what the party may not know.</para>
/// </summary>
public class BookArtDocument
{
    public const string Party = "party";
    public const string Dm = "dm";

    /// <summary>A campaign's book art is bounded: the pictures in use and the earlier versions kept beside them, together.</summary>
    public const int MaxPictures = 600;

    /// <summary>The most versions of one slot's picture an edition keeps, the one in use counted (docs/BOOK_EXPORT.md 28.3, item 3: "keep 3").
    /// A new picture past this drops the oldest earlier version, which the pre-flight says before anything is drawn.</summary>
    public const int MaxVersions = 3;

    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public List<BookArtPicture> Pictures { get; set; } = new();

    /// <summary>Earlier versions of a slot's picture, put back by a new one: kept so a picture paid for is not lost, and so the GM can
    /// choose which prints (<see cref="IBookArtRepository.UseAsync"/>). Nothing that builds a book reads this list; only
    /// <see cref="Pictures"/> prints.</summary>
    public List<BookArtPicture> Kept { get; set; } = new();

    /// <summary>What the art director suggested for each slot, per edition. A suggestion carries the tag of the edition whose source it was
    /// made from, and anything drawn from it later inherits that tag, not the tray's it was drawn in.</summary>
    public List<BookArtSuggestion> Suggestions { get; set; } = new();

    /// <summary>What the art director's runs cost, over all runs: tokens, calls, money where prices were known.</summary>
    public long DirectorInputTokens { get; set; }
    public long DirectorOutputTokens { get; set; }
    public int DirectorCalls { get; set; }
    public int DirectorRuns { get; set; }
    public bool DirectorEstimated { get; set; }
    public decimal DirectorCost { get; set; }
    public int DirectorUnpricedRuns { get; set; }
    public string DirectorPriceLabel { get; set; } = string.Empty;

    /// <summary>The look every picture of this book is drawn in, chosen once for the book (both editions). Null until the GM picks one:
    /// <see cref="BookArtStyle.Default"/> is used then.</summary>
    public BookArtStyle? Style { get; set; }

    /// <summary>What drawing pictures has cost, over all runs: pictures drawn, and money where a price per picture was known.</summary>
    public int DrawnPictures { get; set; }
    public decimal DrawCost { get; set; }
    public int DrawUnpricedPictures { get; set; }
    public string DrawPriceLabel { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static string IdFor(string campaignId) => "bookart/" + campaignId;

    public static bool IsEdition(string? builtFrom) => builtFrom is Party or Dm;

    /// <summary>The start of every attachment name a slot's pictures are stored under, for one edition. Never one of the names a media route
    /// serves ("cover", "portrait", "art", ...).</summary>
    public static string AttachmentFor(string slot, string builtFrom) => "book-" + builtFrom + "-" + slot;

    /// <summary>A new picture's own attachment name: the slot's, and a part of its own, so an earlier version keeps its bytes when a new one comes.
    /// Pictures stored before versions were kept have the slot's name alone.</summary>
    public static string NewAttachment(string slot, string builtFrom) =>
        AttachmentFor(slot, builtFrom) + "-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    /// <summary>The form of every attachment name: the slot's, alone or with an 8-digit part.</summary>
    public static bool IsAttachment(string? name, string slot, string builtFrom)
    {
        var start = AttachmentFor(slot, builtFrom);
        if (name == null || !name.StartsWith(start, StringComparison.Ordinal)) return false;
        var rest = name.AsSpan(start.Length);
        return rest.Length == 0 || rest.Length == 9 && rest[0] == '-' && rest[1..].IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    /// <summary>One slot's versions for one edition: the one in use (if any) and the kept ones, newest first.</summary>
    public IEnumerable<BookArtPicture> Versions(string slot, string builtFrom) =>
        Pictures.Where(p => p.Slot == slot && p.BuiltFrom == builtFrom)
            .Concat(Kept.Where(p => p.Slot == slot && p.BuiltFrom == builtFrom).OrderByDescending(p => p.AddedAt));

    /// <summary>The kept version a new picture in this slot would drop, or null when there is room for it.</summary>
    public BookArtPicture? DroppedByNew(string slot, string builtFrom) =>
        Versions(slot, builtFrom).Count() < MaxVersions ? null
            : Kept.Where(p => p.Slot == slot && p.BuiltFrom == builtFrom).OrderBy(p => p.KeptAt ?? p.AddedAt).FirstOrDefault();

    /// <summary>Moves a slot's picture in use (for one edition) among the kept.</summary>
    public void PutBack(string slot, string builtFrom, DateTime at)
    {
        foreach (var used in Pictures.Where(p => p.Slot == slot && p.BuiltFrom == builtFrom).ToList())
        {
            Pictures.Remove(used);
            used.KeptAt = at;
            Kept.Add(used);
        }
    }
}

public class BookArtPicture
{
    /// <summary>The slot's key (<see cref="BookSlots.KeyFor"/>).</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary><see cref="BookArtDocument.Party"/> or <see cref="BookArtDocument.Dm"/>: the edition whose slot it was made for.</summary>
    public string BuiltFrom { get; set; } = string.Empty;

    /// <summary>"upload" for a picture the GM chose, "drawn" for one an image model drew from a suggestion.</summary>
    public string Origin { get; set; } = "upload";

    /// <summary>For a drawn picture: a hash of what it was drawn from (the slot's kind, the visual, the mood and the style), so the tray can say
    /// "drawn from an older description" without the picture being thrown away. Null for an upload.</summary>
    public string? Inputs { get; set; }

    /// <summary>For a drawn picture: the short visual description it was drawn from, to draw it again from.</summary>
    public string? Visual { get; set; }

    public string Attachment { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public long Bytes { get; set; }

    /// <summary>What the slot was called when the picture was put there, for the tray when the slot has since gone.</summary>
    public string Label { get; set; } = string.Empty;

    public string AddedBy { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When it last stopped being the one in use; null while it is. The kept version that went out of use longest ago is the one a new
    /// picture drops.</summary>
    public DateTime? KeptAt { get; set; }
}

public class BookArtSuggestion
{
    public string Slot { get; set; } = string.Empty;
    public string BuiltFrom { get; set; } = string.Empty;

    /// <summary>A hash of what the director was shown for this slot (its heading and excerpt, the prompt version): a suggestion whose hash
    /// differs from the slot's now was made from text that has since changed.</summary>
    public string Inputs { get; set; } = string.Empty;

    public string Depicts { get; set; } = string.Empty;
    public string Visual { get; set; } = string.Empty;
    public string Mood { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateTime MadeAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A run's spend, added to the document's director totals once.</summary>
public sealed record BookArtSpend(long InputTokens, long OutputTokens, int Calls, bool Estimated, decimal? Cost, string? PriceLabel);

public interface IBookArtRepository
{
    /// <summary>The campaign's book art; null for anyone who is not its GM now.</summary>
    Task<BookArtDocument?> GetAsync(CampaignAccess dm);

    /// <summary>Puts <paramref name="image"/> (already through <see cref="BookImageSanitizer"/>) in a slot for one edition, in use from now on. The
    /// picture it puts back is kept as an earlier version; past <see cref="BookArtDocument.MaxVersions"/> the oldest earlier version goes.
    /// False when the caller is not the GM now, the key or edition is malformed, or the campaign holds too many pictures.</summary>
    Task<bool> PutAsync(CampaignAccess dm, string slot, string builtFrom, string label, BookImage image, string origin = "upload", BookArtDrawn? drawn = null);

    /// <summary>Takes a picture out of a slot for one edition. With <paramref name="attachment"/> null, the one in use goes among the kept versions
    /// (it may have been paid for) and none is in use; with an attachment name, that version is deleted for good, bytes and all.</summary>
    Task<bool> RemoveAsync(CampaignAccess dm, string slot, string builtFrom, string? attachment = null);

    /// <summary>Puts a kept version of a slot's picture in use for its edition, and the one in use until now among the kept. Only a version kept
    /// under this slot and this edition: a picture made for the GM's edition can never be put in use in the party's. False when there is none.</summary>
    Task<bool> UseAsync(CampaignAccess dm, string slot, string builtFrom, string attachment);

    Task DeleteAllForCampaignAsync(string campaignId);

    /// <summary>Stores suggestions for one edition, each replacing that edition's suggestion for its slot. False when the caller is not the GM now.</summary>
    /// <paramref name="present"/>, when given, is every slot the edition has now: that edition's suggestions for any other slot are dropped.
    Task<bool> SaveSuggestionsAsync(CampaignAccess dm, string builtFrom, IReadOnlyList<BookArtSuggestion> suggestions, IReadOnlySet<string>? present = null);

    /// <summary>Adds one run's spend to the director totals. False when the caller is not the GM now.</summary>
    Task<bool> AddDirectorSpendAsync(CampaignAccess dm, BookArtSpend spend);

    /// <summary>Sets the book's drawing style, normalised to the vocabulary. False when the caller is not the GM now.</summary>
    Task<bool> SetStyleAsync(CampaignAccess dm, BookArtStyle style);

    /// <summary>Adds one drawing run's pictures to the totals; <paramref name="cost"/> null when no price per picture was set.</summary>
    Task<bool> AddDrawSpendAsync(CampaignAccess dm, int pictures, decimal? cost, string? priceLabel);
}

/// <summary>What a drawn picture was drawn from (<see cref="BookArtPicture.Inputs"/>, <see cref="BookArtPicture.Visual"/>).</summary>
public sealed record BookArtDrawn(string Inputs, string Visual);

public class RavenBookArtRepository(RavenDbService dbService, StorageQuota? quota = null) : IBookArtRepository
{
    private const int MaxAttempts = 8;

    public async Task<BookArtDocument?> GetAsync(CampaignAccess dm)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, dm) is not { IsDm: true } now) return null;
        using var session = dbService.Store.OpenAsyncSession();
        var art = await session.LoadAsync<BookArtDocument>(BookArtDocument.IdFor(now.CampaignId));
        return art != null && art.CampaignId == now.CampaignId ? art : null;
    }

    public async Task<bool> PutAsync(CampaignAccess dm, string slot, string builtFrom, string label, BookImage image, string origin = "upload", BookArtDrawn? drawn = null)
    {
        if (!BookSlots.IsKey(slot) || !BookArtDocument.IsEdition(builtFrom) || image.Bytes.Length == 0 || image.Bytes.Length > RavenMediaStore.MaxBytes
            || !RavenMediaStore.AllowedContentTypes.Contains(image.ContentType))
        {
            return false;
        }

        if (await CampaignAccessResolver.NowAsync(dbService.Store, dm) is not { IsDm: true } now) return false;
        var id = BookArtDocument.IdFor(now.CampaignId);
        var name = BookArtDocument.NewAttachment(slot, builtFrom);
        if (quota != null)
        {
            // The new picture has a name of its own and replaces nothing; what it frees is the oldest version it drops, if it drops one.
            using var sizing = dbService.Store.OpenAsyncSession();
            var current = await sizing.LoadAsync<BookArtDocument>(id);
            var freed = current?.DroppedByNew(slot, builtFrom) is { } dropping ? StorageQuota.SizeOf(sizing, current, dropping.Attachment) : 0;
            await quota.EnsureRoomAsync(now.CampaignId, image.Bytes.Length, freed);
        }

        for (var attempt = 1; ; attempt++)
        {
            using var session = dbService.Store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            var art = await session.LoadAsync<BookArtDocument>(id);
            if (art == null)
            {
                art = new BookArtDocument { Id = id, CampaignId = now.CampaignId };
                await session.StoreAsync(art, id);
            }
            else if (art.CampaignId != now.CampaignId)
            {
                return false;
            }

            // The one in use goes among the kept; past the most versions a slot keeps, the oldest kept one goes, its bytes in the same save.
            if (art.DroppedByNew(slot, builtFrom) is { } dropped)
            {
                art.Kept.Remove(dropped);
                await DeleteAttachmentAsync(session, id, dropped.Attachment);
            }

            art.PutBack(slot, builtFrom, DateTime.UtcNow);
            if (art.Pictures.Count + art.Kept.Count >= BookArtDocument.MaxPictures) return false;
            // A name of its own: one already listed (a one-in-four-billion chance) would have its bytes written over.
            while (art.Pictures.Concat(art.Kept).Any(p => p.Attachment == name)) name = BookArtDocument.NewAttachment(slot, builtFrom);
            art.Pictures.Add(new BookArtPicture
            {
                Slot = slot, BuiltFrom = builtFrom, Origin = origin, Attachment = name, ContentType = image.ContentType,
                Width = image.Width, Height = image.Height, Bytes = image.Bytes.Length, Label = Clip(label), AddedBy = now.UserId,
                Inputs = drawn?.Inputs, Visual = drawn == null ? null : Clip(drawn.Visual, BookImagePrompt.MaxVisualChars)
            });
            art.UpdatedAt = DateTime.UtcNow;
            // The attachment is stored in the same save as the document's list, so the two never disagree.
            session.Advanced.Attachments.Store(id, name, new MemoryStream(image.Bytes, writable: false), image.ContentType);
            try
            {
                await session.SaveChangesAsync();
                return true;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
            }
        }
    }

    public async Task<bool> RemoveAsync(CampaignAccess dm, string slot, string builtFrom, string? attachment = null)
    {
        if (!BookSlots.IsKey(slot) || !BookArtDocument.IsEdition(builtFrom)) return false;
        if (attachment != null && !BookArtDocument.IsAttachment(attachment, slot, builtFrom)) return false;
        if (await CampaignAccessResolver.NowAsync(dbService.Store, dm) is not { IsDm: true } now) return false;
        var id = BookArtDocument.IdFor(now.CampaignId);
        for (var attempt = 1; ; attempt++)
        {
            using var session = dbService.Store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            var art = await session.LoadAsync<BookArtDocument>(id);
            if (art == null || art.CampaignId != now.CampaignId) return true;
            if (attachment == null)
            {
                // Out of the book, not out of the store: it stays a version the GM can put back or delete.
                if (!art.Pictures.Any(p => p.Slot == slot && p.BuiltFrom == builtFrom)) return true;
                art.PutBack(slot, builtFrom, DateTime.UtcNow);
            }
            else
            {
                var gone = art.Pictures.Concat(art.Kept).Where(p => p.Slot == slot && p.BuiltFrom == builtFrom && p.Attachment == attachment).ToList();
                if (gone.Count == 0) return true;
                foreach (var picture in gone)
                {
                    art.Pictures.Remove(picture);
                    art.Kept.Remove(picture);
                    await DeleteAttachmentAsync(session, id, picture.Attachment);
                }
            }

            art.UpdatedAt = DateTime.UtcNow;
            try
            {
                await session.SaveChangesAsync();
                return true;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
            }
        }
    }

    public async Task<bool> UseAsync(CampaignAccess dm, string slot, string builtFrom, string attachment)
    {
        if (!BookSlots.IsKey(slot) || !BookArtDocument.IsEdition(builtFrom) || !BookArtDocument.IsAttachment(attachment, slot, builtFrom)) return false;
        return await MutateAsync(dm, art =>
        {
            // Only a version kept under this very slot and edition: the tag is the version's own, and must be the edition asked for.
            var chosen = art.Kept.FirstOrDefault(p => p.Slot == slot && p.BuiltFrom == builtFrom && p.Attachment == attachment);
            if (chosen == null) return false;
            art.PutBack(slot, builtFrom, DateTime.UtcNow);
            art.Kept.Remove(chosen);
            chosen.KeptAt = null;
            art.Pictures.Add(chosen);
            return true;
        });
    }

    private static async Task DeleteAttachmentAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string id, string name)
    {
        if (await session.Advanced.Attachments.ExistsAsync(id, name)) session.Advanced.Attachments.Delete(id, name);
    }

    public async Task<bool> SaveSuggestionsAsync(CampaignAccess dm, string builtFrom, IReadOnlyList<BookArtSuggestion> suggestions, IReadOnlySet<string>? present = null)
    {
        if (!BookArtDocument.IsEdition(builtFrom) || suggestions.Any(x => !BookSlots.IsKey(x.Slot))) return false;
        return await MutateAsync(dm, art =>
        {
            // Slots that are gone (an entry deleted, hidden or renamed out of this edition) keep no suggestion, so the list stays bounded.
            if (present != null) art.Suggestions.RemoveAll(x => x.BuiltFrom == builtFrom && !present.Contains(x.Slot));
            foreach (var suggestion in suggestions)
            {
                suggestion.BuiltFrom = builtFrom;
                art.Suggestions.RemoveAll(x => x.Slot == suggestion.Slot && x.BuiltFrom == builtFrom);
                art.Suggestions.Add(suggestion);
            }

            // Bounded like the pictures: a slot has at most one suggestion per edition.
            return art.Suggestions.Count <= BookArtDocument.MaxPictures * 2;
        });
    }

    public Task<bool> AddDirectorSpendAsync(CampaignAccess dm, BookArtSpend spend) => MutateAsync(dm, art =>
    {
        art.DirectorRuns++;
        art.DirectorInputTokens += spend.InputTokens;
        art.DirectorOutputTokens += spend.OutputTokens;
        art.DirectorCalls += spend.Calls;
        art.DirectorEstimated |= spend.Estimated;
        if (spend.Calls > 0)
        {
            if (spend.Cost is { } cost) { art.DirectorCost += cost; art.DirectorPriceLabel = spend.PriceLabel ?? art.DirectorPriceLabel; }
            else art.DirectorUnpricedRuns++;
        }

        return true;
    });

    public Task<bool> SetStyleAsync(CampaignAccess dm, BookArtStyle style) => MutateAsync(dm, art =>
    {
        art.Style = BookArtStyles.Normalize(style);
        return true;
    });

    public Task<bool> AddDrawSpendAsync(CampaignAccess dm, int pictures, decimal? cost, string? priceLabel) => MutateAsync(dm, art =>
    {
        if (pictures <= 0) return false;
        art.DrawnPictures += pictures;
        if (cost is { } c) { art.DrawCost += c; art.DrawPriceLabel = priceLabel ?? art.DrawPriceLabel; }
        else art.DrawUnpricedPictures += pictures;
        return true;
    });

    /// <summary>Loads (or creates) the campaign's document, applies <paramref name="mutate"/> and saves under optimistic concurrency, again
    /// on a conflict. <paramref name="mutate"/> returns false to save nothing.</summary>
    private async Task<bool> MutateAsync(CampaignAccess dm, Func<BookArtDocument, bool> mutate)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, dm) is not { IsDm: true } now) return false;
        var id = BookArtDocument.IdFor(now.CampaignId);
        for (var attempt = 1; ; attempt++)
        {
            using var session = dbService.Store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            var art = await session.LoadAsync<BookArtDocument>(id);
            if (art == null)
            {
                art = new BookArtDocument { Id = id, CampaignId = now.CampaignId };
                await session.StoreAsync(art, id);
            }
            else if (art.CampaignId != now.CampaignId)
            {
                return false;
            }

            if (!mutate(art)) return false;
            art.UpdatedAt = DateTime.UtcNow;
            try
            {
                await session.SaveChangesAsync();
                return true;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
            }
        }
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        // Its attachments go with it.
        session.Delete(BookArtDocument.IdFor(campaignId));
        await session.SaveChangesAsync();
    }

    // A drawn picture's visual is kept whole (as long as a prompt may take it): the tray hashes it again to tell whether the picture is
    // current, and a visual cut at a label's length never matched (found on a real model, whose visuals run past 200 characters).
    private static string Clip(string? label, int max = 200)
    {
        var text = (label ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max];
    }
}
