using Codex.Core;
using Codex.Core.Scripting;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Codex.Core.AI;
using Microsoft.Extensions.AI;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();

// Configure authentication and cookies using a single AuthenticationBuilder.
var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
    })
    .AddCookie("External", options =>
    {
        options.Cookie.Name = "ExternalAuthCookie";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
    });

// Register external providers conditionally based on configuration.
var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrEmpty(googleClientId) && !string.IsNullOrEmpty(googleClientSecret))
{
    authBuilder.AddGoogle(options =>
    {
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
        options.SignInScheme = "External";
        // Surface Google's own verification flag as a claim so the callback can decide
        // whether it's safe to auto-link this login to an existing local account by email (B11).
        options.ClaimActions.MapJsonKey("email_verified", "email_verified");
    });
}

var appleClientId = builder.Configuration["Authentication:Apple:ClientId"];
var appleTeamId = builder.Configuration["Authentication:Apple:TeamId"];
var appleKeyId = builder.Configuration["Authentication:Apple:KeyId"];
var applePrivateKey = builder.Configuration["Authentication:Apple:PrivateKey"];
if (!string.IsNullOrEmpty(appleClientId) && !string.IsNullOrEmpty(appleTeamId) && !string.IsNullOrEmpty(appleKeyId) && !string.IsNullOrEmpty(applePrivateKey))
{
    authBuilder.AddApple(options =>
    {
        options.ClientId = appleClientId;
        options.KeyId = appleKeyId;
        options.TeamId = appleTeamId;

        // Handle private key: can be a file path or PEM content
        if (System.IO.File.Exists(applePrivateKey))
        {
            // Treat as file path
            options.UsePrivateKey(
                (keyId) => new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetDirectoryName(applePrivateKey) ?? ".").GetFileInfo(Path.GetFileName(applePrivateKey))
            );
        }
        else if (applePrivateKey.Contains("-----BEGIN") || applePrivateKey.Contains("-----END"))
        {
            // Treat as PEM content. Hand it to the Apple handler as an in-memory file so the
            // key material never touches disk (B11: this used to write an undeleted temp file
            // under Path.GetTempPath() on every startup).
            options.UsePrivateKey(
                (keyId) => new Codex.Web.InMemoryPemFileInfo(applePrivateKey, $"apple_key_{keyId}.p8")
            );
        }

        options.SignInScheme = "External";
    });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("DM", policy => policy.RequireRole("DM"));

    // Require authentication by default, and explicitly allow anonymous on login/register/etc.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddHttpContextAccessor();

// Configure Codex
var dataDir = builder.Configuration["Codex:DataDirectory"] ?? "RavenData";

builder.Services.AddSingleton(sp => new RavenDbService(dataDir, logger: sp.GetRequiredService<ILogger<RavenDbService>>()));
builder.Services.AddSingleton<ICampaignRepository, CampaignRepository>();
builder.Services.AddSingleton<ICharacterRepository, CharacterRepository>();
builder.Services.AddSingleton<IUserRepository, RavenUserRepository>();
builder.Services.AddSingleton<ISessionRepository, RavenSessionRepository>();
builder.Services.AddSingleton<INoteRepository, RavenNoteRepository>();

builder.Services.AddSingleton<ComponentRegistry>();
builder.Services.AddSingleton<ScriptEvaluator>();
builder.Services.AddSingleton<IContentRegistry, ContentRegistry>();
builder.Services.AddSingleton<IContentPackLoader, YamlContentPackLoader>();
builder.Services.AddSingleton<PluginLoader>();
builder.Services.AddSingleton<CodexWorld>();

// Configure AI services
var aiConfig = new AIConfiguration();
builder.Configuration.GetSection("AI").Bind(aiConfig);
builder.Services.AddSingleton(aiConfig);

var chatClient = AIClientFactory.CreateClient(aiConfig);
if (chatClient != null)
{
    builder.Services.AddSingleton<IChatClient>(chatClient);
}

builder.Services.AddSingleton<LoreGenerator>(sp =>
{
    var chatClient = sp.GetService<IChatClient>();
    return new LoreGenerator(chatClient);
});

// Add SignalR explicitly (already added by AddServerSideBlazor, but doing it for clarity)
builder.Services.AddSignalR();
builder.Services.AddHttpClient();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();

// B18: this used to hardcode "http://localhost:5000", which hasn't been the bound port since
// launchSettings.json moved to 5183. Log whatever Kestrel is actually bound to once it starts.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
        .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
    logger.LogInformation("Server running at: {Addresses}", addresses is { Count: > 0 } ? string.Join(", ", addresses) : "(unknown)");
});

// Initialize Plugins and World once at startup
using (var scope = app.Services.CreateScope())
{
    var loader = scope.ServiceProvider.GetRequiredService<PluginLoader>();
    var world = scope.ServiceProvider.GetRequiredService<CodexWorld>();
    var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    // B10: a published, single-file app has no "../../plugins" two levels above its content
    // root - that path only makes sense running from source under bin/Debug/net10.0/. Prefer a
    // "plugins" folder sitting right next to the published executable; only fall back to the
    // configured (dev-time) path if that doesn't exist.
    var besidePublishedApp = Path.Combine(AppContext.BaseDirectory, "plugins");
    string absolutePluginsDir;
    if (Directory.Exists(besidePublishedApp))
    {
        absolutePluginsDir = besidePublishedApp;
    }
    else
    {
        var pluginsPath = config["Codex:PluginsDirectory"] ?? "plugins";
        absolutePluginsDir = Path.GetFullPath(Path.Combine(env.ContentRootPath, pluginsPath));
    }

    logger.LogInformation("Loading plugins and content packs from: {Path}", absolutePluginsDir);
    await loader.LoadAndInitializeAsync(absolutePluginsDir, world);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Robust sign-out: do it on a normal HTTP request so cookies can be cleared reliably.
// POST + antiforgery (B11): a GET logout can be triggered cross-site by a bare <img>/<a> tag.
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
});

app.MapGet("/login/external", async (string provider, string? returnUrl, Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider schemeProvider) =>
{
    // B11: whitelist against schemes actually registered instead of handing an
    // attacker-controlled string straight to Results.Challenge (which 500s on an unknown scheme).
    var registeredSchemes = (await schemeProvider.GetAllSchemesAsync()).Select(s => s.Name);
    if (!Codex.Web.ExternalLoginPolicy.IsRegisteredScheme(provider, registeredSchemes))
    {
        return Results.Redirect("/login?error=" + Uri.EscapeDataString("Unknown login provider."));
    }

    var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties
    {
        RedirectUri = $"/login/external-callback?returnUrl={Uri.EscapeDataString(returnUrl ?? "/")}"
    };
    return Results.Challenge(properties, new[] { provider });
});

app.MapGet("/login/external-callback", async (HttpContext context, IUserRepository userRepository, string? returnUrl) =>
{
    var authenticateResult = await context.AuthenticateAsync("External");

    if (!authenticateResult.Succeeded || authenticateResult.Principal == null)
    {
        return Results.Redirect("/login?error=" + Uri.EscapeDataString("External authentication failed."));
    }

    var principal = authenticateResult.Principal;
    var provider = authenticateResult.Properties?.Items["LoginProvider"]
                   ?? authenticateResult.Properties?.Items[".AuthScheme"]
                   ?? principal.Identities.FirstOrDefault()?.AuthenticationType;
    var providerKey = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                      ?? principal.FindFirst("sub")?.Value
                      ?? principal.FindFirst("id")?.Value;
    var email = principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
    var name = principal.Identity?.Name ?? email;

    if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(providerKey))
    {
        return Results.Redirect("/login?error=" + Uri.EscapeDataString("Invalid external authentication response."));
    }

    var user = await userRepository.GetUserByExternalLoginAsync(provider, providerKey);

    // B11: only auto-link to an existing local account by email if the provider itself
    // vouches that the email is verified. An unverified email lets an attacker who controls
    // that mailbox at the provider take over an existing local account.
    if (user == null && !string.IsNullOrEmpty(email) && Codex.Web.ExternalLoginPolicy.IsEmailVerified(principal))
    {
        user = await userRepository.GetUserByEmailAsync(email);
        if (user != null)
        {
            user.ExternalLogins.Add(new ExternalLogin { Provider = provider, ProviderKey = providerKey });
            await userRepository.UpdateUserAsync(user);
        }
    }

    if (user == null)
    {
        var baseUsername = !string.IsNullOrEmpty(name) ? name : ("User" + Guid.NewGuid().ToString().Substring(0, 8));
        user = new UserDocument
        {
            Id = Guid.NewGuid().ToString(),
            Username = baseUsername,
            Email = email ?? "",
            Roles = new List<string> { "Player" },
            ExternalLogins = new List<ExternalLogin> { new ExternalLogin { Provider = provider, ProviderKey = providerKey } }
        };

        // B12: reserve the username atomically, retrying with a fresh suffix on collision
        // instead of the old query-then-insert race.
        var attempt = 0;
        while (!await userRepository.TryReserveUsernameAsync(user.Username, user.Id))
        {
            attempt++;
            user.Username = $"{baseUsername}_{Guid.NewGuid().ToString().Substring(0, 4)}";
            if (attempt > 5)
            {
                return Results.Redirect("/login?error=" + Uri.EscapeDataString("Could not allocate a username. Please try again."));
            }
        }

        await userRepository.CreateUserAsync(user);
    }

    var claims = new List<System.Security.Claims.Claim>
    {
        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, user.Username),
        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id)
    };
    if (!string.IsNullOrEmpty(user.Email))
    {
        claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, user.Email));
    }

    foreach (var role in user.Roles)
    {
        claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role));
    }

    var identity = new System.Security.Claims.ClaimsIdentity(claims, Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
    var newPrincipal = new System.Security.Claims.ClaimsPrincipal(identity);

    await context.SignInAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, newPrincipal);
    await context.SignOutAsync("External");

    if (!Codex.Web.UrlHelper.IsLocalUrl(returnUrl))
    {
        returnUrl = "/";
    }

    return Results.Redirect(returnUrl ?? "/");
});

app.MapRazorComponents<Codex.Web.Components.App>().AddInteractiveServerRenderMode();

app.Run();
