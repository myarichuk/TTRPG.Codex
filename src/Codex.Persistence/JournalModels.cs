using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;

namespace Codex.Persistence;

/// <summary>
/// A player's private notebook entry: one per campaign and (optionally) session, readable by its
/// owner alone. Unlike <see cref="NoteDocument"/>, the DM cannot see these - they are the
/// player's own scribbles - so they are also kept out of the DM's campaign export.
/// </summary>
public class JournalEntryDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>The session this entry belongs to; null for a general campaign note.</summary>
    public string? SessionId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public interface IJournalRepository
{
    /// <summary>The caller's own entries in the campaign, newest first. Nobody else's, ever.</summary>
    Task<IEnumerable<JournalEntryDocument>> GetMineAsync(CampaignAccess access);

    /// <summary>Creates or updates an entry the caller owns. Returns false for anyone else's entry.</summary>
    Task<bool> TrySaveAsync(JournalEntryDocument entry, CampaignAccess access);

    Task<bool> TryDeleteAsync(string id, CampaignAccess access);

    Task DeleteAllForCampaignAsync(string campaignId);
}

public class JournalByCampaignIndex : AbstractIndexCreationTask<JournalEntryDocument>
{
    public JournalByCampaignIndex()
    {
        Map = entries => from entry in entries
                         select new
                         {
                             entry.CampaignId,
                             entry.OwnerUserId,
                             entry.SessionId,
                             entry.UpdatedAt
                         };
    }
}

public class RavenJournalRepository(RavenDbService dbService) : IJournalRepository
{
    public async Task<IEnumerable<JournalEntryDocument>> GetMineAsync(CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        // Owner filter runs inside the query: another member's journal never leaves the database.
        return await session.Query<JournalEntryDocument, JournalByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(e => e.CampaignId == access.CampaignId && e.OwnerUserId == access.UserId)
            .OrderByDescending(e => e.UpdatedAt)
            .ToListAsync();
    }

    public async Task<bool> TrySaveAsync(JournalEntryDocument entry, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var existing = string.IsNullOrEmpty(entry.Id) ? null : await session.LoadAsync<JournalEntryDocument>(entry.Id);
        if (existing != null && (existing.CampaignId != access.CampaignId || existing.OwnerUserId != access.UserId))
        {
            return false;
        }

        entry.UpdatedAt = DateTime.UtcNow;
        if (existing == null)
        {
            entry.Id = string.IsNullOrEmpty(entry.Id) ? Guid.NewGuid().ToString() : entry.Id;
            entry.CampaignId = access.CampaignId;
            entry.OwnerUserId = access.UserId;
            entry.CreatedAt = DateTime.UtcNow;
            await session.StoreAsync(entry);
        }
        else
        {
            existing.Title = entry.Title;
            existing.Body = entry.Body;
            existing.SessionId = entry.SessionId;
            existing.UpdatedAt = entry.UpdatedAt;
        }

        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> TryDeleteAsync(string id, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var entry = await session.LoadAsync<JournalEntryDocument>(id);
        if (entry == null || entry.CampaignId != access.CampaignId || entry.OwnerUserId != access.UserId)
        {
            return false;
        }

        session.Delete(entry);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<JournalEntryDocument, JournalByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(e => e.CampaignId == campaignId)
            .ToListAsync();
        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }
}
