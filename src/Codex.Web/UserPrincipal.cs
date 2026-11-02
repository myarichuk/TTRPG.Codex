using System.Security.Claims;
using Codex.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Codex.Web;

/// <summary>The cookie identity of a signed-in user: their name, id, email when known, and server roles. Sign-in, sign-up
/// and an external sign-in all make it here, so the three never carry different claims.</summary>
public static class UserPrincipal
{
    public static ClaimsPrincipal For(UserDocument user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, user.Id)
        };
        if (!string.IsNullOrEmpty(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
