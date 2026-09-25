namespace Codex.Persistence;

public interface ICharacterRepository
{
    Task<CharacterDocument?> GetAsync(string characterId);
    Task SaveAsync(CharacterDocument character);
    Task<IEnumerable<CharacterDocument>> GetAllForCampaignAsync(string campaignId);
    Task<IEnumerable<CharacterDocument>> GetAllAsync(int limit = 10, bool sortByCreated = false);

    /// <summary>Deletes every character belonging to a campaign (B13 cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);
}
