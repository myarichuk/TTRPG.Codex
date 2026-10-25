using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// The app on a phone-sized screen: no page scrolls sideways, and the header folds language, theme and
/// sign-out into one menu instead of pushing the page wider than the screen.
/// </summary>
[Collection("RavenDb")]
public class PhoneLayoutE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public PhoneLayoutE2ETests(AppFixture app) => _app = app;

    private static Task<int> OverflowAsync(IPage page) =>
        page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");

    [Fact]
    public async Task MainPages_OnAPhone_DoNotScrollSideways_AndTheHeaderMenuHoldsTheRest()
    {
        const string password = "Correct-Horse-1";
        var userId = "phone-dm-" + Guid.NewGuid();
        var username = "dm_" + userId;
        await _app.SeedUserAsync(userId, username, password, "Player");

        await using var context = await _app.Browser.NewContextAsync(new()
        {
            BaseURL = _app.BaseUrl,
            ViewportSize = new ViewportSize { Width = 375, Height = 812 },
        });
        var page = await context.NewPageAsync();
        await E2EHelpers.LoginAsync(page, username, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(page, "Phone Test", "srd5e");

        foreach (var section in new[] { "", "table", "sessions", "journal", "cast", "world", "wiki", "library", "settings" })
        {
            await E2EHelpers.GoToSectionAsync(page, campaignId, section);
            Assert.True(await OverflowAsync(page) <= 0, $"/campaigns/…/{section} scrolls sideways on a phone");
        }

        foreach (var url in new[] { "/", "/library", "/about" })
        {
            await E2EHelpers.GotoInteractiveAsync(page, url);
            Assert.True(await OverflowAsync(page) <= 0, $"{url} scrolls sideways on a phone");
        }

        // The wide-screen controls are hidden; the menu shows the languages and sign-out.
        var signOut = page.GetByRole(AriaRole.Button, new() { Name = "Sign out" });
        await Assertions.Expect(signOut).ToBeHiddenAsync();
        await page.GetByLabel("Menu").ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Русский" })).ToBeVisibleAsync();
        await Assertions.Expect(signOut).ToBeVisibleAsync();

        // A tap elsewhere (well below the open panel) closes it again.
        await page.Mouse.ClickAsync(20, 700);
        await Assertions.Expect(signOut).ToBeHiddenAsync();
    }
}
