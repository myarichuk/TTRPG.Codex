namespace Codex.Persistence;

public class CampaignAccessResolver(ICampaignRepository campaignRepository) : ICampaignAccessResolver
{
    public async Task<CampaignAccess?> ResolveAsync(string campaignId, string userId)
    {
        var campaign = await campaignRepository.GetAsync(campaignId);
        if (campaign == null)
        {
            return null;
        }

        if (string.Equals(campaign.OwnerId, userId, StringComparison.Ordinal))
        {
            return new CampaignAccess(campaignId, userId, CampaignRole.DM);
        }

        var member = campaign.Members.FirstOrDefault(m => string.Equals(m.UserId, userId, StringComparison.Ordinal));
        return member == null ? null : new CampaignAccess(campaignId, userId, member.Role);
    }
}
