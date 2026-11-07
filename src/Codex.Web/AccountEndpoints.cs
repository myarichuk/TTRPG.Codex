using Codex.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Codex.Web;

/// <summary>Signing in and out, and the reader's language: the endpoints the sign-in pages post and link to.</summary>
public static class AccountEndpoints
{
    public static void MapAccounts(this WebApplication app)
    {
        // Sign-out is a POST with an antiforgery token so a cross-site <img> can't log a user out.
        app.MapPost("/logout", async (HttpContext ctx, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(ctx);
            }
            catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
            {
                return Results.BadRequest("Invalid antiforgery token.");
            }

            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).AllowAnonymous(); // an account waiting for approval must be able to sign out; the token still guards it

        // Changing language is harmless, so a plain GET (a link in the header and on the sign-in pages) is enough.
        // It sets the cookie, remembers the choice on the signed-in user, then returns to a local page.
        app.MapGet("/set-culture", async (string culture, string? returnUrl, HttpContext ctx, IUserRepository users) =>
        {
            var tag = Codex.Localization.SupportedCultures.Normalize(culture);
            Codex.Web.CultureCookie.Write(ctx, tag);

            var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (userId is not null && await users.GetUserByIdAsync(userId) is { } user && user.Culture != tag)
            {
                await users.UpdateUserAsync(user.Id, stored => stored.Culture = tag);
            }

            return Results.LocalRedirect(Codex.Web.UrlHelper.IsLocalUrl(returnUrl) ? returnUrl! : "/");
        }).AllowAnonymous();

        app.MapGet("/login/external", async (string provider, string? returnUrl, Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider schemeProvider) =>
        {
            var registeredSchemes = (await schemeProvider.GetAllSchemesAsync()).Select(s => s.Name);
            if (!Codex.Web.ExternalLoginPolicy.IsRegisteredScheme(provider, registeredSchemes))
            {
                return Results.Redirect("/login?error=unknown-provider");
            }

            var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties
            {
                RedirectUri = $"/login/external-callback?returnUrl={Uri.EscapeDataString(returnUrl ?? "/")}"
            };
            return Results.Challenge(properties, new[] { provider });
        }).AllowAnonymous();

        app.MapGet("/login/external-callback", async (HttpContext context, IUserRepository userRepository, ICampaignRepository campaignRepository, IConfiguration config, string? returnUrl) =>
        {
            var authenticateResult = await context.AuthenticateAsync("External");

            if (!authenticateResult.Succeeded || authenticateResult.Principal == null)
            {
                return Results.Redirect("/login?error=external-failed");
            }

            var principal = authenticateResult.Principal;
            var provider = Codex.Web.ExternalLoginPolicy.ProviderOf(authenticateResult.Properties, principal);
            var providerKey = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                              ?? principal.FindFirst("sub")?.Value
                              ?? principal.FindFirst("id")?.Value;
            var email = principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
            var name = principal.Identity?.Name ?? email;

            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(providerKey))
            {
                return Results.Redirect("/login?error=external-invalid");
            }

            var user = await userRepository.GetUserByExternalLoginAsync(provider, providerKey);

            // Only auto-link to an existing local account by email if the provider vouches the email is verified.
            if (user == null && !string.IsNullOrEmpty(email) && Codex.Web.ExternalLoginPolicy.IsEmailVerified(principal))
            {
                user = await userRepository.GetUserByEmailAsync(email);
                if (user != null)
                {
                    user = await userRepository.UpdateUserAsync(user.Id, stored =>
                    {
                        if (!stored.ExternalLogins.Any(e => e.Provider == provider && e.ProviderKey == providerKey))
                        {
                            stored.ExternalLogins.Add(new ExternalLogin { Provider = provider, ProviderKey = providerKey });
                        }
                    });
                }
            }

            var firstAdmin = false;
            if (user == null)
            {
                // The sign-up rule (docs/SERVER_ACCESS_AND_VAULT.md 3): open to the internet, only with a real invite.
                var exposed = Codex.Web.ExposurePolicy.IsExposed(config);
                var settingsStore = context.RequestServices.GetRequiredService<IServerSettingsStore>();
                var noAccounts = !await userRepository.AnyUsersExistAsync();
                var status = await Codex.Web.ServerAccess.NewAccountAsync(settingsStore, exposed, returnUrl, campaignRepository);
                // The very first account, made on this computer, is the admin's, whatever the rule says.
                var mayBeFirstAdmin = string.IsNullOrWhiteSpace(config["Codex:AdminUsername"]) && Codex.Web.ExposurePolicy.MayClaimFirstAdmin(exposed, context.Connection.RemoteIpAddress);
                // A server with no accounts takes only its admin's, from this computer; otherwise the rule decides.
                if (noAccounts ? !mayBeFirstAdmin : status == null)
                {
                    await context.SignOutAsync("External");
                    return Results.Redirect("/login?error=invite-only");
                }

                var baseUsername = !string.IsNullOrEmpty(name) ? name : ("User" + Guid.NewGuid().ToString().Substring(0, 8));
                user = new UserDocument
                {
                    Id = Guid.NewGuid().ToString(),
                    Username = baseUsername,
                    Email = email ?? "",
                    Roles = new List<string> { UserRoles.Player },
                    Status = status ?? AccountStatus.Pending,
                    CreatedAt = DateTime.UtcNow,
                    Culture = Codex.Localization.SupportedCultures.Normalize(System.Globalization.CultureInfo.CurrentUICulture.Name),
                    ExternalLogins = new List<ExternalLogin> { new ExternalLogin { Provider = provider, ProviderKey = providerKey } }
                };

                var attempt = 0;
                while (!await userRepository.TryReserveUsernameAsync(user.Username, user.Id))
                {
                    attempt++;
                    user.Username = $"{baseUsername}_{Guid.NewGuid().ToString().Substring(0, 4)}";
                    if (attempt > 5)
                    {
                        return Results.Redirect("/login?error=no-username");
                    }
                }

                // The first account made on this computer runs the server, but never on an open one (R13). The name is the identity
                // provider's display name, which the person signing in chooses, so unlike the sign-up form it is never matched
                // against Codex:AdminUsername.
                if (mayBeFirstAdmin && await userRepository.TryClaimFirstAdminAsync(user.Id))
                {
                    user.Roles.Add(UserRoles.ServerAdmin);
                    user.Roles.Add(UserRoles.GameMaster);
                    user.Status = AccountStatus.Approved;
                    firstAdmin = !(await settingsStore.GetAsync()).SetupDone;
                }
                else if (status == null)
                {
                    // Someone else took the seat between the check and now.
                    await context.SignOutAsync("External");
                    return Results.Redirect("/login?error=invite-only");
                }

                await userRepository.CreateUserAsync(user);
            }
            else if (user.Status == AccountStatus.Disabled)
            {
                await context.SignOutAsync("External");
                return Results.Redirect("/login?error=disabled");
            }

            var newPrincipal = Codex.Web.UserPrincipal.For(user);

            await context.SignInAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, newPrincipal);
            await context.SignOutAsync("External");
            if (user.Culture is not null)
                Codex.Web.CultureCookie.Write(context, user.Culture);

            if (!Codex.Web.UrlHelper.IsLocalUrl(returnUrl))
            {
                returnUrl = "/";
            }

            if (user.Status == AccountStatus.Pending && Codex.Web.ExposurePolicy.InviteCodeFrom(returnUrl) == null) return Results.Redirect(Codex.Web.ServerAccess.WaitingPath);
            if (firstAdmin) return Results.Redirect("/setup/server");
            return Results.Redirect(returnUrl ?? "/");
        }).AllowAnonymous();
    }
}
