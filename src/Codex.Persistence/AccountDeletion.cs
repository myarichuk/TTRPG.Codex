using Raven.Client.Documents;

namespace Codex.Persistence;

/// <summary>A campaign the account being deleted owns, and who may take it: its other GMs (co-GMs), in member order.</summary>
public sealed record OwnedCampaign(string CampaignId, string Name, IReadOnlyList<string> CoGms);

/// <summary>What deleting an account touches, for the admin to read before confirming.</summary>
public sealed record AccountDeletionPlan(IReadOnlyList<OwnedCampaign> Owned, int Memberships, int VaultCharacters);

public enum AccountDeletionResult
{
    Deleted,
    NotFound,

    /// <summary>A campaign it owns appeared while it was being deleted. Deleted by an admin, the account is left turned off, to be
    /// deleted again; deleting your own, it is turned back on.</summary>
    StillOwns,

    /// <summary>Deleting your own account: a campaign you own has no co-GM picked to take it (or none at all). Nothing was changed.</summary>
    NeedsNewOwner,

    /// <summary>The account is the server's only approved admin. Nothing was changed.</summary>
    LastAdmin
}

/// <summary>
/// Deleting an account (docs/SERVER_ACCESS_AND_VAULT.md 9, built in 15 and 16). Every campaign it owns passes to one of its co-GMs, and
/// when an admin deletes it, to that admin where none is picked, so no campaign is lost and none is left without an owner. Someone
/// deleting their own account picks a co-GM for each campaign they own; a campaign with none has to be passed on or deleted first,
/// since no one else chose to take it. It leaves every other campaign as a player who leaves would: its characters there stay in
/// the cast. What is its alone goes with it: the vault, its private journals, its world-builder chats and book builds. The account is
/// turned off first and deleted last, and its own things go only once it owns no campaign, so an attempt that stops half way leaves
/// a turned-off account to delete again, never a campaign whose owner is nobody.
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
    /// Deletes <paramref name="userId"/>. <paramref name="newOwners"/> names, per owned campaign, the co-GM who takes it. A campaign
    /// missing from it, or naming someone who is not a co-GM of it as stored now, goes to <paramref name="fallbackOwner"/> (the admin
    /// deleting the account), who becomes its owner and a GM member. With no <paramref name="fallbackOwner"/> (someone deleting their
    /// own account) every owned campaign needs a co-GM picked, or nothing is done.
    /// </summary>
    public async Task<AccountDeletionResult> DeleteAsync(string userId, string? fallbackOwner, IReadOnlyDictionary<string, string>? newOwners = null)
    {
        if (string.IsNullOrEmpty(userId) || userId == fallbackOwner) throw new ArgumentException("An admin does not delete their own account here.", nameof(userId));
        if (await users.GetUserByIdAsync(userId) is not { } user) return AccountDeletionResult.NotFound;
        if (await IsLastAdminAsync(user)) return AccountDeletionResult.LastAdmin;
        var theirs = (await campaigns.GetForUserAsync(userId)).ToList();
        if (fallbackOwner == null && theirs.Any(c => c.OwnerId == userId && !CoGms(c, userId).Contains(newOwners?.GetValueOrDefault(c.Id) ?? string.Empty)))
        {
            return AccountDeletionResult.NeedsNewOwner;
        }

        var status = user.Status;
        if (await users.UpdateUserAsync(userId, u => u.Status = AccountStatus.Disabled) == null) return AccountDeletionResult.NotFound;

        foreach (var campaign in theirs.Where(c => c.OwnerId == userId))
        {
            var chosen = newOwners?.GetValueOrDefault(campaign.Id);
            await campaigns.UpdateAsync(campaign.Id, c => HandOver(c, userId, chosen, fallbackOwner));
        }

        // Nothing of its own goes while it still owns a campaign: one made meanwhile, or a pick who stopped being a co-GM.
        if (await StillOwnsAsync(userId)) return await KeptAsync(userId, fallbackOwner, status);

        foreach (var campaign in (await campaigns.GetForUserAsync(userId)).ToList())
        {
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

        // Never an owner of nobody: a campaign made while this ran stops the deletion, and running it again passes that one on too.
        if (await StillOwnsAsync(userId)) return AccountDeletionResult.StillOwns;

        await users.DeleteAsync(userId);
        return AccountDeletionResult.Deleted;
    }

    // An admin's deletion leaves the account turned off, to be deleted again; your own turns it back on, as you left it.
    private async Task<AccountDeletionResult> KeptAsync(string userId, string? fallbackOwner, AccountStatus status)
    {
        if (fallbackOwner == null) await users.UpdateUserAsync(userId, u => u.Status = status);
        return AccountDeletionResult.StillOwns;
    }

    private async Task<bool> StillOwnsAsync(string userId)
    {
        using var session = db.Store.OpenAsyncSession();
        return await session.Query<CampaignDocument>().Customize(x => x.WaitForNonStaleResults()).AnyAsync(c => c.OwnerId == userId);
    }

    private async Task<bool> IsLastAdminAsync(UserDocument user)
    {
        if (!user.Roles.Contains(UserRoles.ServerAdmin) || user.Status != AccountStatus.Approved) return false;
        return !(await users.ListAsync()).Any(o => o.Id != user.Id && o.Roles.Contains(UserRoles.ServerAdmin) && o.Status == AccountStatus.Approved);
    }

    /// <summary>The GM members other than <paramref name="owner"/>: who can take the campaign over.</summary>
    public static IReadOnlyList<string> CoGms(CampaignDocument campaign, string owner) =>
        campaign.Members.Where(m => m.Role == CampaignRole.DM && m.UserId != owner).Select(m => m.UserId).Distinct().ToList();

    /// <summary>Passes <paramref name="campaign"/> from <paramref name="owner"/> to <paramref name="chosen"/> when that is one of its
    /// co-GMs now, otherwise to <paramref name="fallback"/>, made a GM member. Does nothing when <paramref name="owner"/> no longer owns
    /// it, or when the pick is no co-GM and there is no fallback.</summary>
    public static void HandOver(CampaignDocument campaign, string owner, string? chosen, string? fallback)
    {
        if (campaign.OwnerId != owner) return;
        var to = chosen != null && CoGms(campaign, owner).Contains(chosen) ? chosen : fallback;
        if (to == null) return;
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
