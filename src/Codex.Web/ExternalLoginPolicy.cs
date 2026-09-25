using System.Security.Claims;

namespace Codex.Web;

/// <summary>
/// Decision logic for external-login handling, split out from Program.cs so it can be
/// unit-tested without spinning up the whole authentication pipeline (B11 remediation).
/// </summary>
public static class ExternalLoginPolicy
{
    /// <summary>
    /// True only if the external identity provider asserts, via an "email_verified" claim,
    /// that the email on the principal has actually been verified. Auto-linking an external
    /// login to an existing local account by email is only safe when the provider vouches for
    /// the address — otherwise anyone who controls an unverified mailbox at a lax or
    /// misconfigured provider could take over an existing account.
    /// </summary>
    public static bool IsEmailVerified(ClaimsPrincipal principal)
    {
        var claim = principal.FindFirst("email_verified")
                    ?? principal.FindFirst("https://schemas.google.com/email_verified");

        return claim != null && bool.TryParse(claim.Value, out var verified) && verified;
    }

    /// <summary>
    /// True if <paramref name="provider"/> names one of the authentication schemes actually
    /// registered in the app. Prevents an arbitrary, attacker-controlled scheme name reaching
    /// Results.Challenge and causing an unhandled 500 (or worse, resolving to an unexpected
    /// scheme in a future refactor).
    /// </summary>
    public static bool IsRegisteredScheme(string? provider, IEnumerable<string> registeredSchemeNames)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }

        return registeredSchemeNames.Any(name => string.Equals(name, provider, StringComparison.Ordinal));
    }
}
