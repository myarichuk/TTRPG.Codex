using Raven.Client.Documents;

namespace Codex.Persistence;

public class CharacterRepository(RavenDbService dbService) : ICharacterRepository
{
    public async Task<IEnumerable<CharacterDocument>> GetAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<CharacterDocument>()
            .Where(c => c.CampaignId == campaignId)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<CharacterDocument>> GetAllAsync(int limit = 10, bool sortByCreated = false)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var query = session.Query<CharacterDocument>();

        if (sortByCreated)
        {
            return await query.OrderByDescending(c => c.CreatedAt).Take(limit).ToListAsync();
        }
        else
        {
            return await query.OrderByDescending(c => c.UpdatedAt).Take(limit).ToListAsync();
        }
    }

    public async Task<CharacterDocument?> GetAsync(string characterId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<CharacterDocument>(characterId);
    }

    public async Task SaveAsync(CharacterDocument character)
    {
        using var session = dbService.Store.OpenAsyncSession();

        character.UpdatedAt = DateTime.UtcNow;
        if (string.IsNullOrEmpty(character.Id))
        {
            character.CreatedAt = DateTime.UtcNow;
        }

        await session.StoreAsync(character);
        await session.SaveChangesAsync();
    }
}
