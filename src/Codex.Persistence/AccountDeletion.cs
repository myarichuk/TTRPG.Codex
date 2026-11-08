using Raven.Client.Documents;

namespace Codex.Persistence;

/// <summary>A campaign the account being deleted owns, and who may take it: its other GMs (co-GMs), in member order.</summary>
public sealed record OwnedCampaign(string CampaignId, string Name, IReadOnlyList<string> CoGms);

/// <summary>What deleting an account touches, for the admin to read before confirming.</summary>
public sealed record AccountDeletionPlan(IReadOnlyList<OwnedCampaign> Owned, int Memberships, int VaultCharacters);

public enum AccountDeletionResult { Deleted, NotFound }

/// <summary>
/// Deleting an account (docs/SERVER_ACCESS_AND_VAULT.md 9, built in 15). Every campaign it owns passes to one of its co-GMs, chosen by
/// the admin, or to the admin when it has none, so no campaign is lost and none is left without an owner. It leaves every other
/// campaign as a player who leaves would: its characters there stay in the cast. What is its alone goes with it: the vault, its
/// private journals, its world-builder chats and book builds. The account itself goes last, after it is turned off, so an attempt
/// that stops half way leaves a turned-off account the admin deletes again, never a campaign whose owner is nobody.
/// </summary>
public sealed class AccountDeletion(RavenDbService db, ICampaignRepository campaigns, IVaultRepository vault, IUserRepository users)
{
    public async Task<AccountDeletionPlan> PlanAsync(string userId)
    {
        var theirs = (await campaigns.GetForUserAsync(userId)).ToList();
        var owned = theirs.Where(c => c.OwnerId == userId)
            .Select(c => new OwnedCampaign(c.Id, c.Name, CoGms(c, userId)))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new AccountDeletionPlan(owned, theirs.Count - owned.Count, (await vault.GetAllAsync(userId)).Count);
    }

    /// <summary>
    /// Deletes <paramref name="userId"/>. <paramref name="newOwners"/> names, per owned campaign, the co-GM who takes it; a campaign
    /// missing from it, or naming someone who is not a co-GM of it as stored now, goes to <paramref name="adminId"/>, who becomes its
    /// owner and a GM member.
    /// </summary>
    public async Task<AccountDeletionResult> DeleteAsync(string userId, string adminId, IReadOnlyDictionary<string, string>? newOwners = null)
    {
        if (string.IsNullOrEmpty(userId) || userId == adminId) throw new ArgumentException("An admin does not delete their own account here.", nameof(userId));
        if (await users.UpdateUserAsync(userId, u => u.Status = AccountStatus.Disabled) == null) return AccountDeletionResult.NotFound;

        foreach (var campaign in (await campaigns.GetForUserAsync(userId)).ToList())
        {
            if (campaign.OwnerId == userId)
            {
                var chosen = newOwners?.GetValueOrDefault(campaign.Id);
                await campaigns.UpdateAsync(campaign.Id, c => HandOver(c, userId, chosen, adminId));
            }

            // Leaving refuses the owner; after the hand-over they are not one any more.
            await campaigns.LeaveAsync(campaign.Id, userId);
        }

        foreach (var character in await vault.GetAllAsync(userId))
        {
            await vault.DeleteAsync(character.Id, userId);
        }

        await DeleteOwnedAsync<JournalEntryDocument>(j => j.OwnerUserId == userId);
        await DeleteOwnedAsync<WorldChatDocument>(c => c.OwnerUserId == userId);
        await DeleteOwnedAsync<BookBuildDocument>(b => b.OwnerUserId == userId);

        await users.DeleteAsync(userId);
        return AccountDeletionResult.Deleted;
    }

    /// <summary>The GM members other than <paramref name="owner"/>: who can take the campaign over.</summary>
    public static IReadOnlyList<string> CoGms(CampaignDocument campaign, string owner) =>
        campaign.Members.Where(m => m.Role == CampaignRole.DM && m.UserId != owner).Select(m => m.UserId).Distinct().ToList();

    /// <summary>Passes <paramref name="campaign"/> from <paramref name="owner"/> to <paramref name="chosen"/> when that is one of its
    /// co-GMs now, otherwise to <paramref name="admin"/>, made a GM member. Does nothing when <paramref name="owner"/> no longer owns it.</summary>
    public static void HandOver(CampaignDocument campaign, string owner, string? chosen, string admin)
    {
        if (campaign.OwnerId != owner) return;
        var to = chosen != null && CoGms(campaign, owner).Contains(chosen) ? chosen : admin;
        campaign.OwnerId = to;
        if (campaign.Members.FirstOrDefault(m => m.UserId == to) is { } member) member.Role = CampaignRole.DM;
        else campaign.Members.Add(new CampaignMember { UserId = to, Role = CampaignRole.DM });
    }

    private async Task DeleteOwnedAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> mine)
    {
        while (true)
        {
            using var session = db.Store.OpenAsyncSession();
            var batch = await session.Query<T>().Customize(x => x.WaitForNonStaleResults()).Where(mine).Take(256).ToListAsync();
            if (batch.Count == 0) return;
            foreach (var doc in batch) session.Delete(doc!);
            await session.SaveChangesAsync();
        }
    }
}
