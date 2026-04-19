using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;

namespace Codex.Tests.Authentication;

/// <summary>
/// Fixtures for OAuth testing without external dependencies.
/// </summary>
public static class OAuthTestFixtures
{
    /// <summary>
    /// Creates a mock external authentication result from Google/Apple/etc.
    /// </summary>
    public static AuthenticateResult CreateExternalAuthResult(
        string provider,
        string providerKey,
        string? email = null,
        string? name = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, providerKey),
        };

        if (!string.IsNullOrEmpty(email))
            claims.Add(new Claim(ClaimTypes.Email, email));

        if (!string.IsNullOrEmpty(name))
            claims.Add(new Claim(ClaimTypes.Name, name));

        var identity = new ClaimsIdentity(claims, provider);
        var principal = new ClaimsPrincipal(identity);

        var properties = new AuthenticationProperties
        {
            Items =
            {
                ["LoginProvider"] = provider,
                [".AuthScheme"] = provider
            }
        };

        return AuthenticateResult.Success(new AuthenticationTicket(principal, properties, provider));
    }

    /// <summary>
    /// Creates a failed external authentication result.
    /// </summary>
    public static AuthenticateResult CreateFailedAuthResult() =>
        AuthenticateResult.Fail("External authentication failed");

    /// <summary>
    /// Sample Google OAuth response (common scenario).
    /// </summary>
    public static AuthenticateResult CreateGoogleAuthResult(
        string googleId = "123456789",
        string email = "user@gmail.com",
        string name = "John Doe") =>
        CreateExternalAuthResult("Google", googleId, email, name);

    /// <summary>
    /// Sample Apple OAuth response (common scenario).
    /// </summary>
    public static AuthenticateResult CreateAppleAuthResult(
        string appleId = "001234.5678abcd.1234",
        string email = "user@privaterelay.appleid.com",
        string name = "Jane Doe") =>
        CreateExternalAuthResult("Apple", appleId, email, name);

    /// <summary>
    /// Sample case where email is returned but name is missing.
    /// </summary>
    public static AuthenticateResult CreatePartialAuthResult(
        string provider = "Google",
        string providerKey = "987654321",
        string email = "noname@example.com") =>
        CreateExternalAuthResult(provider, providerKey, email, null);
}
