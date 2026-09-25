using Raven.Client.Documents;

namespace Codex.Persistence;

public class RavenSessionRepository(RavenDbService dbService) : ISessionRepository
{
    public async Task<IEnumerable<SessionDocument>> GetAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<SessionDocument, SessionsByCampaignIndex>()
            .Where(x => x.CampaignId == campaignId)
            .OrderByDescending(x => x.Date)
            .ToListAsync();
    }

    public async Task<SessionDocument?> GetAsync(string sessionId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<SessionDocument>(sessionId);
    }

    public async Task SaveAsync(SessionDocument sessionDocument)
    {
        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(sessionDocument);
        await session.SaveChangesAsync();
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<SessionDocument, SessionsByCampaignIndex>()
            .Where(x => x.CampaignId == campaignId)
            .ToListAsync();

        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }
}
