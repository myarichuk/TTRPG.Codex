using System.Net;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// Guards the very first thing a fresh user sees: anonymous static assets, the boot splash
/// going away once Blazor starts, and first-user registration reaching the dashboard.
///
/// Regression origin: launching the built DLL from the wrong working directory left WebRootPath
/// missing, so every static file fell through to the auth fallback and the browser received
/// login HTML instead of <c>blazor.web.js</c> - a permanently stuck loading screen. The
/// <see cref="StaticAssets_AreServedAnonymously"/> test pins the asset side of that; the
/// registration scenario pins the "fresh user can actually get in" side.
///
/// Shares the "RavenDb" collection with every other embedded-RavenDB test (see
/// CampaignRuntimeTests.cs/PersistenceTest.cs) - this scenario's RavenDB instance lives inside
/// a child process, and the collection keeps the machine from running several embedded
/// RavenDB servers at once.
/// </summary>
[Collection("RavenDb")]
public class InitialLoadingTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public InitialLoadingTests(AppFixture app) => _app = app;

    /// <summary>Every asset the boot page needs must serve its real bytes to an anonymous
    /// client - a 302 to /login here is the stuck-loading-screen bug (the browser would execute
    /// login HTML as blazor.web.js and Blazor would never start).</summary>
    [Fact]
    public async Task StaticAssets_AreServedAnonymously()
    {
        // No auto-redirect: HttpClient would otherwise follow a 302 to /login and report the
        // login page's 200, hiding exactly the regression this asserts against.
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = _app.Http.BaseAddress
        };

        var assets = new Dictionary<string, string>
        {
            ["/app.css"] = "text/css",
            ["/Codex.Web.styles.css"] = "text/css",
            ["/_framework/blazor.web.js"] = "text/javascript",
            ["/js/app-startup.js"] = "text/javascript",
            ["/favicon.png"] = "image/png",
        };

        foreach (var (path, expectedContentType) in assets)
        {
            using var response = await noRedirect.GetAsync(path);
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"{path} returned {(int)response.StatusCode} {response.StatusCode} (expected 200 - a 302 to /login is the stuck-loading-screen bug).");
            Assert.Equal(expectedContentType, response.Content.Headers.ContentType?.MediaType);
        }

        // Second half: fetch exactly what the rendered page references - `@Assets[]` emits
        // fingerprinted URLs (app.{hash}.css) that only exist in the static-assets manifest, so
        // asserting the plain paths alone would miss a MapStaticAssets/FallbackPolicy regression
        // like the one that once served login HTML for both stylesheets in every environment.
        var html = await _app.Http.GetStringAsync("/login");
        var referenced = System.Text.RegularExpressions.Regex.Matches(html, "(?:href|src)=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Where(url => !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && (url.EndsWith(".css") || url.EndsWith(".js") || url.EndsWith(".png")))
            .Distinct()
            .ToList();
        Assert.NotEmpty(referenced);

        foreach (var url in referenced)
        {
            var path = "/" + url.TrimStart('/');
            using var response = await noRedirect.GetAsync(path);
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"Referenced asset {path} returned {(int)response.StatusCode} {response.StatusCode} (expected 200).");
            Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        }
    }

    /// <summary>The Blazor framework endpoints carry no authorize metadata of their own, so
    /// the auth fallback would 302 them to /login for anonymous visitors - and blazor.web.js
    /// JSON-parses the login HTML, throwing an uncaught SyntaxError on every anonymous page
    /// load. This pins the carve-out (see AuthenticatedOrBlazorFramework).</summary>
    [Fact]
    public async Task BlazorInitializers_AreServedAnonymously()
    {
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = _app.Http.BaseAddress
        };

        using var response = await noRedirect.GetAsync("/_blazor/initializers");
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"/_blazor/initializers returned {(int)response.StatusCode} {response.StatusCode} (expected 200 - a 302 to /login throws in blazor.web.js).");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>The fixture already waits for readiness before any test runs; by the time we
    /// get here RavenDB must be up (the app warms it on a background task after Kestrel starts
    /// listening).</summary>
    [Fact]
    public async Task HealthReady_ReportsReady()
    {
        using var response = await _app.Http.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The culture endpoint (6.5) sets the localization cookie and bounces back
    /// to the return URL - no login required, so pre-auth pages can offer it too.</summary>
    [Fact]
    public async Task CultureEndpoint_SetsCultureCookie()
    {
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = _app.Http.BaseAddress
        };

        using var response = await noRedirect.GetAsync("/culture/he?returnUrl=/login");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.ToString());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            header => header.Contains(".AspNetCore.Culture") && header.Contains("he"));
    }

    [Fact]
    public async Task CultureEndpoint_RejectsUnsupportedCulture()
    {
        using var response = await _app.Http.GetAsync("/culture/xx");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Fresh user, empty database: / bounces to /login, the boot splash disappears once
    /// Blazor starts, and registering the first account lands on the dashboard. This is the
    /// whole onboarding path - there is no separate onboarding flow; the first registered user
    /// becomes ServerAdmin (see Register.razor).</summary>
    [Fact]
    public async Task FreshUser_CanRegister_AndReachDashboard()
    {
        var browser = await _app.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var page = await context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);

        await page.GotoAsync("/");

        // Unauthenticated root bounces to the login page...
        await page.WaitForURLAsync(url => url.Contains("/login"));

        // ...whose boot splash must go away once Blazor starts (i.e. blazor.web.js and
        // app-startup.js actually loaded and executed - in the broken state the splash stays
        // forever because app-startup.js was login HTML).
        await page.WaitForSelectorAsync("#boot-splash", new() { State = WaitForSelectorState.Detached });
        await page.GetByRole(AriaRole.Button, new() { Name = "Enter Codex" }).WaitForAsync();

        // Register a brand-new account through the real UI.
        await page.GetByRole(AriaRole.Link, new() { Name = "Initiate Account" }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/register"));

        var username = "e2e_first_" + Guid.NewGuid().ToString("N")[..8];
        await page.Locator("#username").FillAsync(username);
        await page.Locator("#password").FillAsync("Correct-Horse-1");
        await page.Locator("#confirmPassword").FillAsync("Correct-Horse-1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Initiate Account" }).ClickAsync();

        // Successful registration signs in and lands on the dashboard.
        await page.WaitForURLAsync(url => !url.Contains("/register") && !url.Contains("/login"));
        await page.GetByText("Your Worlds").WaitForAsync();

        // The anonymous /_blazor/initializers 302 once threw an uncaught SyntaxError in
        // blazor.web.js on exactly this path - fail loudly if any page starts throwing again.
        Assert.Empty(pageErrors);
    }
}
