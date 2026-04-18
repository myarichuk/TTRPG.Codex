namespace Codex.Persistence;

public interface ICharacterRepository
{
    Task<CharacterDocument?> GetAsync(string characterId);
    Task SaveAsync(CharacterDocument character);
    Task<IEnumerable<CharacterDocument>> GetAllForCampaignAsync(string campaignId);
    Task<IEnumerable<CharacterDocument>> GetAllAsync(int limit = 10, bool sortByCreated = false);
}
