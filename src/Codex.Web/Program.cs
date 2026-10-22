using Codex.Core;
using Codex.Core.Scripting;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Codex.Core.AI;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();

// Authentication: a local cookie scheme plus whichever external providers are configured.
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
        // whether it's safe to auto-link this login to an existing local account by email.
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

        if (System.IO.File.Exists(applePrivateKey))
        {
            options.UsePrivateKey(
                (keyId) => new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetDirectoryName(applePrivateKey) ?? ".").GetFileInfo(Path.GetFileName(applePrivateKey))
            );
        }
        else if (applePrivateKey.Contains("-----BEGIN") || applePrivateKey.Contains("-----END"))
        {
            // PEM content: hand it to the Apple handler in memory so the key never touches disk.
            options.UsePrivateKey(
                (keyId) => new Codex.Web.InMemoryPemFileInfo(applePrivateKey, $"apple_key_{keyId}.p8")
            );
        }

        options.SignInScheme = "External";
    });
}

builder.Services.AddAuthorization(options =>
{
    // Everything requires a signed-in user unless a page opts out with [AllowAnonymous].
    // Authorisation inside a campaign is per-campaign membership (CampaignAccess), not a role.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddHttpContextAccessor();

// Persistence
var dataDir = builder.Configuration["Codex:DataDirectory"] ?? "RavenData";

builder.Services.AddSingleton(sp => new RavenDbService(dataDir, logger: sp.GetRequiredService<ILogger<RavenDbService>>()));
builder.Services.AddSingleton<CampaignLifecycleHooks>();
builder.Services.AddSingleton<ICampaignRepository, CampaignRepository>();
builder.Services.AddSingleton<IActorRepository, ActorRepository>();
builder.Services.AddSingleton<IUserRepository, RavenUserRepository>();
builder.Services.AddSingleton<ISessionRepository, RavenSessionRepository>();
builder.Services.AddSingleton<INoteRepository, RavenNoteRepository>();
builder.Services.AddSingleton<IRegionRepository, RegionRepository>();
builder.Services.AddSingleton<IFactRepository, FactRepository>();
builder.Services.AddSingleton<IEncounterRepository, RavenEncounterRepository>();
builder.Services.AddSingleton<IHomebrewRepository, RavenHomebrewRepository>();
builder.Services.AddSingleton<IWikiRepository, RavenWikiRepository>();
builder.Services.AddSingleton<IJournalRepository, RavenJournalRepository>();
builder.Services.AddSingleton<IMediaStore, RavenMediaStore>();
builder.Services.AddSingleton<CampaignExportService>();
builder.Services.AddScoped<ICampaignAccessResolver, CampaignAccessResolver>();
builder.Services.AddScoped<ViewerClock>();

// Game systems and content
builder.Services.AddSingleton<ComponentRegistry>();
builder.Services.AddSingleton<ISystemCatalog>(sp => sp.GetRequiredService<PluginLoader>());
builder.Services.AddSingleton<ScriptEvaluator>();
builder.Services.AddSingleton<IContentRegistry, ContentRegistry>();
builder.Services.AddSingleton<IContentPackLoader, YamlContentPackLoader>();
builder.Services.AddSingleton<PluginLoader>();
builder.Services.AddSingleton<ContentPackExporter>();
builder.Services.AddSingleton<CampaignLibrary>();
builder.Services.AddSingleton<Codex.Persistence.Runtime.CampaignRuntimeManager>();
builder.Services.AddHostedService<IdleRuntimeEvictionService>();

// AI: the "AI" configuration section is the server-wide fallback provider; users set up their
// own OpenAI-compatible providers (OpenRouter by default) under /settings/ai, keys protected by
// Data Protection. Nothing is sent to a model unless someone clicks an AI button.
var aiConfig = new AIConfiguration();
builder.Configuration.GetSection("AI").Bind(aiConfig);
builder.Services.AddSingleton(aiConfig);
builder.Services.AddDataProtection();
builder.Services.AddSingleton<AiProviderService>();
builder.Services.AddSingleton<AiFeatures>();
builder.Services.AddScoped<HomebrewImportService>();
builder.Services.AddSingleton<CastService>();

builder.Services.AddSignalR();
builder.Services.AddHttpClient();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
        .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
    logger.LogInformation("Server running at: {Addresses}", addresses is { Count: > 0 } ? string.Join(", ", addresses) : "(unknown)");
});

// The tray launcher keeps this process's stdin open and closes it to ask for a clean stop (there is
// no portable signal for that on Windows). It also stops the server if the launcher itself dies.
if (app.Configuration.GetValue<bool>("Codex:StopOnStdinClose"))
{
    _ = Task.Run(async () =>
    {
        try
        {
            while (await Console.In.ReadLineAsync() != null) { }
        }
        catch (IOException)
        {
        }

        logger.LogInformation("Stdin closed: stopping.");
        app.Lifetime.StopApplication();
    });
}

// Load systems (compiled plugins and system.yaml files) and content packs once at startup.
using (var scope = app.Services.CreateScope())
{
    var loader = scope.ServiceProvider.GetRequiredService<PluginLoader>();
    var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
    var roots = ContentRoots.Resolve(app.Configuration, env.ContentRootPath);

    logger.LogInformation("Loading systems and content packs from: {Roots}", string.Join(", ", roots));
    await loader.LoadAndInitializeAsync(roots);
    logger.LogInformation("Loaded systems: {Systems}", string.Join(", ", loader.LoadedSystemIds));
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

// Test-only seeding endpoints for the Playwright suites: opt-in via config, off by default, so a
// stray "Codex:EnableTestSeedEndpoint=true" can never leak into a real deployment's route table.
if (app.Configuration.GetValue<bool>("Codex:EnableTestSeedEndpoint"))
{
    app.MapPost("/test/seed/users", async (List<Codex.Web.TestSeedUser> users, IUserRepository userRepository) =>
    {
        foreach (var seed in users)
        {
            var user = new UserDocument { Id = seed.Id, Username = seed.Username, Roles = seed.Roles };
            user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<UserDocument>().HashPassword(user, seed.Password);
            await userRepository.TryReserveUsernameAsync(seed.Username, seed.Id);
            await userRepository.CreateUserAsync(user);
        }

        return Results.Ok();
    }).AllowAnonymous();

    app.MapPost("/test/seed/actors", async (List<Codex.Web.TestSeedActor> actors, IActorRepository actorRepository, ICampaignRepository campaignRepository, ComponentRegistry componentRegistry, ISystemCatalog catalog) =>
    {
        foreach (var seed in actors)
        {
            var campaign = await campaignRepository.GetAsync(seed.CampaignId);
            var system = campaign != null ? catalog.GetDefinition(campaign.SystemId) : null;
            var actor = new ActorDocument
            {
                Id = seed.Id,
                CampaignId = seed.CampaignId,
                Kind = seed.Kind,
                OwnerUserId = seed.OwnerUserId,
                Name = seed.Name,
                Visibility = seed.Visibility
            };

            foreach (var (key, value) in seed.Sheet ?? new Dictionary<string, object>())
            {
                actor.Sheet[key] = value is System.Text.Json.JsonElement element ? element.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    _ => element.ToString()
                } : value;
            }

            var primary = system?.PrimaryResource;
            actor.Sheet[primary?.Key ?? "HP"] = (long)seed.Hp;
            actor.Sheet[primary?.MaxKey ?? "HP_Max"] = (long)seed.HpMax;
            ActorSheetSync.ApplySheetToState(actor, system, componentRegistry);
            await actorRepository.SaveAsync(actor);
        }

        return Results.Ok();
    }).AllowAnonymous();

    // The session log isn't shown verbatim in any UI, so the e2e suite reads it back directly.
    app.MapGet("/test/inspect/session-events/{campaignId}", async (string campaignId, ICampaignRepository campaignRepository, ISessionRepository sessionRepository) =>
    {
        var campaign = await campaignRepository.GetAsync(campaignId);
        if (campaign?.CurrentSessionId == null)
        {
            return Results.Ok(Array.Empty<string>());
        }

        var session = await sessionRepository.GetAsync(campaign.CurrentSessionId);
        return Results.Ok(session?.Events.Select(e => e.Type).ToList() ?? new List<string>());
    }).AllowAnonymous();
}

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
});

app.MapGet("/login/external", async (string provider, string? returnUrl, Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider schemeProvider) =>
{
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

    // Only auto-link to an existing local account by email if the provider vouches the email is verified.
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

Codex.Web.MediaEndpoints.MapMedia(app);

app.MapRazorComponents<Codex.Web.Components.App>().AddInteractiveServerRenderMode();

// Daily RavenDB backup into the data dir. Best-effort: a backup that can't be configured must never
// keep the table from starting.
try
{
    var backupDir = Path.GetFullPath(Path.Combine(dataDir, "Backups"));
    using var backupScope = app.Services.CreateScope();
    await backupScope.ServiceProvider.GetRequiredService<RavenDbService>().EnsureScheduledBackupAsync(backupDir);
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Scheduled backup configuration failed; continuing without it.");
}

app.Run();
