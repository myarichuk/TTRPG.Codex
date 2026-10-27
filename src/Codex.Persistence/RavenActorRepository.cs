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

        return CanSee(actor, access) ? Redact(actor, access) : null;
    }

    /// <summary>The DM's private notes never reach a player's circuit, even for an actor they own.</summary>
    private static ActorDocument Redact(ActorDocument actor, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            actor.DmNotes = null;
        }

        return actor;
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

        return (await query.ToListAsync()).Select(a => Redact(a, access)).ToList();
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

    public Task<ActorDocument?> UpdateAsync(string actorId, Action<ActorDocument> mutate) =>
        RavenUpdate.RunAsync(dbService, actorId, mutate);

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
        CopyEditableFields(existing, actor, access.IsDm);
        await session.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// The fields a sheet save may change, copied from <paramref name="source"/> onto
    /// <paramref name="target"/>. CampaignId never moves; only the DM can change ownership and DM
    /// notes (a player's save arrives with the notes redacted to null and leaves them alone).
    /// </summary>
    internal static void CopyEditableFields(ActorDocument target, ActorDocument source, bool isDm)
    {
        target.Name = source.Name;
        target.Kind = source.Kind;
        target.Visibility = source.Visibility;
        target.PublicName = source.PublicName;
        target.PublicDescription = source.PublicDescription;
        target.BlueprintId = source.BlueprintId;
        target.Sheet = source.Sheet;
        target.State = source.State;
        target.Biography = source.Biography;
        target.HasPortrait = source.HasPortrait;
        target.PortraitVersion = source.PortraitVersion;
        if (isDm)
        {
            target.OwnerUserId = source.OwnerUserId;
            target.DmNotes = source.DmNotes;
            target.WikiPageId = source.WikiPageId;
        }

        target.UpdatedAt = DateTime.UtcNow;
    }

    public async Task<bool> TryDeleteAsync(string actorId, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var actor = await session.LoadAsync<ActorDocument>(actorId);
        if (actor == null || actor.CampaignId != access.CampaignId)
        {
            return false;
        }

        // The DM deletes anything; a player may retire their own player character and nothing else.
        var ownsPlayerCharacter = actor.Kind == ActorKind.PlayerCharacter && actor.OwnerUserId == access.UserId;
        if (!access.IsDm && !ownsPlayerCharacter)
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
