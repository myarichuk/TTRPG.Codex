using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Codex.Persistence;

public class ActorRepository(RavenDbService dbService) : IActorRepository
{
    public async Task<ActorDocument?> GetVisibleAsync(string actorId, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var actor = await session.LoadAsync<ActorDocument>(actorId);
        if (actor == null || actor.CampaignId != access.CampaignId)
        {
            return null;
        }

        return CanSee(actor, access) ? actor : null;
    }

    public async Task<IEnumerable<ActorDocument>> GetVisibleForCampaignAsync(CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var query = session.Query<ActorDocument, ActorsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(a => a.CampaignId == access.CampaignId);

        if (!access.IsDm)
        {
            // Filters in the Raven query itself (1.4): a Hidden or someone-else's-Unknown actor
            // never leaves the database for a player's session, rather than being loaded and
            // filtered out in Razor after the fact.
            query = query.Where(a => a.Visibility == ActorVisibility.Known || a.OwnerUserId == access.UserId);
        }

        return await query.ToListAsync();
    }

    public async Task SaveAsync(ActorDocument actor)
    {
        using var session = dbService.Store.OpenAsyncSession();

        actor.UpdatedAt = DateTime.UtcNow;
        if (string.IsNullOrEmpty(actor.Id))
        {
            actor.CreatedAt = DateTime.UtcNow;
        }

        await session.StoreAsync(actor);
        await session.SaveChangesAsync();
    }

    public async Task<bool> TrySaveAsync(ActorDocument actor, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var existing = string.IsNullOrEmpty(actor.Id) ? null : await session.LoadAsync<ActorDocument>(actor.Id);

        if (existing == null)
        {
            if (access.IsDm)
            {
                actor.CampaignId = access.CampaignId;
            }
            else if (actor.OwnerUserId != access.UserId || actor.CampaignId != access.CampaignId)
            {
                return false;
            }

            actor.CreatedAt = DateTime.UtcNow;
            actor.UpdatedAt = DateTime.UtcNow;
            await session.StoreAsync(actor);
            await session.SaveChangesAsync();
            return true;
        }

        if (existing.CampaignId != access.CampaignId)
        {
            return false;
        }

        if (!access.IsDm && existing.OwnerUserId != access.UserId)
        {
            return false;
        }

        // Copy onto the tracked instance (the passed object is detached): CampaignId never
        // moves, and only the DM can change ownership - a player's OwnerUserId is ignored.
        existing.Name = actor.Name;
        existing.Kind = actor.Kind;
        existing.Visibility = actor.Visibility;
        existing.PublicName = actor.PublicName;
        existing.PublicDescription = actor.PublicDescription;
        existing.BlueprintId = actor.BlueprintId;
        existing.State = actor.State;
        if (access.IsDm)
        {
            existing.OwnerUserId = actor.OwnerUserId;
        }

        existing.UpdatedAt = DateTime.UtcNow;
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> TryDeleteAsync(string actorId, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var actor = await session.LoadAsync<ActorDocument>(actorId);
        if (actor == null || actor.CampaignId != access.CampaignId)
        {
            return false;
        }

        session.Delete(actorId);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<ActorDocument, ActorsByCampaignIndex>()
            .Where(a => a.CampaignId == campaignId)
            .ToListAsync();

        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }

    public async Task<IEnumerable<ActorDocument>> GetRecentForOwnerAsync(string ownerUserId, int limit, bool sortByCreated)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var query = session.Query<ActorDocument, ActorsByCampaignIndex>()
            .Where(a => a.OwnerUserId == ownerUserId);

        query = sortByCreated
            ? query.OrderByDescending(a => a.CreatedAt)
            : query.OrderByDescending(a => a.UpdatedAt);

        return await query.Take(limit).ToListAsync();
    }

    private static bool CanSee(ActorDocument actor, CampaignAccess access) =>
        access.IsDm || actor.Visibility == ActorVisibility.Known || actor.OwnerUserId == access.UserId;
}
