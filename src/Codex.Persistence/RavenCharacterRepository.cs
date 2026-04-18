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
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<CharacterDocument>> GetAllAsync(int limit = 10, bool sortByCreated = false)
    {
        using var session = _dbService.Store.OpenAsyncSession();
        var query = session.Query<CharacterDocument>();
        
        if (sortByCreated)
        {
            query = query.OrderByDescending(c => c.CreatedAt);
        }
        else
        {
            query = query.OrderByDescending(c => c.UpdatedAt);
        }

        return await query.Take(limit).ToListAsync();
    }

    public async Task<CharacterDocument?> GetAsync(string characterId)
    {
        using var session = _dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<CharacterDocument>(characterId);
    }

    public async Task SaveAsync(CharacterDocument character)
    {
        using var session = _dbService.Store.OpenAsyncSession();
        
        character.UpdatedAt = DateTime.UtcNow;
        if (string.IsNullOrEmpty(character.Id))
        {
            character.CreatedAt = DateTime.UtcNow;
        }

        await session.StoreAsync(character);
        await session.SaveChangesAsync();
    }
}
