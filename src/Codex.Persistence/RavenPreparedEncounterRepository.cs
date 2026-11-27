using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace Codex.Persistence;

public sealed class RavenPreparedEncounterRepository(RavenDbService dbService) : IPreparedEncounterRepository
{
    private const int MaxAttempts = 8;

    public async Task<PreparedEncounterDocument?> GetAsync(string id, CampaignAccess access)
    {
        if (string.IsNullOrEmpty(id) || await ReaderAsync(access) is not { } current) return null;
        using var session = dbService.Store.OpenAsyncSession();
        return await LoadAsync(session, id, current.CampaignId);
    }

    public async Task<IReadOnlyList<PreparedEncounterDocument>> ListAsync(CampaignAccess access)
    {
        if (await ReaderAsync(access) is not { } current) return Array.Empty<PreparedEncounterDocument>();
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<PreparedEncounterDocument, PreparedEncountersByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.CampaignId == current.CampaignId)
            .OrderByDescending(x => x.UpdatedAt)
            .ToListAsync();
    }

    public async Task<bool> SaveAsync(PreparedEncounterDocument document, CampaignAccess access)
    {
        if (document == null || await WriterAsync(access) is not { } current) return false;
        if (string.IsNullOrEmpty(document.CampaignId)) document.CampaignId = current.CampaignId;
        if (document.CampaignId != current.CampaignId) return false;
        document.Lines ??= new List<PreparedLine>();
        document.Tags ??= new List<string>();
        document.HazardsLeftOut ??= new List<string>();

        if (string.IsNullOrEmpty(document.Id))
        {
            var id = PreparedEncounterDocument.IdPrefix + Guid.NewGuid();
            document.Id = id;
            document.CreatedBy = current.UserId;
            document.UpdatedAt = DateTime.UtcNow;
            document.RunEncounterIds = new List<string>(); // a new plan has run nowhere, whatever the copy says
            document.LastRunAt = null;
            if (document.Status == PreparedStatus.Ran) document.Status = PreparedStatus.Ready; // only MarkRunAsync sets Ran
            using var session = dbService.Store.OpenAsyncSession();
            await session.StoreAsync(document, string.Empty, id);
            await session.SaveChangesAsync();
            return true;
        }

        for (var attempt = 1; ; attempt++)
        {
            using var session = dbService.Store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            var stored = await LoadAsync(session, document.Id, current.CampaignId);
            if (stored == null) return false;

            // The stored copy is changed, not replaced, so what only another writer owns (the run list, who made it) cannot be lost to a stale copy.
            stored.Name = document.Name;
            stored.RegionId = document.RegionId;
            stored.PlaceId = document.PlaceId;
            stored.Lines = document.Lines;
            stored.Notes = document.Notes;
            stored.Tags = document.Tags;
            stored.HazardsLeftOut = document.HazardsLeftOut ?? new List<string>();
            stored.FromChatId = document.FromChatId;
            stored.UpdatedAt = DateTime.UtcNow;
            // Only MarkRunAsync sets Ran: a plan with stored runs stays Ran whatever the copy says; one with none cannot be Ran.
            stored.Status = stored.RunEncounterIds.Count > 0 ? PreparedStatus.Ran
                : document.Status == PreparedStatus.Ran ? PreparedStatus.Ready : document.Status;

            try
            {
                await session.SaveChangesAsync();
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
                continue;
            }

            document.Status = stored.Status;
            document.RunEncounterIds = stored.RunEncounterIds.ToList();
            document.LastRunAt = stored.LastRunAt;
            document.CreatedBy = stored.CreatedBy;
            document.UpdatedAt = stored.UpdatedAt;
            return true;
        }
    }

    public async Task<bool> DeleteAsync(string id, CampaignAccess access)
    {
        if (string.IsNullOrEmpty(id) || await WriterAsync(access) is not { } current) return false;
        using var session = dbService.Store.OpenAsyncSession();
        if (await LoadAsync(session, id, current.CampaignId) is not { } stored) return false;
        session.Delete(stored);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<PreparedEncounterDocument, PreparedEncountersByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.CampaignId == campaignId)
            .ToListAsync();
        foreach (var doc in toDelete) session.Delete(doc);
        await session.SaveChangesAsync();
    }

    public async Task<bool> MarkRunAsync(string id, string encounterId, CampaignAccess access)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(encounterId) || await WriterAsync(access) is not { } current) return false;
        for (var attempt = 1; ; attempt++)
        {
            using var session = dbService.Store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            var stored = await LoadAsync(session, id, current.CampaignId);
            if (stored == null) return false;

            // Only the run fields (and the time): a plan saved by someone else since the start was read keeps all its edits.
            if (!stored.RunEncounterIds.Contains(encounterId)) stored.RunEncounterIds.Add(encounterId);
            stored.Status = PreparedStatus.Ran;
            stored.LastRunAt = DateTime.UtcNow;
            stored.UpdatedAt = DateTime.UtcNow;
            try
            {
                await session.SaveChangesAsync();
                return true;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
                // Saved by someone else between the load and the save: go again from a fresh load.
            }
        }
    }

    public async Task<IReadOnlyList<PreparedEncounterDocument>> GetAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<PreparedEncounterDocument, PreparedEncountersByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.CampaignId == campaignId)
            .OrderBy(x => x.UpdatedAt)
            .ToListAsync();
    }

    public async Task RestoreAsync(PreparedEncounterDocument document)
    {
        if (string.IsNullOrEmpty(document.CampaignId)) return;
        if (string.IsNullOrEmpty(document.Id)) document.Id = PreparedEncounterDocument.IdPrefix + Guid.NewGuid();
        document.Lines ??= new List<PreparedLine>();
        document.Tags ??= new List<string>();
        document.HazardsLeftOut ??= new List<string>();
        document.RunEncounterIds ??= new List<string>();
        // An upsert, as every other document of an import is, but only over this campaign's own plan, or at an id nothing is stored at and that a plan
        // can have (the prefix, or a bare GUID as an import as a copy makes). Another campaign's document, another kind's, and the per-campaign ids
        // (CampaignObjects/{id} and the like, which would make a later save of that document fail) are never written over or taken: a crafted file
        // writes nothing for that plan.
        using (var check = dbService.Store.OpenAsyncSession())
        {
            if (await check.Advanced.ExistsAsync(document.Id))
            {
                if (await LoadAsync(check, document.Id, document.CampaignId) == null) return;
            }
            else if (!IsPlanId(document.Id))
            {
                return;
            }
        }

        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(document, document.Id);
        await session.SaveChangesAsync();
    }

    private static bool IsPlanId(string id) =>
        Guid.TryParseExact(id.StartsWith(PreparedEncounterDocument.IdPrefix, StringComparison.Ordinal) ? id[PreparedEncounterDocument.IdPrefix.Length..] : id, "D", out _);

    /// <summary>The access as the campaign stores it now, when it is a GM's: the role is read again, so a co-GM demoted after their page resolved reads nothing more.
    /// A game system that is missing still reads.</summary>
    private async Task<CampaignAccess?> ReaderAsync(CampaignAccess access) =>
        access.IsDm && await CampaignAccessResolver.NowAsync(dbService.Store, access, write: false) is { IsDm: true } current ? current : null;

    /// <summary>As <see cref="ReaderAsync"/>, and not a party preview (a player's access, so never a GM's) and not for a missing game system.</summary>
    private async Task<CampaignAccess?> WriterAsync(CampaignAccess access) =>
        access.IsDm && await CampaignAccessResolver.NowAsync(dbService.Store, access) is { IsDm: true, SystemMissing: false } current ? current : null;

    /// <summary>Loads the plan with that id, or null when nothing is stored there, what is stored is not a plan (another kind of document: it is never
    /// deserialized as one, since kinds share property names of different types), or, when <paramref name="campaignId"/> is given, it is not that
    /// campaign's. The id is looked up in the plans' collection only.</summary>
    private async Task<PreparedEncounterDocument?> LoadAsync(IAsyncDocumentSession session, string id, string? campaignId)
    {
        var collection = dbService.Store.Conventions.GetCollectionName(typeof(PreparedEncounterDocument));
        var stored = (await session.Advanced.AsyncRawQuery<PreparedEncounterDocument>($"from '{collection}' where id() = $id")
            .AddParameter("id", id)
            .Take(1)
            .ToListAsync()).FirstOrDefault();
        if (stored == null) return null;
        if (campaignId != null && stored.CampaignId != campaignId) return null;
        stored.Lines ??= new List<PreparedLine>();
        stored.Tags ??= new List<string>();
        stored.RunEncounterIds ??= new List<string>();
        return stored;
    }
}
