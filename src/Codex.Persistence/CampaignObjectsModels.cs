using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Raven.Client.Exceptions;

namespace Codex.Persistence;

/// <summary>
/// A campaign's own objects (overhaul step 7): its clocks and tables, as <see cref="CampaignObjects"/> reads them. A document of
/// its own, one per campaign, rather than a field of <see cref="CampaignDocument"/>: a clock ticks at the table every few
/// minutes, and the campaign document is saved whole from the settings page, so sharing one would have each lose the other's
/// writes.
/// </summary>
public class CampaignObjectsDocument
{
    public static string IdFor(string campaignId) => "CampaignObjects/" + campaignId;

    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;

    /// <summary><c>clocks</c> and each table's rows, by key.</summary>
    public Dictionary<string, object> State { get; set; } = new();

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public interface ICampaignObjectsRepository
{
    /// <summary>The campaign's objects as <paramref name="access"/> may see them: whole for the GM, without what is kept for the
    /// GM for a player. Empty when there are none.</summary>
    Task<Dictionary<string, object>> GetVisibleAsync(CampaignAccess access, SystemDefinition system);

    /// <summary>Changes the stored objects: <paramref name="change"/> runs on a fresh load (more than once when another writer
    /// got in between) and returns false to leave them unsaved. GM only; null when refused, else the objects as saved.</summary>
    Task<Dictionary<string, object>?> TryChangeAsync(CampaignAccess access, Func<Dictionary<string, object>, bool> change);

    /// <summary>Writes <paramref name="keys"/> from <paramref name="state"/> (a key it lacks is removed), leaving the others as
    /// stored: the live table's write-back, which knows which keys its commands changed.</summary>
    Task WriteKeysAsync(string campaignId, IReadOnlyDictionary<string, object> state, IEnumerable<string> keys);

    /// <summary>Export and the live table's start: the stored document, or null.</summary>
    Task<CampaignObjectsDocument?> GetDocumentAsync(string campaignId);

    /// <summary>Import only.</summary>
    Task SaveDocumentAsync(CampaignObjectsDocument document);

    Task DeleteAllForCampaignAsync(string campaignId);
}

public sealed class RavenCampaignObjectsRepository(RavenDbService dbService) : ICampaignObjectsRepository
{
    private const int MaxAttempts = 8;

    public async Task<Dictionary<string, object>> GetVisibleAsync(CampaignAccess access, SystemDefinition system)
    {
        var stored = await GetDocumentAsync(access.CampaignId);
        return CampaignObjects.Visible(system, stored?.State, access.IsDm);
    }

    public async Task<Dictionary<string, object>?> TryChangeAsync(CampaignAccess access, Func<Dictionary<string, object>, bool> change)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { IsDm: true, SystemMissing: false } current) return null;
        return await ChangeAsync(current.CampaignId, state => change(state));
    }

    public Task WriteKeysAsync(string campaignId, IReadOnlyDictionary<string, object> state, IEnumerable<string> keys)
    {
        var written = keys.ToList();
        return ChangeAsync(campaignId, stored =>
        {
            foreach (var key in written)
            {
                if (state.TryGetValue(key, out var value)) stored[key] = value;
                else stored.Remove(key);
            }

            return written.Count > 0;
        });
    }

    /// <summary>Load (or start) the document, change it, save under optimistic concurrency; again from a fresh load when
    /// another writer got in between.</summary>
    private async Task<Dictionary<string, object>> ChangeAsync(string campaignId, Func<Dictionary<string, object>, bool> change)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var session = dbService.Store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            var id = CampaignObjectsDocument.IdFor(campaignId);
            var document = await session.LoadAsync<CampaignObjectsDocument>(id);
            var isNew = document == null;
            document ??= new CampaignObjectsDocument { Id = id, CampaignId = campaignId };
            if (!change(document.State)) return document.State;
            document.UpdatedAt = DateTime.UtcNow;
            if (isNew) await session.StoreAsync(document, string.Empty, id);
            try
            {
                await session.SaveChangesAsync();
                return document.State;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
                // Saved by someone else between the load and the save (or started by them): go again.
            }
        }
    }

    public async Task<CampaignObjectsDocument?> GetDocumentAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<CampaignObjectsDocument>(CampaignObjectsDocument.IdFor(campaignId));
    }

    public async Task SaveDocumentAsync(CampaignObjectsDocument document)
    {
        using var session = dbService.Store.OpenAsyncSession();
        document.Id = CampaignObjectsDocument.IdFor(document.CampaignId);
        await session.StoreAsync(document, document.Id);
        await session.SaveChangesAsync();
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        session.Delete(CampaignObjectsDocument.IdFor(campaignId));
        await session.SaveChangesAsync();
    }
}
