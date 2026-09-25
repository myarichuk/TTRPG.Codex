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
}
