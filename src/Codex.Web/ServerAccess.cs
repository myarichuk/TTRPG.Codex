using System.Security.Claims;
using Codex.Persistence;

namespace Codex.Web;

/// <summary>
/// Who may make an account, use the server and create campaigns (docs/SERVER_ACCESS_AND_VAULT.md 3), on top of
/// <see cref="ExposurePolicy"/>: while the server is open to the internet a new account still needs a real campaign invite, whatever
/// the sign-up rule says.
/// </summary>
public static class ServerAccess
{
    /// <summary>On the principal of an account that waits for approval; absent on every other.</summary>
    public const string StatusClaim = "codex:status";

    /// <summary>The fallback authorization policy's name for "signed in and approved"; <see cref="SignedIn"/> lets a waiting account in too.</summary>
    public const string SignedIn = "codex:signed-in";

    public const string WaitingPath = "/waiting";

    /// <summary>What a new account starts as, or null when it may not be made. An invite counts as approval to join that campaign as a
    /// player: it is the Game Master who let them in.</summary>
    public static AccountStatus? NewAccount(ServerSettingsDocument settings, bool exposed, bool hasInvite)
    {
        if (hasInvite) return AccountStatus.Approved;
        if (exposed) return null;
        return settings.SignUp switch
        {
            SignUpRule.Open => AccountStatus.Approved,
            SignUpRule.Approval => AccountStatus.Pending,
            _ => null
        };
    }

    public static async Task<AccountStatus?> NewAccountAsync(IServerSettingsStore settings, bool exposed, string? returnUrl, ICampaignRepository campaigns) =>
        NewAccount(await settings.GetAsync(), exposed,
            ExposurePolicy.InviteCodeFrom(returnUrl) is { } code && await campaigns.InviteCodeExistsAsync(code));

    /// <summary>Whether the account may create a campaign, from the stored account (not the cookie's roles, which may be older).</summary>
    public static bool MayCreateCampaigns(UserDocument? user, ServerSettingsDocument settings) =>
        user is { Status: AccountStatus.Approved }
        && (settings.CampaignCreation == CampaignCreationRule.Anyone
            || user.Roles.Contains(UserRoles.GameMaster)
            || user.Roles.Contains(UserRoles.ServerAdmin));

    public static bool IsWaiting(ClaimsPrincipal? user) => user?.HasClaim(StatusClaim, nameof(AccountStatus.Pending)) == true;

    /// <summary>Whether the principal still says what the stored account says: the same status and roles.</summary>
    public static bool Matches(ClaimsPrincipal principal, UserDocument user)
    {
        if (IsWaiting(principal) != (user.Status == AccountStatus.Pending)) return false;
        var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        return roles.SetEquals(user.Roles) && principal.FindFirst(ClaimTypes.Name)?.Value == user.Username;
    }

    /// <summary>Whether the first-time setup may run for this visitor: nobody has an account yet, the server is not open to the
    /// internet, and the visitor is on this computer (the rule the first admin seat already follows).</summary>
    public static async Task<bool> MaySetUpAsync(IUserRepository users, bool exposed, System.Net.IPAddress? client) =>
        ExposurePolicy.MayClaimFirstAdmin(exposed, client) && !await users.AnyUsersExistAsync();
}

/// <summary>What changed between two copies of the settings, for the admin log: setting names and values as stored, not translated.</summary>
public static class SettingsChange
{
    public static string? Describe(ServerSettingsDocument before, ServerSettingsDocument after)
    {
        var parts = new List<string>();
        if (before.SignUp != after.SignUp) parts.Add($"sign-up: {before.SignUp} → {after.SignUp}");
        if (before.CampaignCreation != after.CampaignCreation) parts.Add($"campaigns: {before.CampaignCreation} → {after.CampaignCreation}");
        if (before.ServerAi != after.ServerAi) parts.Add($"server AI key: {before.ServerAi?.ToString() ?? "as before"} → {after.ServerAi?.ToString() ?? "as before"}");
        if (before.WebFetch != after.WebFetch) parts.Add($"fetching: {before.WebFetch} → {after.WebFetch}");
        var added = after.FetchSites.Except(before.FetchSites, StringComparer.Ordinal).ToList();
        var removed = before.FetchSites.Except(after.FetchSites, StringComparer.Ordinal).ToList();
        if (added.Count > 0) parts.Add("sites added: " + string.Join(", ", added));
        if (removed.Count > 0) parts.Add("sites removed: " + string.Join(", ", removed));
        if (before.SetupDone != after.SetupDone && after.SetupDone) parts.Add("setup finished");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
