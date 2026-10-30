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

    /// <summary>Reveals block <paramref name="block"/> of a page's body to the party (<see cref="SecretAudience.Party"/>), to the
    /// characters <paramref name="knowers"/> (<see cref="SecretAudience.Some"/>), or hides it again (<see cref="SecretAudience.Dm"/>);
    /// see <see cref="SecretText.SetAudience"/>. <paramref name="expectedInner"/> is the block's text as the DM saw it. DM only.</summary>
    Task<SecretChange> TrySetSecretAsync(string pageId, int block, string expectedInner, SecretAudience to, IReadOnlyList<string>? knowers, CampaignAccess access);

    /// <summary>Puts <paramref name="version"/> beside DM block <paramref name="block"/> as what the players are told
    /// (a <c>:::players</c> block right after it, replacing the one there); see <see cref="SecretText.SetPlayersVersion"/>.
    /// <paramref name="expectedInner"/> is the block's text as the DM saw it. DM only.</summary>
    Task<SecretChange> TrySetPlayersVersionAsync(string pageId, int block, string expectedInner, string version, CampaignAccess access);

    /// <summary>Makes the page the page of cast member <paramref name="actorId"/>: sets its kind and its <see cref="WikiPageDocument.ActorId"/>
    /// and nothing else, on the page as stored, so an edit or a reveal made meanwhile is kept. A page already linked to another
    /// character keeps that link unless it is <paramref name="replacing"/> (one the caller found gone). Returns the character the
    /// page is linked to afterwards, or null when there is no such page or the caller is not the DM. DM only.</summary>
    Task<string?> TryLinkActorAsync(string pageId, string actorId, WikiPageKind kind, string? replacing, CampaignAccess access);

    /// <summary>Records that the page's illustration was stored (or removed) and bumps its version. DM only.</summary>
    Task<bool> SetArtAsync(string id, bool hasArt, CampaignAccess access);

    /// <summary>Every page regardless of visibility - export only.</summary>
    Task<IEnumerable<WikiPageDocument>> GetAllForCampaignAsync(string campaignId);

    Task SaveAsync(WikiPageDocument page);
    Task DeleteAllForCampaignAsync(string campaignId);
}

/// <summary>How asking to reveal or hide a block of a page went.</summary>
public enum SecretChange
{
    Done,

    /// <summary>The block is not what the DM saw: the page was edited since, or the block is gone. Nothing changed.</summary>
    Stale,

    /// <summary>Not the DM's, no such page, or a block that cannot be changed that way. Nothing changed.</summary>
    Refused
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

        var pages = (await query.ToListAsync()).OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
        if (!access.IsDm)
        {
            var visible = pages.Select(p => p.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var owned = await OwnedActors.OfAsync(session, access);
            foreach (var page in pages)
            {
                RedactForPlayer(page, visible, owned);
            }
        }

        return pages;
    }

    // Pages handed to a player with their secret blocks taken out. A page in here is never saved, which would write the
    // player's copy over the DM's text; only the DM saves, and the DM is never given one, so this only guards the future.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WikiPageDocument, object> Redacted = new();

    /// <summary>The page as a player owning <paramref name="ownedActorIds"/> reads it: the body without the blocks they may not
    /// read (revealed blocks keep their markers and time, so the page can show them as new), and links only to what that body
    /// links to (and, when <paramref name="visibleSlugs"/> is given, only to pages the player may open), so neither the link
    /// count nor "pages linking here" points at a secret.</summary>
    private static void RedactForPlayer(WikiPageDocument page, IReadOnlySet<string>? visibleSlugs, IReadOnlySet<string> ownedActorIds)
    {
        page.Body = SecretText.ForPlayersMarked(page.Body, ownedActorIds);
        page.Links = WikiText.ExtractLinks(page.Body).Select(WikiText.Slugify).Distinct()
            .Where(slug => visibleSlugs == null || visibleSlugs.Contains(slug))
            .ToList();
        Redacted.AddOrUpdate(page, page);
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

        if (access.IsDm)
        {
            return page;
        }

        if (page.Visibility != WikiVisibility.Party)
        {
            return null;
        }

        RedactForPlayer(page, null, await OwnedActors.OfAsync(session, access));
        return page;
    }

    public async Task<bool> TrySaveAsync(WikiPageDocument page, CampaignAccess access)
    {
        if (!access.IsDm || string.IsNullOrWhiteSpace(page.Title) || Redacted.TryGetValue(page, out _))
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

    public async Task<SecretChange> TrySetSecretAsync(string pageId, int block, string expectedInner, SecretAudience to, IReadOnlyList<string>? knowers, CampaignAccess access)
    {
        if (!access.IsDm || to is not (SecretAudience.Party or SecretAudience.Some or SecretAudience.Dm))
        {
            return SecretChange.Refused;
        }

        var result = SecretChange.Refused;
        // Under optimistic concurrency, against a fresh load each attempt: a save of the page in between is either seen here
        // (and the block checked against it) or makes this go again, so a reveal never writes over an edit.
        await RavenUpdate.RunAsync<WikiPageDocument>(dbService, pageId, page =>
        {
            result = SecretChange.Refused;
            if (page.CampaignId != access.CampaignId)
            {
                return;
            }

            var found = SecretText.Blocks(page.Body).FirstOrDefault(b => b.Block == block);
            if (found == null || found.Inner != expectedInner)
            {
                result = SecretChange.Stale;
                return;
            }

            if (SecretText.SetAudience(page.Body, block, expectedInner, to, knowers, DateTime.UtcNow) is not { } body)
            {
                return;
            }

            page.Body = body;
            page.Links = WikiText.ExtractLinks(page.Body).Select(WikiText.Slugify).Distinct().ToList();
            page.UpdatedAt = DateTime.UtcNow;
            result = SecretChange.Done;
        });
        return result;
    }

    public async Task<string?> TryLinkActorAsync(string pageId, string actorId, WikiPageKind kind, string? replacing, CampaignAccess access)
    {
        if (!access.IsDm || string.IsNullOrEmpty(actorId))
        {
            return null;
        }

        string? linked = null;
        await RavenUpdate.RunAsync<WikiPageDocument>(dbService, pageId, page =>
        {
            linked = null;
            if (page.CampaignId != access.CampaignId)
            {
                return;
            }

            if (!string.IsNullOrEmpty(page.ActorId) && page.ActorId != replacing)
            {
                linked = page.ActorId;
                return;
            }

            page.Kind = kind;
            page.ActorId = actorId;
            page.UpdatedAt = DateTime.UtcNow;
            linked = actorId;
        });
        return linked;
    }

    public async Task<SecretChange> TrySetPlayersVersionAsync(string pageId, int block, string expectedInner, string version, CampaignAccess access)
    {
        if (!access.IsDm || string.IsNullOrWhiteSpace(version))
        {
            return SecretChange.Refused;
        }

        var result = SecretChange.Refused;
        // As TrySetSecretAsync: against a fresh load each attempt, so the version never lands beside a block edited since.
        await RavenUpdate.RunAsync<WikiPageDocument>(dbService, pageId, page =>
        {
            result = SecretChange.Refused;
            if (page.CampaignId != access.CampaignId)
            {
                return;
            }

            var found = SecretText.Blocks(page.Body).FirstOrDefault(b => b.Block == block);
            if (found == null || found.Inner != expectedInner)
            {
                result = SecretChange.Stale;
                return;
            }

            if (found.Marked != SecretAudience.Dm || SecretText.SetPlayersVersion(page.Body, block, expectedInner, version) is not { } body)
            {
                return;
            }

            page.Body = body;
            page.Links = WikiText.ExtractLinks(page.Body).Select(WikiText.Slugify).Distinct().ToList();
            page.UpdatedAt = DateTime.UtcNow;
            result = SecretChange.Done;
        });
        return result;
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
