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

    /// <summary>A campaign's book art is bounded: a slot holds at most one picture per edition, and a book has well under this many slots.</summary>
    public const int MaxPictures = 600;

    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public List<BookArtPicture> Pictures { get; set; } = new();

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

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static string IdFor(string campaignId) => "bookart/" + campaignId;

    public static bool IsEdition(string? builtFrom) => builtFrom is Party or Dm;

    /// <summary>The attachment a picture is stored under. Never one of the names a media route serves ("cover", "portrait", "art", ...).</summary>
    public static string AttachmentFor(string slot, string builtFrom) => "book-" + builtFrom + "-" + slot;
}

public class BookArtPicture
{
    /// <summary>The slot's key (<see cref="BookSlots.KeyFor"/>).</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary><see cref="BookArtDocument.Party"/> or <see cref="BookArtDocument.Dm"/>: the edition whose slot it was made for.</summary>
    public string BuiltFrom { get; set; } = string.Empty;

    /// <summary>"upload" for a picture the GM chose; later steps add drawn ones.</summary>
    public string Origin { get; set; } = "upload";

    public string Attachment { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public long Bytes { get; set; }

    /// <summary>What the slot was called when the picture was put there, for the tray when the slot has since gone.</summary>
    public string Label { get; set; } = string.Empty;

    public string AddedBy { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
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

    /// <summary>Puts <paramref name="image"/> (already through <see cref="BookImageSanitizer"/>) in a slot for one edition, replacing what was there.
    /// False when the caller is not the GM now, the key or edition is malformed, or the campaign holds too many pictures.</summary>
    Task<bool> PutAsync(CampaignAccess dm, string slot, string builtFrom, string label, BookImage image, string origin = "upload");

    Task<bool> RemoveAsync(CampaignAccess dm, string slot, string builtFrom);

    Task DeleteAllForCampaignAsync(string campaignId);

    /// <summary>Stores suggestions for one edition, each replacing that edition's suggestion for its slot. False when the caller is not the GM now.</summary>
    /// <paramref name="present"/>, when given, is every slot the edition has now: that edition's suggestions for any other slot are dropped.
    Task<bool> SaveSuggestionsAsync(CampaignAccess dm, string builtFrom, IReadOnlyList<BookArtSuggestion> suggestions, IReadOnlySet<string>? present = null);

    /// <summary>Adds one run's spend to the director totals. False when the caller is not the GM now.</summary>
    Task<bool> AddDirectorSpendAsync(CampaignAccess dm, BookArtSpend spend);
}

public class RavenBookArtRepository(RavenDbService dbService) : IBookArtRepository
{
    private const int MaxAttempts = 8;

    public async Task<BookArtDocument?> GetAsync(CampaignAccess dm)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, dm) is not { IsDm: true } now) return null;
        using var session = dbService.Store.OpenAsyncSession();
        var art = await session.LoadAsync<BookArtDocument>(BookArtDocument.IdFor(now.CampaignId));
        return art != null && art.CampaignId == now.CampaignId ? art : null;
    }

    public async Task<bool> PutAsync(CampaignAccess dm, string slot, string builtFrom, string label, BookImage image, string origin = "upload")
    {
        if (!BookSlots.IsKey(slot) || !BookArtDocument.IsEdition(builtFrom) || image.Bytes.Length == 0 || image.Bytes.Length > RavenMediaStore.MaxBytes
            || !RavenMediaStore.AllowedContentTypes.Contains(image.ContentType))
        {
            return false;
        }

        if (await CampaignAccessResolver.NowAsync(dbService.Store, dm) is not { IsDm: true } now) return false;
        var id = BookArtDocument.IdFor(now.CampaignId);
        var name = BookArtDocument.AttachmentFor(slot, builtFrom);
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

            art.Pictures.RemoveAll(p => p.Slot == slot && p.BuiltFrom == builtFrom);
            if (art.Pictures.Count >= BookArtDocument.MaxPictures) return false;
            art.Pictures.Add(new BookArtPicture
            {
                Slot = slot, BuiltFrom = builtFrom, Origin = origin, Attachment = name, ContentType = image.ContentType,
                Width = image.Width, Height = image.Height, Bytes = image.Bytes.Length, Label = Clip(label), AddedBy = now.UserId
            });
            art.UpdatedAt = DateTime.UtcNow;
            // A new attachment under an existing name replaces it in the same save as the document's list, so the two never disagree.
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

    public async Task<bool> RemoveAsync(CampaignAccess dm, string slot, string builtFrom)
    {
        if (!BookSlots.IsKey(slot) || !BookArtDocument.IsEdition(builtFrom)) return false;
        if (await CampaignAccessResolver.NowAsync(dbService.Store, dm) is not { IsDm: true } now) return false;
        var id = BookArtDocument.IdFor(now.CampaignId);
        for (var attempt = 1; ; attempt++)
        {
            using var session = dbService.Store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            var art = await session.LoadAsync<BookArtDocument>(id);
            if (art == null || art.CampaignId != now.CampaignId) return true;
            if (art.Pictures.RemoveAll(p => p.Slot == slot && p.BuiltFrom == builtFrom) == 0) return true;
            art.UpdatedAt = DateTime.UtcNow;
            var name = BookArtDocument.AttachmentFor(slot, builtFrom);
            if (await session.Advanced.Attachments.ExistsAsync(id, name)) session.Advanced.Attachments.Delete(id, name);
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

    private static string Clip(string? label)
    {
        var text = (label ?? string.Empty).Trim();
        return text.Length <= 200 ? text : text[..200];
    }
}
