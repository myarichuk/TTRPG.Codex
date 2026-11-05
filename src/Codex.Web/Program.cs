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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// A page whose connection drops keeps its place on the server for half an hour, not the default three minutes: a phone
// locked mid-entry comes back to the same page with what was typed, instead of a reload (D9).
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30));
builder.Services.AddSingleton<Codex.Localization.Localizer>();
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
// The pages that tell a visitor whether a password or an invite code is right get a ration per address. Always, not only
// when open to the internet (R13): on a LAN server the sign-in form is plain HTTP, and anyone on the network can reach it.
// This computer itself is not rationed; open to the internet, Funnel's visitors carry their own address (forwarded headers).
builder.Services.AddSingleton<LoginAttempts>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(context =>
        Codex.Web.ExposurePolicy.IsGuessable(context.Request.Path) && context.Connection.RemoteIpAddress is { } address && !System.Net.IPAddress.IsLoopback(address)
            ? System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                Codex.Web.ExposurePolicy.ClientKey(address),
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = Codex.Web.ExposurePolicy.GuessesPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                })
            : System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("open"));
});

// Persistence
var dataDir = builder.Configuration["Codex:DataDirectory"] ?? "RavenData";

// The data folder is checked before anything writes to it (the Data Protection key folder below is made while services
// are registered): a read-only folder then stops the server with its plain message for the launcher, not a stack trace.
if (RavenDbService.CheckWritable(dataDir) is { } unwritable)
{
    // The launcher shows this line to the user (ServerController.StartupProblemPrefix).
    Console.Error.WriteLine("CODEX_STARTUP_ERROR: " + unwritable + " Shutting down.");
    Environment.Exit(1);
}

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
builder.Services.AddSingleton<ITemplateRepository, RavenTemplateRepository>();
builder.Services.AddSingleton<IJournalRepository, RavenJournalRepository>();
builder.Services.AddSingleton<IWorldChatRepository, RavenWorldChatRepository>();
builder.Services.AddSingleton<IMediaStore, RavenMediaStore>();
builder.Services.AddSingleton<CampaignExportService>();
builder.Services.AddSingleton<BookSourceReader>();
// Pictures are decoded in a separate process (Codex.ImageWorker, built beside the server): a file that crashes the native decoder ends that
// process, not this one. Without the worker file the book keeps pictures as they are.
builder.Services.AddSingleton<IBookImageDecoder>(_ => new BookImageWorkerPool(BookImageWorkerLaunch.Beside(AppContext.BaseDirectory)));
builder.Services.AddSingleton(sp => new BookImageScaler(sp.GetRequiredService<IBookImageDecoder>()));
builder.Services.AddSingleton<BookArtReader>();
builder.Services.AddSingleton<IBookBuildRepository, RavenBookBuildRepository>();
builder.Services.Configure<Codex.Web.Book.BookPdfOptions>(builder.Configuration.GetSection(Codex.Web.Book.BookPdfOptions.Section));
builder.Services.AddSingleton<Codex.Web.Book.BookPdf>();
builder.Services.AddSingleton<Codex.Web.Book.IBookModels, Codex.Web.Book.AiBookModels>();
// A singleton that must read a campaign's roles afresh for each request: it asks the (scoped) resolver through a scope of its own.
builder.Services.AddSingleton(sp => new Codex.Web.Book.BookBuilds(
    sp.GetRequiredService<BookSourceReader>(), sp.GetRequiredService<IBookBuildRepository>(), sp.GetRequiredService<Codex.Web.Book.IBookModels>(),
    async (campaignId, userId) =>
    {
        using var scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ICampaignAccessResolver>().ResolveAsync(campaignId, userId);
    }));
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
builder.Services.AddScoped<MentionSources>();
builder.Services.AddScoped<WorldChatSessions>();
builder.Services.AddSingleton<Codex.Persistence.Runtime.CampaignRuntimeManager>();
builder.Services.AddHostedService<IdleRuntimeEvictionService>();
builder.Services.AddSingleton<JsonBackups>();
builder.Services.AddSingleton(new BackupStatus
{
    Folder = Path.GetFullPath(Path.Combine(dataDir, "Backups")),
    Enabled = builder.Configuration.GetValue("Codex:Backups:Enabled", true),
    Keep = Math.Max(1, builder.Configuration.GetValue("Codex:Backups:Keep", 7))
});
builder.Services.AddHostedService<BackupService>();

// AI: the "AI" configuration section is the server-wide fallback provider; users set up their
// own OpenAI-compatible providers (OpenRouter by default) under /settings/ai, keys protected by
// Data Protection. Nothing is sent to a model unless someone clicks an AI button.
var aiConfig = new AIConfiguration();
builder.Configuration.GetSection("AI").Bind(aiConfig);
builder.Services.AddSingleton(aiConfig);
var aiImageConfig = new AIImageConfiguration();
builder.Configuration.GetSection("AIImage").Bind(aiImageConfig);
builder.Services.AddSingleton(aiImageConfig);
// Keys under the data directory, so the folder carries what its cookies and stored AI keys need (D12).
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(Codex.Web.DataProtectionKeys.Directory(dataDir))
    .SetApplicationName(Codex.Web.DataProtectionKeys.ApplicationName);
builder.Services.AddSingleton<ServerAiUsage>();
builder.Services.AddSingleton<AiProviderService>();
builder.Services.AddSingleton<AiFeatures>();
builder.Services.AddSingleton<WorldBuilderService>();
builder.Services.AddScoped<HomebrewImportService>();
builder.Services.AddSingleton<ArtService>();
builder.Services.AddSingleton<PlaceWikiSync>();
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

if (Codex.Web.ExposurePolicy.IsExposed(app.Configuration))
{
    // Open to the internet through Tailscale Funnel: its proxy runs on this machine and passes the visitor's
    // address and scheme in X-Forwarded-*. The default trusts only loopback, so nobody else can set them.
    app.UseForwardedHeaders(new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
    });
}

app.UseRateLimiter();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// The launcher serves the DM on plain loopback HTTP beside the players' HTTPS address, so it turns the redirect off.
if (app.Configuration.GetValue("Codex:HttpsRedirect", true)) app.UseHttpsRedirection();

// Language: the culture cookie (set by /set-culture) wins; then the install-wide default the launcher
// passes as Codex:DefaultCulture; only without one does the browser's Accept-Language decide.
var configuredCulture = app.Configuration["Codex:DefaultCulture"];
var requestCultures = new RequestLocalizationOptions()
    .SetDefaultCulture(Codex.Localization.SupportedCultures.Normalize(configuredCulture))
    .AddSupportedCultures(Codex.Localization.SupportedCultures.Tags.ToArray())
    .AddSupportedUICultures(Codex.Localization.SupportedCultures.Tags.ToArray());
requestCultures.RequestCultureProviders.Clear();
requestCultures.RequestCultureProviders.Add(new CookieRequestCultureProvider());
if (string.IsNullOrWhiteSpace(configuredCulture))
    requestCultures.RequestCultureProviders.Add(new AcceptLanguageHeaderRequestCultureProvider());
app.UseRequestLocalization(requestCultures);

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Test-only seeding endpoints for the Playwright suites: opt-in via config, off by default, so a
// stray "Codex:EnableTestSeedEndpoint=true" can never leak into a real deployment's route table.
if (app.Configuration.GetValue<bool>("Codex:EnableTestSeedEndpoint"))
{
    Codex.Web.TestSeedEndpoints.MapTestSeeding(app);
}

Codex.Web.AccountEndpoints.MapAccounts(app);
Codex.Web.MediaEndpoints.MapMedia(app);
Codex.Web.PreviewEndpoints.MapPreviews(app);
Codex.Web.BookEndpoints.MapBooks(app);

// wwwroot under fingerprinted URLs (app.<hash>.css, through @Assets[...]) that the browser keeps for good: a new build is a new URL,
// so an update is never drawn with the previous styles or scripts (F28). Open to anyone: the sign-in page needs its styles.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<Codex.Web.Components.App>().AddInteractiveServerRenderMode();

// The database is the app: without it nothing works, and a half-alive server that answers every request
// with a stack trace is worse than a clear failure. Check the folders, open the store now, and stop with
// a message in the log (the launcher reports the stop) rather than failing on the first sign-in.
{
    var problem = RavenDbService.CheckWritable(dataDir);
    if (problem == null)
    {
        try
        {
            _ = app.Services.GetRequiredService<RavenDbService>().Store;
        }
        catch (Exception ex)
        {
            problem = $"The database could not be started: {ex.GetBaseException().Message.Split('\n')[0]}";
            app.Logger.LogError(ex, "RavenDB failed to start.");
        }
    }

    if (problem != null)
    {
        app.Logger.LogCritical("{Problem} Shutting down.", problem);
        // The launcher shows this line to the user (ServerController.StartupProblemPrefix).
        Console.Error.WriteLine("CODEX_STARTUP_ERROR: " + problem);
        Environment.Exit(1);
    }
}

// Daily RavenDB backup into the data dir, beside the server's own JSON backups (BackupService). Best-effort: a backup that
// can't be configured must never keep the table from starting. The free RavenDB licence refuses it; that is expected, and
// the About page says so (D11).
var backupStatus = app.Services.GetRequiredService<BackupStatus>();
try
{
    using var backupScope = app.Services.CreateScope();
    await backupScope.ServiceProvider.GetRequiredService<RavenDbService>().EnsureScheduledBackupAsync(backupStatus.Folder);
    backupStatus.Database = DatabaseBackups.On;
}
catch (Exception ex) when (Codex.Web.Services.BackupFailures.IsLicenceRefusal(ex))
{
    backupStatus.Database = DatabaseBackups.NotLicensed;
    app.Logger.LogInformation("The RavenDB licence does not allow database backups; the daily JSON backup in {Folder} covers them.", backupStatus.Folder);
}
catch (Exception ex)
{
    backupStatus.Database = DatabaseBackups.Failed;
    app.Logger.LogError(ex, "Scheduled backup configuration failed; continuing without it.");
}

app.Run();
