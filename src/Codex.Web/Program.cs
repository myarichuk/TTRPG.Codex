using Codex.Core;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
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

var authBuilder = builder.Services.AddAuthentication();

if (!string.IsNullOrEmpty(builder.Configuration["Authentication:Google:ClientId"]))
{
    authBuilder.AddGoogle(options =>
    {
        options.ClientId = builder.Configuration["Authentication:Google:ClientId"]!;
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"]!;
        options.SignInScheme = "External";
    });
}

if (!string.IsNullOrEmpty(builder.Configuration["Authentication:Apple:ClientId"]))
{
    authBuilder.AddApple(options =>
    {
        options.ClientId = builder.Configuration["Authentication:Apple:ClientId"]!;
        options.KeyId = builder.Configuration["Authentication:Apple:KeyId"]!;
        options.TeamId = builder.Configuration["Authentication:Apple:TeamId"]!;
        // The private key should be passed according to the provider docs
        var privateKeyContent = builder.Configuration["Authentication:Apple:PrivateKey"];
        if (!string.IsNullOrEmpty(privateKeyContent))
        {
            options.UsePrivateKey(
                (keyId) => new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Directory.GetCurrentDirectory()).GetFileInfo(privateKeyContent)
            );
        }
        options.SignInScheme = "External";
    });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("DM", policy => policy.RequireRole("DM"));
});
builder.Services.AddHttpContextAccessor();

// Configure Codex
var dataDir = builder.Configuration["Codex:DataDirectory"] ?? "RavenData";
var pluginsDir = builder.Configuration["Codex:PluginsDirectory"] ?? "plugins";

builder.Services.AddSingleton(sp => new RavenDbService(dataDir));
builder.Services.AddSingleton<ICampaignRepository, RavenCampaignRepository>();
builder.Services.AddSingleton<ICharacterRepository, RavenCharacterRepository>();
builder.Services.AddSingleton<IUserRepository, RavenUserRepository>();
builder.Services.AddSingleton<ISessionRepository, RavenSessionRepository>();

builder.Services.AddSingleton<ComponentRegistry>();
builder.Services.AddSingleton<PluginLoader>();
builder.Services.AddSingleton<CodexWorld>();

// Add SignalR explicitly (already added by AddServerSideBlazor, but doing it for clarity)
builder.Services.AddSignalR();
builder.Services.AddHttpClient();

var app = builder.Build();

// Initialize everything on startup
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Server running at: http://localhost:5000");

var dbService = app.Services.GetRequiredService<RavenDbService>();
// Store is initialized in constructor
logger.LogInformation("RavenDB initialized at {DataDir}", dataDir);

var loader = app.Services.GetRequiredService<PluginLoader>();
var registry = app.Services.GetRequiredService<ComponentRegistry>();
var world = app.Services.GetRequiredService<CodexWorld>();

var absolutePluginsDir = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, pluginsDir));
logger.LogInformation("Loading plugins from: {PluginsDir}", absolutePluginsDir);

_ = loader.LoadAndInitializeAsync(absolutePluginsDir, world);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

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
    var provider = authenticateResult.Properties?.Items["LoginProvider"] ?? principal.Identities.FirstOrDefault()?.AuthenticationType;
    var providerKey = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
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
            Username = name ?? "User" + Guid.NewGuid().ToString().Substring(0, 8),
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

    if (string.IsNullOrEmpty(returnUrl) || !Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
    {
        returnUrl = "/";
    }

    return Results.Redirect(returnUrl);
});

app.MapRazorComponents<Codex.Web.Components.App>().AddInteractiveServerRenderMode();

app.Run();
