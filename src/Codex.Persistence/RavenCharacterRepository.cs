using Raven.Client.Documents;

namespace Codex.Persistence;

public class RavenCharacterRepository : ICharacterRepository
{
    private readonly RavenDbService _dbService;

    public RavenCharacterRepository(RavenDbService dbService)
    {
        _dbService = dbService;
    }

    public async Task<IEnumerable<CharacterDocument>> GetAllForCampaignAsync(string campaignId)
    {
        using var session = _dbService.Store.OpenAsyncSession();
        return await session.Query<CharacterDocument>()
            .Where(c => c.CampaignId == campaignId)
            .ToListAsync();
    }

    public async Task<CharacterDocument?> GetAsync(string characterId)
    {
        using var session = _dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<CharacterDocument>(characterId);
    }

    public async Task SaveAsync(CharacterDocument character)
    {
        using var session = _dbService.Store.OpenAsyncSession();
        await session.StoreAsync(character);
        await session.SaveChangesAsync();
    }
}
