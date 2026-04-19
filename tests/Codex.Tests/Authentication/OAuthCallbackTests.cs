using Codex.Persistence;
using Codex.Tests.Authentication;
using Xunit;

namespace Codex.Tests.Authentication;

public class OAuthCallbackTests
{
    private readonly MockUserRepository _userRepository = new();

    private void ResetRepository() => _userRepository.Reset();

    [Fact]
    public async Task NewUserFromGoogle_CreatesUserAndExternalLogin()
    {
        ResetRepository();
        var authResult = OAuthTestFixtures.CreateGoogleAuthResult(
            googleId: "google123",
            email: "newuser@gmail.com",
            name: "New User");

        Assert.True(authResult.Succeeded);
        var principal = authResult.Principal;
        var provider = "Google";
        var providerKey = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var email = principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        var name = principal.Identity?.Name;

        // Simulate the external callback logic
        var user = await _userRepository.GetUserByExternalLoginAsync(provider, providerKey!);
        
        if (user == null && !string.IsNullOrEmpty(email))
        {
            user = await _userRepository.GetUserByEmailAsync(email);
            if (user != null)
            {
                user.ExternalLogins.Add(new ExternalLogin { Provider = provider, ProviderKey = providerKey! });
                await _userRepository.UpdateUserAsync(user);
            }
        }

        if (user == null)
        {
            user = new UserDocument
            {
                Id = Guid.NewGuid().ToString(),
                Username = name ?? "User" + Guid.NewGuid().ToString().Substring(0, 8),
                Email = email ?? "",
                Roles = new List<string> { "Player" },
                ExternalLogins = new List<ExternalLogin> 
                { 
                    new ExternalLogin { Provider = provider, ProviderKey = providerKey! } 
                }
            };
            await _userRepository.CreateUserAsync(user);
        }

        // Assert
        Assert.NotNull(user);
        Assert.Equal("New User", user.Username);
        Assert.Equal("newuser@gmail.com", user.Email);
        Assert.Single(user.ExternalLogins);
        Assert.Equal("Google", user.ExternalLogins[0].Provider);
        Assert.Equal("google123", user.ExternalLogins[0].ProviderKey);
    }

    [Fact]
    public async Task ExistingUserWithEmail_LinksExternalLogin()
    {
        ResetRepository();
        
        // Create existing user
        var existingUser = new UserDocument
        {
            Id = "existing-id",
            Username = "existing_user",
            Email = "existing@gmail.com",
            Roles = new List<string> { "Player" },
            ExternalLogins = new List<ExternalLogin>()
        };
        await _userRepository.CreateUserAsync(existingUser);

        // Simulate Google OAuth callback with same email
        var authResult = OAuthTestFixtures.CreateGoogleAuthResult(
            googleId: "google123",
            email: "existing@gmail.com",
            name: "Existing User");

        var principal = authResult.Principal;
        var provider = "Google";
        var providerKey = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var email = principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

        var user = await _userRepository.GetUserByExternalLoginAsync(provider, providerKey!);

        if (user == null && !string.IsNullOrEmpty(email))
        {
            user = await _userRepository.GetUserByEmailAsync(email);
            if (user != null)
            {
                user.ExternalLogins.Add(new ExternalLogin { Provider = provider, ProviderKey = providerKey! });
                await _userRepository.UpdateUserAsync(user);
            }
        }

        // Assert
        Assert.NotNull(user);
        Assert.Equal("existing_user", user.Username);
        Assert.Single(user.ExternalLogins);
        Assert.Equal("Google", user.ExternalLogins[0].Provider);
    }

    [Fact]
    public async Task KnownExternalLogin_ReturnsExistingUser()
    {
        ResetRepository();

        // Create user with existing external login
        var existingUser = new UserDocument
        {
            Id = "existing-id",
            Username = "google_user",
            Email = "user@gmail.com",
            Roles = new List<string> { "Player" },
            ExternalLogins = new List<ExternalLogin>
            {
                new ExternalLogin { Provider = "Google", ProviderKey = "google123" }
            }
        };
        await _userRepository.CreateUserAsync(existingUser);

        // Simulate same Google OAuth callback
        var authResult = OAuthTestFixtures.CreateGoogleAuthResult(
            googleId: "google123",
            email: "user@gmail.com",
            name: "Google User");

        var principal = authResult.Principal;
        var provider = "Google";
        var providerKey = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        var user = await _userRepository.GetUserByExternalLoginAsync(provider, providerKey!);

        // Assert
        Assert.NotNull(user);
        Assert.Equal("existing-id", user.Id);
        Assert.Equal("google_user", user.Username);
    }

    [Fact]
    public void FailedAuth_ReturnsFalse()
    {
        var authResult = OAuthTestFixtures.CreateFailedAuthResult();

        Assert.False(authResult.Succeeded);
    }

    [Fact]
    public async Task PartialAuthResult_CreatesUserWithGeneratedUsername()
    {
        ResetRepository();

        var authResult = OAuthTestFixtures.CreatePartialAuthResult(
            provider: "Google",
            providerKey: "google456",
            email: "noname@example.com");

        var principal = authResult.Principal;
        var provider = "Google";
        var providerKey = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var email = principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        var name = principal.Identity?.Name ?? email;

        var user = new UserDocument
        {
            Id = Guid.NewGuid().ToString(),
            Username = !string.IsNullOrEmpty(name) ? name : ("User" + Guid.NewGuid().ToString().Substring(0, 8)),
            Email = email ?? "",
            Roles = new List<string> { "Player" },
            ExternalLogins = new List<ExternalLogin>
            {
                new ExternalLogin { Provider = provider, ProviderKey = providerKey! }
            }
        };
        await _userRepository.CreateUserAsync(user);

        // Assert
        Assert.NotNull(user);
        Assert.Equal("noname@example.com", user.Username); // Falls back to email
        Assert.Equal("noname@example.com", user.Email);
    }

    [Fact]
    public async Task MultipleExternalLogins_SameUser()
    {
        ResetRepository();

        var user = new UserDocument
        {
            Id = "multi-id",
            Username = "multi_user",
            Email = "user@example.com",
            Roles = new List<string> { "Player" },
            ExternalLogins = new List<ExternalLogin>
            {
                new ExternalLogin { Provider = "Google", ProviderKey = "google123" }
            }
        };
        await _userRepository.CreateUserAsync(user);

        // Link Apple login to same user
        user.ExternalLogins.Add(new ExternalLogin { Provider = "Apple", ProviderKey = "apple456" });
        await _userRepository.UpdateUserAsync(user);

        // Verify both logins work
        var byGoogle = await _userRepository.GetUserByExternalLoginAsync("Google", "google123");
        var byApple = await _userRepository.GetUserByExternalLoginAsync("Apple", "apple456");

        Assert.NotNull(byGoogle);
        Assert.NotNull(byApple);
        Assert.Equal(byGoogle.Id, byApple.Id);
        Assert.Equal(2, byGoogle.ExternalLogins.Count);
    }
}
