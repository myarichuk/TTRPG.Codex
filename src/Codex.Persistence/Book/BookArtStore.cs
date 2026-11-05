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

public interface IBookArtRepository
{
    /// <summary>The campaign's book art; null for anyone who is not its GM now.</summary>
    Task<BookArtDocument?> GetAsync(CampaignAccess dm);

    /// <summary>Puts <paramref name="image"/> (already through <see cref="BookImageSanitizer"/>) in a slot for one edition, replacing what was there.
    /// False when the caller is not the GM now, the key or edition is malformed, or the campaign holds too many pictures.</summary>
    Task<bool> PutAsync(CampaignAccess dm, string slot, string builtFrom, string label, BookImage image, string origin = "upload");

    Task<bool> RemoveAsync(CampaignAccess dm, string slot, string builtFrom);

    Task DeleteAllForCampaignAsync(string campaignId);
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
