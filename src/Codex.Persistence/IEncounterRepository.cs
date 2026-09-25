namespace Codex.Persistence;

public interface IEncounterRepository
{
    Task<EncounterDocument?> GetAsync(string encounterId);
    Task SaveAsync(EncounterDocument encounter);
    Task<IEnumerable<EncounterDocument>> GetAllForCampaignAsync(string campaignId);

    /// <summary>Deletes every encounter belonging to a campaign (B13 cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);
}
