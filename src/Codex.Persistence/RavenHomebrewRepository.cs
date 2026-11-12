using Codex.Core;
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

        var entries = Normalized(await query.OrderBy(h => h.Name).ToListAsync());
        return access.IsDm ? entries : entries.Select(ForPlayers).ToList();
    }

    public async Task<HomebrewEntryDocument?> GetVisibleAsync(string id, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var entry = await session.LoadAsync<HomebrewEntryDocument>(id);
        if (entry == null || entry.CampaignId != access.CampaignId)
        {
            return null;
        }

        if (!access.IsDm && !entry.IsPublished) return null;
        var normalized = Normalized(new[] { entry })[0];
        return access.IsDm ? normalized : ForPlayers(normalized);
    }

    public async Task<bool> TrySaveAsync(HomebrewEntryDocument entry, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        access = current;

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
            existing.ImportedFrom = entry.ImportedFrom;
            existing.UpdatedAt = entry.UpdatedAt;
        }

        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetArtAsync(string id, bool hasArt, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        access = current;

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

        entry.HasArt = hasArt;
        entry.ArtVersion++;
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> TryDeleteAsync(string id, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        access = current;

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
        return Normalized(await session.Query<HomebrewEntryDocument, HomebrewByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(h => h.CampaignId == campaignId)
            .ToListAsync());
    }

    /// <summary>A published entry as a player is handed it: the GM's blocks (<see cref="SecretText"/>) are gone from its
    /// description and from every text in its properties (a feature's description), so they never reach a player's browser.
    /// The entry was loaded for this read alone, so it is changed in place.</summary>
    internal static HomebrewEntryDocument ForPlayers(HomebrewEntryDocument entry)
    {
        entry.Description = SecretText.ForPlayers(entry.Description);
        entry.Properties = (Dictionary<string, object>)Redacted(entry.Properties)!;
        return entry;
    }

    /// <summary>The same for an entry a character carries: what a player wrote or was given never holds a GM's block.</summary>
    internal static CarriedEntry ForPlayers(CarriedEntry entry) => new()
    {
        Type = entry.Type,
        EntryId = entry.EntryId,
        Name = entry.Name,
        Description = SecretText.ForPlayers(entry.Description),
        Tags = new List<string>(entry.Tags),
        Properties = (Dictionary<string, object>)Redacted(YamlValues.NormalizeMap(entry.Properties))!
    };

    private static object? Redacted(object? value) => value switch
    {
        string text => SecretText.ForPlayers(text),
        IDictionary<string, object> map => map.ToDictionary(p => p.Key, p => Redacted(p.Value)!),
        System.Collections.IEnumerable list => list.Cast<object?>().Select(Redacted).ToList<object>()!,
        _ => value
    };

    /// <summary>RavenDB reads numbers inside an <c>object</c> back as its own lazy type ("18.0");
    /// the editor, the stat block and the exporter all want plain longs and doubles.</summary>
    private static IList<HomebrewEntryDocument> Normalized(IList<HomebrewEntryDocument> entries)
    {
        foreach (var entry in entries)
        {
            entry.Properties = YamlValues.NormalizeMap(entry.Properties);
        }

        return entries;
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
            .Customize(x => x.WaitForNonStaleResults())
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
