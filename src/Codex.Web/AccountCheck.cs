using System.Collections.Concurrent;
using System.Security.Claims;
using Codex.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace Codex.Web;

/// <summary>
/// Keeps a signed-in principal true to the stored account (docs/SERVER_ACCESS_AND_VAULT.md 3). The cookie carries the roles and
/// status from sign-in; an admin who approves, disables or changes a role changes the account, and this makes every request see it:
/// a disabled or deleted account is signed out, a changed one gets a new principal. An account is read at most every few seconds
/// per person; the admin page forgets an account it changed so that takes effect at once.
/// </summary>
public sealed class AccountCheck(IUserRepository users)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, (UserDocument? User, DateTime At)> seen = new(StringComparer.Ordinal);

    public async Task<UserDocument?> GetAsync(string userId)
    {
        if (seen.TryGetValue(userId, out var hit) && DateTime.UtcNow - hit.At < Fresh) return hit.User;
        var user = await users.GetUserByIdAsync(userId);
        seen[userId] = (user, DateTime.UtcNow);
        return user;
    }

    public void Forget(string userId) => seen.TryRemove(userId, out _);

    /// <summary>The cookie handler's check on every request.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var id = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (id == null) return;
        var check = context.HttpContext.RequestServices.GetRequiredService<AccountCheck>();
        var user = await check.GetAsync(id);
        if (user == null || user.Status == AccountStatus.Disabled)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        if (!ServerAccess.Matches(context.Principal!, user))
        {
            context.ReplacePrincipal(UserPrincipal.For(user));
            context.ShouldRenew = true;
        }
    }
}

/// <summary>An open page (a Blazor circuit) keeps the principal it started with; this ends it when the account is deleted, turned off
/// or put back to waiting, so the page reloads into sign-in or the waiting page. A role change shows on the next page load.</summary>
public sealed class AccountRevalidation(ILoggerFactory logs, AccountCheck check) : RevalidatingServerAuthenticationStateProvider(logs)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancel)
    {
        var id = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (id == null) return true;
        var user = await check.GetAsync(id);
        return user != null && user.Status != AccountStatus.Disabled && ServerAccess.IsWaiting(state.User) == (user.Status == AccountStatus.Pending);
    }
}
