using Codex.Core;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;

namespace Codex.Persistence;

public enum WikiVisibility
{
    /// <summary>Only the DM can open the page. Links to it from party pages render as plain text for players.</summary>
    DmOnly,

    /// <summary>Every campaign member can read it.</summary>
    Party
}

/// <summary>What a page is about. A person can be promoted to the cast and a place to the world map; the page then links to
/// what it became (<see cref="WikiPageDocument.ActorId"/>, <see cref="WikiPageDocument.PlaceId"/>).</summary>
public enum WikiPageKind
{
    General,
    Person,
    Place,
    Faction,
    Quest
}

/// <summary>
/// One page of a campaign's world wiki. Pages link to each other with <c>[[Page title]]</c>;
/// <see cref="Links"/> is the normalised list of slugs the body points at, kept up to date on
/// save so backlinks are one query. The DM writes pages; players comment with notes
/// (<see cref="NoteDocument"/> with target <c>wiki:{slug}</c>).
/// </summary>
public class WikiPageDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;

    /// <summary>URL-safe, unique within the campaign; derived from the title on first save.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public WikiVisibility Visibility { get; set; } = WikiVisibility.DmOnly;
    public List<string> Tags { get; set; } = new();
    public WikiPageKind Kind { get; set; } = WikiPageKind.General;

    /// <summary>The cast member this page is about (<see cref="ActorDocument.Id"/>), or empty. Set when the page is promoted to
    /// the cast; the texts are copied once, not kept in step.</summary>
    public string? ActorId { get; set; }

    /// <summary>The place on the world map this page is about (<see cref="LocationDocument.Id"/>), or empty. The page shows the
    /// place's picture, and sharing the page reveals the place.</summary>
    public string? PlaceId { get; set; }

    /// <summary>Slugs of the pages this one links to.</summary>
    public List<string> Links { get; set; } = new();

    /// <summary>True once the page's illustration is stored as its "art" attachment; <see cref="ArtVersion"/> busts caches. A page
    /// about a place shows the place's picture instead, so a place never has two.</summary>
    public bool HasArt { get; set; }
    public int ArtVersion { get; set; }

    public string AuthorUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public interface IWikiRepository
{
    /// <summary>Pages the caller may read: all of them for the DM, Party ones for everyone else.</summary>
    Task<IEnumerable<WikiPageDocument>> GetVisibleForCampaignAsync(CampaignAccess access);

    Task<WikiPageDocument?> GetVisibleBySlugAsync(string slug, CampaignAccess access);

    /// <summary>Creates or updates a page. DM only. Assigns the slug on first save and refreshes <see cref="WikiPageDocument.Links"/>.</summary>
    Task<bool> TrySaveAsync(WikiPageDocument page, CampaignAccess access);

    Task<bool> TryDeleteAsync(string id, CampaignAccess access);

    /// <summary>Records that the page's illustration was stored (or removed) and bumps its version. DM only.</summary>
    Task<bool> SetArtAsync(string id, bool hasArt, CampaignAccess access);

    /// <summary>Every page regardless of visibility - export only.</summary>
    Task<IEnumerable<WikiPageDocument>> GetAllForCampaignAsync(string campaignId);

    Task SaveAsync(WikiPageDocument page);
    Task DeleteAllForCampaignAsync(string campaignId);
}

public class WikiByCampaignIndex : AbstractIndexCreationTask<WikiPageDocument>
{
    public WikiByCampaignIndex()
    {
        Map = pages => from page in pages
                       select new
                       {
                           page.CampaignId,
                           page.Slug,
                           page.Visibility,
                           page.Links,
                           page.UpdatedAt
                       };
    }
}

public class RavenWikiRepository(RavenDbService dbService) : IWikiRepository
{
    public async Task<IEnumerable<WikiPageDocument>> GetVisibleForCampaignAsync(CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var query = session.Query<WikiPageDocument, WikiByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(p => p.CampaignId == access.CampaignId);
        if (!access.IsDm)
        {
            query = query.Where(p => p.Visibility == WikiVisibility.Party);
        }

        return (await query.ToListAsync()).OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<WikiPageDocument?> GetVisibleBySlugAsync(string slug, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var page = await session.Query<WikiPageDocument, WikiByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .FirstOrDefaultAsync(p => p.CampaignId == access.CampaignId && p.Slug == slug);
        if (page == null)
        {
            return null;
        }

        return access.IsDm || page.Visibility == WikiVisibility.Party ? page : null;
    }

    public async Task<bool> TrySaveAsync(WikiPageDocument page, CampaignAccess access)
    {
        if (!access.IsDm || string.IsNullOrWhiteSpace(page.Title))
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var existing = string.IsNullOrEmpty(page.Id) ? null : await session.LoadAsync<WikiPageDocument>(page.Id);
        if (existing != null && existing.CampaignId != access.CampaignId)
        {
            return false;
        }

        page.CampaignId = access.CampaignId;
        page.Title = page.Title.Trim();
        page.Links = WikiText.ExtractLinks(page.Body).Select(WikiText.Slugify).Distinct().ToList();
        page.UpdatedAt = DateTime.UtcNow;

        if (existing == null)
        {
            page.Id = string.IsNullOrEmpty(page.Id) ? Guid.NewGuid().ToString() : page.Id;
            page.CreatedAt = DateTime.UtcNow;
            page.AuthorUserId = string.IsNullOrEmpty(page.AuthorUserId) ? access.UserId : page.AuthorUserId;
            if (string.IsNullOrWhiteSpace(page.Slug))
            {
                page.Slug = await UniqueSlugAsync(session, access.CampaignId, WikiText.Slugify(page.Title));
            }

            await session.StoreAsync(page);
        }
        else
        {
            existing.Title = page.Title;
            existing.Body = page.Body;
            existing.Visibility = page.Visibility;
            existing.Tags = page.Tags;
            existing.Kind = page.Kind;
            existing.ActorId = page.ActorId;
            existing.PlaceId = page.PlaceId;
            existing.Links = page.Links;
            existing.UpdatedAt = page.UpdatedAt;
            page.Slug = existing.Slug;
        }

        await session.SaveChangesAsync();
        return true;
    }

    private static async Task<string> UniqueSlugAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string campaignId, string slug)
    {
        var taken = await session.Query<WikiPageDocument, WikiByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(p => p.CampaignId == campaignId)
            .Select(p => p.Slug)
            .ToListAsync();
        var set = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(slug))
        {
            return slug;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{slug}-{i}";
            if (!set.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    public async Task<bool> SetArtAsync(string id, bool hasArt, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var page = await session.LoadAsync<WikiPageDocument>(id);
        if (page == null || page.CampaignId != access.CampaignId)
        {
            return false;
        }

        page.HasArt = hasArt;
        page.ArtVersion++;
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> TryDeleteAsync(string id, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var page = await session.LoadAsync<WikiPageDocument>(id);
        if (page == null || page.CampaignId != access.CampaignId)
        {
            return false;
        }

        session.Delete(page);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<IEnumerable<WikiPageDocument>> GetAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<WikiPageDocument, WikiByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(p => p.CampaignId == campaignId)
            .ToListAsync();
    }

    public async Task SaveAsync(WikiPageDocument page)
    {
        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(page);
        await session.SaveChangesAsync();
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<WikiPageDocument, WikiByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(p => p.CampaignId == campaignId)
            .ToListAsync();
        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }
}
