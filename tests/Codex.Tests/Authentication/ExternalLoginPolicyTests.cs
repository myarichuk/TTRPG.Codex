using System.Security.Claims;
using Codex.Web;
using Xunit;

namespace Codex.Tests.Authentication;

public class ExternalLoginPolicyTests
{
    private static ClaimsPrincipal PrincipalWith(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "External"));

    [Fact]
    public void IsEmailVerified_NoClaim_ReturnsFalse()
    {
        var principal = PrincipalWith(new Claim(ClaimTypes.Email, "user@example.com"));

        Assert.False(ExternalLoginPolicy.IsEmailVerified(principal));
    }

    [Fact]
    public void IsEmailVerified_ClaimFalse_ReturnsFalse()
    {
        var principal = PrincipalWith(
            new Claim(ClaimTypes.Email, "user@example.com"),
            new Claim("email_verified", "false"));

        Assert.False(ExternalLoginPolicy.IsEmailVerified(principal));
    }

    [Fact]
    public void IsEmailVerified_ClaimTrue_ReturnsTrue()
    {
        var principal = PrincipalWith(
            new Claim(ClaimTypes.Email, "user@example.com"),
            new Claim("email_verified", "true"));

        Assert.True(ExternalLoginPolicy.IsEmailVerified(principal));
    }

    [Theory]
    [InlineData("Google", true)]
    [InlineData("Apple", true)]
    [InlineData("EvilScheme", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsRegisteredScheme_OnlyMatchesRegistered(string? provider, bool expected)
    {
        var registered = new[] { "Google", "Apple", "Cookies", "External" };

        Assert.Equal(expected, ExternalLoginPolicy.IsRegisteredScheme(provider, registered));
    }

    [Fact]
    public void ProviderOf_ReadsTheSchemeTheRemoteHandlerRecords()
    {
        var result = OAuthTestFixtures.CreateGoogleAuthResult();

        Assert.False(result.Properties!.Items.ContainsKey("LoginProvider"));
        Assert.Equal("Google", ExternalLoginPolicy.ProviderOf(result.Properties, result.Principal!));
    }

    [Fact]
    public void ProviderOf_FallsBackToTheIdentitysAuthenticationType()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "Apple"));

        Assert.Equal("Apple", ExternalLoginPolicy.ProviderOf(new Microsoft.AspNetCore.Authentication.AuthenticationProperties(), principal));
        Assert.Equal("Apple", ExternalLoginPolicy.ProviderOf(null, principal));
    }
}
