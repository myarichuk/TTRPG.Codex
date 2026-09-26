using Raven.Client.Documents;

namespace Codex.Persistence;

public class RavenEncounterRepository(RavenDbService dbService) : IEncounterRepository
{
    public async Task<IEnumerable<EncounterDocument>> GetAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.Query<EncounterDocument, EncountersByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.CampaignId == campaignId)
            .ToListAsync();
    }

    public async Task<EncounterDocument?> GetAsync(string encounterId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<EncounterDocument>(encounterId);
    }

    public async Task SaveAsync(EncounterDocument encounter)
    {
        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(encounter);
        await session.SaveChangesAsync();
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<EncounterDocument, EncountersByCampaignIndex>()
            .Where(x => x.CampaignId == campaignId)
            .ToListAsync();

        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }
}
