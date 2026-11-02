using Codex.Core;
using Raven.Client.Documents;

namespace Codex.Persistence;

public class RavenSessionRepository(RavenDbService dbService) : ISessionRepository
{
    public async Task<IEnumerable<SessionDocument>> GetAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<SessionDocument, SessionsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.CampaignId == campaignId)
            .OrderByDescending(x => x.Date)
            .ToListAsync();
    }

    public async Task<SessionDocument?> GetAsync(string sessionId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<SessionDocument>(sessionId);
    }

    // Copies redacted for a player: saved whole, one would write the player's view over the DM's session.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SessionDocument, object> Redacted = new();

    public async Task<SessionDocument?> GetVisibleAsync(string sessionId, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var stored = await session.LoadAsync<SessionDocument>(sessionId);
        if (stored == null || stored.CampaignId != access.CampaignId)
        {
            return null;
        }

        if (access.IsDm)
        {
            return stored;
        }

        // What the actor repository gives this player: no Hidden actor unless it is theirs (F6).
        var visible = await session.Query<ActorDocument, ActorsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(a => a.CampaignId == access.CampaignId && (a.Visibility != ActorVisibility.Hidden || a.OwnerUserId == access.UserId))
            .ToListAsync();
        var visibleIds = visible.Select(a => a.Id).ToHashSet();
        var owned = string.IsNullOrEmpty(access.UserId) ? new HashSet<string>() : visible.Where(a => a.OwnerUserId == access.UserId).Select(a => a.Id).ToHashSet();

        stored.DmSummary = string.Empty;
        stored.Recap = SecretText.ForPlayers(stored.Recap, owned);
        stored.Notes = stored.Notes.Where(n => !n.IsSecret).ToList();
        foreach (var note in stored.Notes)
        {
            note.Text = SessionNotes.TextFor(note, access, owned);
        }

        stored.Events = SessionEventRedactor.ForViewer(stored.Events, access, visible).ToList();
        stored.RollLog = stored.RollLog.Where(r => !r.IsSecret && (r.ActorId == null || visibleIds.Contains(r.ActorId))).ToList();
        Redacted.AddOrUpdate(stored, stored);
        return stored;
    }

    public async Task SaveAsync(SessionDocument sessionDocument)
    {
        if (Redacted.TryGetValue(sessionDocument, out _))
        {
            throw new InvalidOperationException("A session redacted for a player cannot be saved.");
        }

        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(sessionDocument);
        await session.SaveChangesAsync();
    }

    public Task<SessionDocument?> UpdateAsync(string sessionId, Action<SessionDocument> mutate) =>
        RavenUpdate.RunAsync(dbService, sessionId, mutate);

    public async Task<bool> DeleteAsync(string sessionId, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        access = current;

        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var stored = await session.LoadAsync<SessionDocument>(sessionId);
        if (stored == null || stored.CampaignId != access.CampaignId)
        {
            return false;
        }

        foreach (var encounter in (await session.LoadAsync<EncounterDocument>(stored.EncounterIds)).Values)
        {
            if (encounter?.CampaignId == access.CampaignId) session.Delete(encounter);
        }

        session.Delete(stored);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<SessionDocument, SessionsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.CampaignId == campaignId)
            .ToListAsync();

        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }
}
