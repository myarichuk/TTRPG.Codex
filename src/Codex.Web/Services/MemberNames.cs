using Codex.Persistence;

namespace Codex.Web.Services;

/// <summary>A campaign's members by user id, as their usernames: what pickers and lists show instead of raw ids.</summary>
public static class MemberNames
{
    public static async Task<Dictionary<string, string>> LoadAsync(IUserRepository users, CampaignDocument campaign)
    {
        var names = new Dictionary<string, string>();
        foreach (var member in campaign.Members)
        {
            names[member.UserId] = (await users.GetUserByIdAsync(member.UserId))?.Username ?? member.UserId;
        }
        return names;
    }
}
