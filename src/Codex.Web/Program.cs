using Codex.Core;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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
    });
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

app.MapRazorComponents<Codex.Web.Components.App>().AddInteractiveServerRenderMode();

app.Run();
