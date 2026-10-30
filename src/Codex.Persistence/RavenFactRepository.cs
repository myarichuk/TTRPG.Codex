using Raven.Client.Documents;

namespace Codex.Persistence;

public class FactRepository(RavenDbService dbService) : IFactRepository
{
    public async Task<IEnumerable<FactDocument>> GetVisibleForCampaignAsync(CampaignAccess access, IReadOnlySet<string> ownedActorIds)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var facts = await session.Query<FactDocument, FactsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
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
        var existing = string.IsNullOrEmpty(fact.Id) ? null : await session.LoadAsync<FactDocument>(fact.Id);
        if (existing != null && existing.CampaignId != access.CampaignId)
        {
            return false;
        }

        fact.CampaignId = access.CampaignId;
        fact.Summary = (fact.Summary ?? string.Empty).Trim();
        fact.Details = string.IsNullOrWhiteSpace(fact.Details) ? null : fact.Details.Trim();
        fact.RelatedEntityIds = FactLinks.Of(fact.Summary, fact.Details).ToList();
        if (existing == null)
        {
            await session.StoreAsync(fact, string.IsNullOrEmpty(fact.Id) ? null : fact.Id);
        }
        else
        {
            // The caller's copy is detached; the tracked one takes its fields.
            existing.Summary = fact.Summary;
            existing.Details = fact.Details;
            existing.Visibility = fact.Visibility;
            existing.KnownBy = fact.KnownBy;
            existing.RelatedEntityIds = fact.RelatedEntityIds;
            existing.Metadata = fact.Metadata;
        }

        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(string factId, CampaignAccess access)
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

        session.Delete(fact);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveKnowerAsync(string factId, string entityId, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var fact = await session.LoadAsync<FactDocument>(factId);
        if (fact == null || fact.CampaignId != access.CampaignId || fact.KnownBy.RemoveAll(k => k.EntityId == entityId) == 0)
        {
            return false;
        }

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

    public async Task<bool> AddKnowerAsync(string factId, KnowerEntry knower, CampaignAccess access)
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

        fact.KnownBy.RemoveAll(k => k.EntityId == knower.EntityId);
        fact.KnownBy.Add(knower);
        if (fact.Visibility == FactVisibility.DmOnly)
        {
            fact.Visibility = FactVisibility.KnowersOnly;
        }

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
