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
            // Treat as PEM content, write to temp file
            var tmpPath = Path.Combine(Path.GetTempPath(), $"apple_key_{Guid.NewGuid()}.p8");
            System.IO.File.WriteAllText(tmpPath, applePrivateKey);
            options.UsePrivateKey(
                (keyId) => new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetDirectoryName(tmpPath)!).GetFileInfo(Path.GetFileName(tmpPath))
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

builder.Services.AddSingleton(sp => new RavenDbService(dataDir));
builder.Services.AddSingleton<ICampaignRepository, CampaignRepository>();
builder.Services.AddSingleton<ICharacterRepository, CharacterRepository>();
builder.Services.AddSingleton<IUserRepository, RavenUserRepository>();
builder.Services.AddSingleton<ISessionRepository, RavenSessionRepository>();

builder.Services.AddSingleton<ComponentRegistry>();
builder.Services.AddSingleton<ScriptEvaluator>();
builder.Services.AddSingleton<IAbilityRegistry, AbilityRegistry>();
builder.Services.AddSingleton<IAbilityPackLoader, YamlAbilityPackLoader>();
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
logger.LogInformation("Server running at: http://localhost:5000");

// Initialize Plugins and World once at startup
using (var scope = app.Services.CreateScope())
{
    var loader = scope.ServiceProvider.GetRequiredService<PluginLoader>();
    var world = scope.ServiceProvider.GetRequiredService<CodexWorld>();
    var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    var pluginsPath = config["Codex:PluginsDirectory"] ?? "plugins";
    var absolutePluginsDir = Path.GetFullPath(Path.Combine(env.ContentRootPath, pluginsPath));

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
app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.MapGet("/login/external", (string provider, string? returnUrl) =>
{
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

    if (user == null && !string.IsNullOrEmpty(email))
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
        user = new UserDocument
        {
            Id = Guid.NewGuid().ToString(),
            Username = !string.IsNullOrEmpty(name) ? name : ("User" + Guid.NewGuid().ToString().Substring(0, 8)),
            Email = email ?? "",
            Roles = new List<string> { "Player" },
            ExternalLogins = new List<ExternalLogin> { new ExternalLogin { Provider = provider, ProviderKey = providerKey } }
        };

        var existingUser = await userRepository.GetUserByUsernameAsync(user.Username);
        if (existingUser != null)
        {
            user.Username = user.Username + "_" + Guid.NewGuid().ToString().Substring(0, 4);
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
