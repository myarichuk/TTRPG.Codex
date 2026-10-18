using Raven.Client.Documents;

namespace Codex.Persistence;

public class RavenHomebrewRepository(RavenDbService dbService) : IHomebrewRepository
{
    public async Task<IEnumerable<HomebrewEntryDocument>> GetVisibleForCampaignAsync(CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var query = session.Query<HomebrewEntryDocument, HomebrewByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(h => h.CampaignId == access.CampaignId);

        if (!access.IsDm)
        {
            query = query.Where(h => h.IsPublished);
        }

        return await query.OrderBy(h => h.Name).ToListAsync();
    }

    public async Task<HomebrewEntryDocument?> GetVisibleAsync(string id, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var entry = await session.LoadAsync<HomebrewEntryDocument>(id);
        if (entry == null || entry.CampaignId != access.CampaignId)
        {
            return null;
        }

        return access.IsDm || entry.IsPublished ? entry : null;
    }

    public async Task<bool> TrySaveAsync(HomebrewEntryDocument entry, CampaignAccess access)
    {
        if (!access.IsDm || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Type))
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var existing = string.IsNullOrEmpty(entry.Id) ? null : await session.LoadAsync<HomebrewEntryDocument>(entry.Id);
        if (existing != null && existing.CampaignId != access.CampaignId)
        {
            return false;
        }

        entry.CampaignId = access.CampaignId;
        if (string.IsNullOrEmpty(entry.Id))
        {
            entry.Id = Guid.NewGuid().ToString();
            entry.CreatedAt = DateTime.UtcNow;
        }

        if (string.IsNullOrWhiteSpace(entry.EntryId))
        {
            entry.EntryId = Slugify(entry.Name);
        }

        entry.UpdatedAt = DateTime.UtcNow;

        if (existing == null)
        {
            await session.StoreAsync(entry);
        }
        else
        {
            existing.Type = entry.Type;
            existing.EntryId = entry.EntryId;
            existing.Name = entry.Name;
            existing.Description = entry.Description;
            existing.Tags = entry.Tags;
            existing.Properties = entry.Properties;
            existing.IsPublished = entry.IsPublished;
            existing.UpdatedAt = entry.UpdatedAt;
        }

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
        var entry = await session.LoadAsync<HomebrewEntryDocument>(id);
        if (entry == null || entry.CampaignId != access.CampaignId)
        {
            return false;
        }

        session.Delete(entry);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<IEnumerable<HomebrewEntryDocument>> GetAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<HomebrewEntryDocument, HomebrewByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(h => h.CampaignId == campaignId)
            .ToListAsync();
    }

    public async Task SaveAsync(HomebrewEntryDocument entry)
    {
        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(entry);
        await session.SaveChangesAsync();
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<HomebrewEntryDocument, HomebrewByCampaignIndex>()
            .Where(h => h.CampaignId == campaignId)
            .ToListAsync();

        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }

    public static string Slugify(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? Guid.NewGuid().ToString("N")[..8] : slug;
    }
}
