using Codex.Localization;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// The app in Russian and Hebrew: the language switch, the right-to-left layout, that the choice follows a user to
/// their next sign-in, and that no main page overflows sideways or leaves a control without an accessible name.
/// </summary>
[Collection("RavenDb")]
public class LocalizationE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public LocalizationE2ETests(AppFixture app) => _app = app;

    private static Task<string> AttributeAsync(IPage page, string name) =>
        page.EvaluateAsync<string>($"() => document.documentElement.getAttribute('{name}') ?? ''");

    private static async Task<bool> OverflowsAsync(IPage page) =>
        await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1");

    [Fact]
    public async Task SignInPage_FollowsTheLanguage_AndDirection()
    {
        foreach (var (tag, dir) in new[] { ("ru", "ltr"), ("he", "rtl"), ("en", "ltr") })
        {
            await using var context = await _app.Browser.NewContextAsync(new() { BaseURL = _app.BaseUrl });
            var page = await context.NewPageAsync();
            await page.GotoAsync($"/set-culture?culture={tag}&returnUrl=/login");

            Assert.Equal(tag, await AttributeAsync(page, "lang"));
            Assert.Equal(dir, await AttributeAsync(page, "dir"));
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = Localizer.For(tag)["Page.Login.Heading"] })).ToBeVisibleAsync();
            Assert.False(await OverflowsAsync(page), $"/login overflows sideways in {tag}");
            Assert.Empty(await E2EHelpers.UnlabelledControlsAsync(page));
        }
    }

    [Fact]
    public async Task MainPages_InRussianAndHebrew_HaveNoOverflowOrUnlabelledControls_AndTheChoiceIsRemembered()
    {
        const string password = "Correct-Horse-1";
        var userId = "i18n-dm-" + Guid.NewGuid();
        var username = "dm_" + userId;
        await _app.SeedUserAsync(userId, username, password, "Player");

        await using var first = await _app.Browser.NewContextAsync(new() { BaseURL = _app.BaseUrl });
        var page = await first.NewPageAsync();
        await E2EHelpers.LoginAsync(page, username, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(page, "Lokalisierung Test", "srd5e");

        foreach (var tag in new[] { "ru", "he" })
        {
            var text = Localizer.For(tag);
            await page.GotoAsync($"/set-culture?culture={tag}&returnUrl=/campaigns/{campaignId}");
            await E2EHelpers.WaitForInteractiveAsync(page);

            Assert.Equal(tag, await AttributeAsync(page, "lang"));
            Assert.Equal(tag == "he" ? "rtl" : "ltr", await AttributeAsync(page, "dir"));
            var nav = page.GetByRole(AriaRole.Navigation, new() { Name = text["Layout.Nav.Main"] });
            await Assertions.Expect(nav).ToBeVisibleAsync();
            await Assertions.Expect(nav.GetByRole(AriaRole.Link, new() { Name = text["Layout.Nav.Library"] })).ToBeVisibleAsync();

            foreach (var section in new[] { "", "library", "settings" })
            {
                await E2EHelpers.GoToSectionAsync(page, campaignId, section);
                Assert.False(await OverflowsAsync(page), $"/campaigns/…/{section} overflows sideways in {tag}");
                Assert.Empty(await E2EHelpers.UnlabelledControlsAsync(page));
            }
        }

        // Hebrew was the last choice: it is remembered on the user, so a brand-new browser lands in Hebrew after signing in.
        await using var second = await _app.Browser.NewContextAsync(new() { BaseURL = _app.BaseUrl });
        var fresh = await second.NewPageAsync();
        await E2EHelpers.LoginAsync(fresh, username, password);
        Assert.Equal("he", await AttributeAsync(fresh, "lang"));
        Assert.Equal("rtl", await AttributeAsync(fresh, "dir"));
    }

    [Fact]
    public async Task RegisteringInHebrew_IsRememberedOnTheNextSignIn()
    {
        var username = "ivrit_" + Guid.NewGuid().ToString("N")[..8];
        const string password = "Correct-Horse-1";

        await using var first = await _app.Browser.NewContextAsync(new() { BaseURL = _app.BaseUrl });
        var page = await first.NewPageAsync();
        await page.GotoAsync("/set-culture?culture=he&returnUrl=/register");
        Assert.Equal("rtl", await AttributeAsync(page, "dir"));
        await page.Locator("#username").FillAsync(username);
        await page.Locator("#password").FillAsync(password);
        await page.Locator("#confirmPassword").FillAsync(password);
        await page.Locator("form[method=post] button[type=submit], form[method=post] input[type=submit]").First.ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("/register"));

        // A different browser, sign-in page still in English: the language saved at sign-up wins once signed in.
        await using var second = await _app.Browser.NewContextAsync(new() { BaseURL = _app.BaseUrl });
        var fresh = await second.NewPageAsync();
        await E2EHelpers.LoginAsync(fresh, username, password);

        Assert.Equal("he", await AttributeAsync(fresh, "lang"));
        Assert.Equal("rtl", await AttributeAsync(fresh, "dir"));
    }
}
