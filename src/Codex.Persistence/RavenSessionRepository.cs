using Raven.Client.Documents;

namespace Codex.Persistence;

public class RavenSessionRepository(RavenDbService dbService) : ISessionRepository
{
    public async Task<IEnumerable<SessionDocument>> GetAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<SessionDocument>()
            .Where(x => x.CampaignId == campaignId)
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
}
