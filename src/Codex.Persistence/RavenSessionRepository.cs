using Raven.Client.Documents;

namespace Codex.Persistence;

public class RavenSessionRepository : ISessionRepository
{
    private readonly RavenDbService _dbService;

    public RavenSessionRepository(RavenDbService dbService)
    {
        _dbService = dbService;
    }

    public async Task<IEnumerable<SessionDocument>> GetAllForCampaignAsync(string campaignId)
    {
        using var session = _dbService.Store.OpenAsyncSession();
        return await session.Query<SessionDocument>()
            .Where(x => x.CampaignId == campaignId)
            .ToListAsync();
    }

    public async Task<SessionDocument?> GetAsync(string sessionId)
    {
        using var session = _dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<SessionDocument>(sessionId);
    }

    public async Task SaveAsync(SessionDocument sessionDocument)
    {
        using var session = _dbService.Store.OpenAsyncSession();
        await session.StoreAsync(sessionDocument);
        await session.SaveChangesAsync();
    }
}
