using Codex.Core;
using Codex.Core.Scripting;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;
using Codex.Core.AI;
using Microsoft.Extensions.AI;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();

// i18n (6.5): component string localizers resolve against Resources/; without a
// matching .resx the key itself (English) renders, so untranslated pages keep working.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

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
    // (plus the /_blazor framework endpoints - see AuthenticatedOrBlazorFramework).
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .AddRequirements(new Codex.Web.AuthenticatedOrBlazorFramework())
        .Build();
});
builder.Services.AddSingleton<IAuthorizationHandler, Codex.Web.BlazorFrameworkEndpointHandler>();
builder.Services.AddHttpContextAccessor();

// Configure Codex
var dataDir = builder.Configuration["Codex:DataDirectory"] ?? "RavenData";
if (!Path.IsPathRooted(dataDir))
{
    // Anchor relative paths to the content root so the database lands in a predictable
    // place no matter which working directory the process was launched from.
    dataDir = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, dataDir));
}

builder.Services.AddSingleton(sp => new RavenDbService(dataDir, logger: sp.GetRequiredService<ILogger<RavenDbService>>()));
builder.Services.AddSingleton<ICampaignRepository, CampaignRepository>();
builder.Services.AddSingleton<IActorRepository, ActorRepository>();
builder.Services.AddSingleton<IUserRepository, RavenUserRepository>();
builder.Services.AddSingleton<ISessionRepository, RavenSessionRepository>();
builder.Services.AddSingleton<INoteRepository, RavenNoteRepository>();
builder.Services.AddSingleton<IRegionRepository, RegionRepository>();
builder.Services.AddSingleton<IFactRepository, FactRepository>();
builder.Services.AddSingleton<IEncounterRepository, RavenEncounterRepository>();
builder.Services.AddSingleton<CampaignExportService>();
builder.Services.AddScoped<ICampaignAccessResolver, CampaignAccessResolver>();

builder.Services.AddSingleton<ComponentRegistry>();
builder.Services.AddSingleton<ISystemCatalog>(sp => sp.GetRequiredService<PluginLoader>());
builder.Services.AddSingleton<ScriptEvaluator>();
builder.Services.AddSingleton<IContentRegistry, ContentRegistry>();
builder.Services.AddSingleton<IContentPackLoader, YamlContentPackLoader>();
builder.Services.AddSingleton<PluginLoader>();
builder.Services.AddSingleton<Codex.Persistence.Runtime.CampaignRuntimeManager>();

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

// Startup diagnostics: a wrong content root (e.g. running the built DLL from the repo root
// instead of `dotnet run --project src/Codex.Web`) leaves WebRootPath missing, every static
// file then falls through to the auth fallback and the browser gets login HTML instead of
// blazor.web.js - a permanently stuck loading screen. Say so loudly instead of failing silent.
logger.LogInformation("Content root: {ContentRoot} | Web root: {WebRoot} | Data dir: {DataDir}",
    app.Environment.ContentRootPath, app.Environment.WebRootPath ?? "(not set)", dataDir);
if (string.IsNullOrEmpty(app.Environment.WebRootPath) || !Directory.Exists(app.Environment.WebRootPath))
{
    logger.LogWarning("Web root '{WebRoot}' does not exist - static files (CSS/JS) will be " +
        "unavailable and the UI will never boot. Launch with 'dotnet run --project src/Codex.Web' " +
        "so the content root points at the web project.", app.Environment.WebRootPath ?? "(not set)");
}

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
    await loader.LoadAndInitializeAsync(absolutePluginsDir);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// i18n (6.5): culture from cookie, English default. Switching cultures reloads the page
// (see CulturePicker), which starts a fresh circuit under the new culture.
var supportedCultures = new[] { "en", "he" };
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("en")
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures));

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Test-only seeding endpoints for the Phase 3 exit-criteria Playwright suite (and any future
// e2e test): opt-in via config, off by default, so a stray "Codex:EnableTestSeedEndpoint=true"
// can never leak into a real deployment's route table - the check happens once here, at startup,
// rather than per-request inside the handler, so the routes don't even exist otherwise.
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

    app.MapPost("/test/seed/actors", async (List<Codex.Web.TestSeedActor> actors, IActorRepository actorRepository, ComponentRegistry componentRegistry) =>
    {
        foreach (var seed in actors)
        {
            var pool = new Codex.Core.Components.ResourcePoolComponent();
            pool.Set("HP", seed.Hp);
            pool.Set("HP_Max", seed.HpMax);

            var actor = new ActorDocument
            {
                Id = seed.Id,
                CampaignId = seed.CampaignId,
                Kind = seed.Kind,
                OwnerUserId = seed.OwnerUserId,
                Name = seed.Name,
                Visibility = seed.Visibility,
                State = componentRegistry.Snapshot(new object[] { pool })
            };
            await actorRepository.SaveAsync(actor);
        }

        return Results.Ok();
    }).AllowAnonymous();

    // Phase 3 exit criterion 6 ("the session log contains every command") reads this back
    // rather than parsing it out of rendered HTML - the session log isn't shown in any UI yet
    // (that's 4.1's recap editor), so the only faithful way to assert on it today is directly.
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

// Local username/password sign-in and registration live here as plain HTTP posts rather than
// interactive Blazor forms: cookie sign-in needs a real HttpContext, which an interactive
// circuit doesn't have (Login.razor/Register.razor render plain <form> elements posting here).
// Failures redirect back with ?error= (PRG), which both pages already render.
static string WithAuthError(string url, string message) =>
    url + (url.Contains('?') ? "&" : "?") + "error=" + Uri.EscapeDataString(message);

// Mints an antiforgery request token for the auth forms when they were reached via in-circuit
// navigation (no SSR ran, so no token was minted or persisted). Safe as an anonymous GET: the
// caller only ever receives their own token, and the cookie is set on this same response.
app.MapGet("/auth/token", (HttpContext ctx, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
    Results.Ok(new
    {
        token = antiforgery.GetAndStoreTokens(ctx).RequestToken,
    })).AllowAnonymous();

app.MapPost("/auth/login", async (HttpContext ctx, IUserRepository userRepository,
    Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
    }
    catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
    {
        return Results.BadRequest("Invalid antiforgery token.");
    }

    var form = ctx.Request.Form;
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();
    var back = "/login" + (Codex.Web.UrlHelper.IsLocalUrl(returnUrl)
        ? "?returnUrl=" + Uri.EscapeDataString(returnUrl) : "");

    var user = await userRepository.GetUserByUsernameAsync(username);
    var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<UserDocument>();
    var result = user == null
        ? Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed
        : hasher.VerifyHashedPassword(user, user.PasswordHash, password);

    if (user == null || result == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
    {
        return Results.Redirect(WithAuthError(back, "Invalid username or password."));
    }

    if (result == Microsoft.AspNetCore.Identity.PasswordVerificationResult.SuccessRehashNeeded)
    {
        user.PasswordHash = hasher.HashPassword(user, password);
        await userRepository.UpdateUserAsync(user);
    }

    var claims = new List<System.Security.Claims.Claim>
    {
        new(System.Security.Claims.ClaimTypes.Name, user.Username),
        new(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id)
    };
    foreach (var role in user.Roles)
    {
        claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role));
    }

    var identity = new System.Security.Claims.ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new System.Security.Claims.ClaimsPrincipal(identity));

    return Results.Redirect(Codex.Web.UrlHelper.IsLocalUrl(returnUrl) ? returnUrl! : "/");
}).AllowAnonymous();

app.MapPost("/auth/register", async (HttpContext ctx, IUserRepository userRepository,
    IConfiguration configuration, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
    }
    catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
    {
        return Results.BadRequest("Invalid antiforgery token.");
    }

    var form = ctx.Request.Form;
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var confirmPassword = form["confirmPassword"].ToString();
    var returnUrl = form["returnUrl"].ToString();
    var back = "/register" + (Codex.Web.UrlHelper.IsLocalUrl(returnUrl)
        ? "?returnUrl=" + Uri.EscapeDataString(returnUrl) : "");
    IResult Fail(string message) => Results.Redirect(WithAuthError(back, message));

    if (password != confirmPassword)
    {
        return Fail("Passwords do not match.");
    }

    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
    {
        return Fail("Username and Password are required.");
    }

    if (password.Length < 8)
    {
        return Fail("Password must be at least 8 characters long.");
    }

    if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
    {
        return Fail("Password must contain at least one uppercase letter, one lowercase letter, and one number.");
    }

    if (await userRepository.GetUserByUsernameAsync(username) != null)
    {
        return Fail("Username is already taken.");
    }

    var user = new UserDocument
    {
        Id = Guid.NewGuid().ToString(),
        Username = username,
        Roles = new List<string> { "Player" }
    };

    // B1 remediation: no username self-grants a role. ServerAdmin goes to a username
    // configured via Codex:AdminUsername, or, if that isn't configured, to whoever
    // registers the very first account.
    var configuredAdmin = configuration["Codex:AdminUsername"];
    var grantsServerAdmin = !string.IsNullOrWhiteSpace(configuredAdmin)
        ? string.Equals(username, configuredAdmin, StringComparison.OrdinalIgnoreCase)
        : !await userRepository.AnyUsersExistAsync();
    if (grantsServerAdmin)
    {
        user.Roles.Add("ServerAdmin");
    }

    user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<UserDocument>().HashPassword(user, password);

    // B12 remediation: reserve the username atomically via compare-exchange before creating
    // the user document, closing the query-then-insert race between concurrent registrations.
    if (!await userRepository.TryReserveUsernameAsync(user.Username, user.Id))
    {
        return Fail("Username is already taken.");
    }

    await userRepository.CreateUserAsync(user);

    var claims = new List<System.Security.Claims.Claim>
    {
        new(System.Security.Claims.ClaimTypes.Name, user.Username),
        new(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id)
    };
    foreach (var role in user.Roles)
    {
        claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role));
    }

    var identity = new System.Security.Claims.ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new System.Security.Claims.ClaimsPrincipal(identity));

    return Results.Redirect(Codex.Web.UrlHelper.IsLocalUrl(returnUrl) ? returnUrl! : "/");
}).AllowAnonymous();

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

// Serves the fingerprinted `@Assets[]` URLs from App.razor. Explicitly anonymous: the
// global auth fallback would otherwise 302 these to /login and the pages would load unstyled.
// UseStaticFiles above keeps serving plain wwwroot paths.
app.MapStaticAssets().AllowAnonymous();

app.MapRazorComponents<Codex.Web.Components.App>().AddInteractiveServerRenderMode();

// Machine-readable startup state: Kestrel listens before RavenDB finishes warming (see
// below), so readiness gates on the store actually being initialized. Anonymous by design -
// load balancers and the e2e suite poll this with no credentials.
app.MapGet("/culture/{culture}", (string culture, HttpContext context) =>
{
    if (!supportedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase))
    {
        return Results.BadRequest("Unsupported culture.");
    }

    context.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });

    var returnUrl = context.Request.Query["returnUrl"].ToString();
    if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//"))
    {
        returnUrl = "/";
    }

    return Results.Redirect(returnUrl);
}).AllowAnonymous();

app.MapGet("/health/ready", (RavenDbService raven) =>
    raven.IsStoreInitialized
        ? Results.Ok(new { status = "ready", ravenReady = true })
        : Results.Json(new { status = "warming", ravenReady = false }, statusCode: 503)
).AllowAnonymous();

// 4.4: daily RavenDB backup into the data dir. Best-effort by design - a backup that can't be
// configured must never keep the table from starting. Runs after Kestrel is already listening:
// touching the store boots the whole embedded RavenDB server (seconds on a warm machine, much
// longer on first extraction), which used to delay "Now listening on" for the entire boot.
// The same background pass warms the store so the first real request doesn't pay for it;
// Lazy<T> is thread-safe, so a request racing the warmup simply blocks until it finishes.
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        try
        {
            using var warmupScope = app.Services.CreateScope();
            var raven = warmupScope.ServiceProvider.GetRequiredService<RavenDbService>();
            _ = raven.Store;
            logger.LogInformation("RavenDB ready at {DataDir}.", dataDir);

            var backupDir = Path.GetFullPath(Path.Combine(dataDir, "Backups"));
            await raven.EnsureScheduledBackupAsync(backupDir);
        }
        catch (Raven.Client.Exceptions.Commercial.LicenseLimitException)
        {
            // Unlicensed (embedded default) servers reject periodic-backup config while serving
            // data perfectly well - a warning, not an error (and no stack trace for it).
            logger.LogWarning("RavenDB has no license for periodic backups; continuing without a daily schedule.");
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Scheduled backup configuration failed; continuing without it.");
        }
    });
});

app.Run();
