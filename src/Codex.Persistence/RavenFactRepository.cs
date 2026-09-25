using Raven.Client.Documents;

namespace Codex.Persistence;

public class FactRepository(RavenDbService dbService) : IFactRepository
{
    public async Task<IEnumerable<FactDocument>> GetVisibleForCampaignAsync(CampaignAccess access, IReadOnlySet<string> ownedActorIds)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var facts = await session.Query<FactDocument, FactsByCampaignIndex>()
            .Where(f => f.CampaignId == access.CampaignId)
            .ToListAsync();

        if (access.IsDm)
        {
            return facts;
        }

        return facts.Where(f => f.Visibility == FactVisibility.Public ||
            (f.Visibility == FactVisibility.KnowersOnly && f.KnownBy.Any(k => ownedActorIds.Contains(k.EntityId))));
    }

    public async Task<bool> SaveAsync(FactDocument fact, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(fact);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetVisibilityAsync(string factId, FactVisibility visibility, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var fact = await session.LoadAsync<FactDocument>(factId);
        if (fact == null || fact.CampaignId != access.CampaignId)
        {
            return false;
        }

        fact.Visibility = visibility;
        await session.SaveChangesAsync();
        return true;
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<FactDocument, FactsByCampaignIndex>()
            .Where(f => f.CampaignId == campaignId)
            .ToListAsync();

        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }
}
