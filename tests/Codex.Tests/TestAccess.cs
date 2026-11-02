using Codex.Persistence;

namespace Codex.Tests;

/// <summary>
/// A campaign access for a test, with the campaign stored so that it grants that role. Repository writes read the role
/// from the stored campaign (F13), so an access made up for a campaign no one stored would be refused. The campaign is
/// made when there is none (its owner is the user, for a DM), and the user is added as a member when they are not in it.
/// A test that saves its own campaign document afterwards replaces this one, so it names its members itself.
/// </summary>
internal static class TestAccess
{
    public static CampaignAccess Member(RavenDbService db, string campaignId, string userId, CampaignRole role)
    {
        using var session = db.Store.OpenSession();
        var campaign = session.Load<CampaignDocument>(campaignId);
        if (campaign == null)
        {
            campaign = new CampaignDocument { Id = campaignId, Name = campaignId, SystemId = "srd5e", OwnerId = role == CampaignRole.DM ? userId : "owner-of-" + campaignId };
            session.Store(campaign, campaignId);
        }

        if (CampaignAccessResolver.RoleOf(campaign, userId) == null)
        {
            campaign.Members.Add(new CampaignMember { UserId = userId, Role = role });
        }

        session.SaveChanges();
        return new CampaignAccess(campaignId, userId, role);
    }
}
