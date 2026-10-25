using Codex.Persistence;
using Codex.Web;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// The app on a phone-sized screen: no page scrolls sideways, the header folds language, theme and sign-out
/// into one menu, campaign sections sit in a tab bar at the bottom, and details that sit beside a list on a wide
/// screen (a Library entry, the journal editor, the combat command panel) open as sheets in view.
/// </summary>
[Collection("RavenDb")]
public class PhoneLayoutE2ETests : IClassFixture<AppFixture>
{
    private const int PhoneWidth = 375;
    private const int PhoneHeight = 812;

    private readonly AppFixture _app;

    public PhoneLayoutE2ETests(AppFixture app) => _app = app;

    private static Task<int> OverflowAsync(IPage page) =>
        page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");

    private async Task<(IBrowserContext Context, IPage Page, string CampaignId)> SignInAsDmAsync(string campaignName)
    {
        const string password = "Correct-Horse-1";
        var userId = "phone-dm-" + Guid.NewGuid();
        var username = "dm_" + userId;
        await _app.SeedUserAsync(userId, username, password, "Player");

        var context = await _app.Browser.NewContextAsync(new()
        {
            BaseURL = _app.BaseUrl,
            ViewportSize = new ViewportSize { Width = PhoneWidth, Height = PhoneHeight },
        });
        var page = await context.NewPageAsync();
        await E2EHelpers.LoginAsync(page, username, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(page, campaignName, "srd5e");
        return (context, page, campaignId);
    }

    /// <summary>The element is on screen, and what is drawn at its centre is the element itself (no tab bar or later row over it).</summary>
    private static async Task AssertOnTopInViewAsync(ILocator locator, string what)
    {
        await SettleAsync(locator);
        var box = await locator.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box!.Y >= 0 && box.Y < PhoneHeight, $"{what} starts off screen at y={box.Y}");
        Assert.True(await locator.EvaluateAsync<bool>(
            "el => { const r = el.getBoundingClientRect(); const hit = document.elementFromPoint(r.left + r.width / 2, Math.min(r.top + 20, innerHeight - 1)); return el.contains(hit); }"),
            $"{what} is covered by something else");
    }

    /// <summary>Sheets slide in; measure them where they come to rest.</summary>
    private static Task SettleAsync(ILocator locator) =>
        locator.EvaluateAsync("el => Promise.all(document.getAnimations()" +
            ".filter(a => a.effect && a.effect.getComputedTiming().endTime !== Infinity)" +
            ".map(a => a.finished.catch(() => {})))");

    [Fact]
    public async Task MainPages_OnAPhone_DoNotScrollSideways_AndTheHeaderMenuHoldsTheRest()
    {
        var (context, page, campaignId) = await SignInAsDmAsync("Phone Test");
        await using var _ = context;

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

    [Fact]
    public async Task CampaignSections_OnAPhone_AreATabBar_AndDetailsOpenAsSheets()
    {
        var (context, page, campaignId) = await SignInAsDmAsync("Phone Sheets");
        await using var _ = context;

        // The section rail is a tab bar at the bottom; More lists the rest and closes once one is picked.
        await E2EHelpers.GoToSectionAsync(page, campaignId, "");
        await Assertions.Expect(page.GetByTestId("section-table")).ToBeHiddenAsync();
        await page.GetByTestId("tabbar-sessions").ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/sessions$"));
        await page.GetByTestId("tabbar-more").ClickAsync();
        var libraryInMenu = page.Locator(".tabbar-more .popover-panel a", new() { HasText = "Library" });
        await libraryInMenu.ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/library$"));
        await Assertions.Expect(libraryInMenu).ToBeHiddenAsync();

        // A Library entry opens over the list, not 18,000px below it; Back returns to the same place in the list.
        await page.GetByTestId("library-type-monster").ClickAsync();
        await Assertions.Expect(page.GetByTestId("library-item").Nth(40)).ToBeAttachedAsync();
        await page.WaitForFunctionAsync("() => document.documentElement.scrollHeight > 4000");
        await page.EvaluateAsync("() => window.scrollTo({ top: 2000, behavior: 'instant' })");
        var listScroll = await page.EvaluateAsync<double>("() => window.scrollY");
        var inView = await page.EvaluateAsync<int>(
            "() => [...document.querySelectorAll('[data-testid=library-item]')].findIndex(a => { const r = a.getBoundingClientRect(); return r.top > 120 && r.bottom < innerHeight - 120; })");
        Assert.True(inView >= 0, "no Library row is on screen after scrolling: " + await page.EvaluateAsync<string>(
            "() => JSON.stringify({ y: scrollY, h: document.documentElement.scrollHeight, n: document.querySelectorAll('[data-testid=library-item]').length, r: [...document.querySelectorAll('[data-testid=library-item]')].slice(0, 60).map(a => Math.round(a.getBoundingClientRect().top)).join(',') })"));
        var item = page.GetByTestId("library-item").Nth(inView);
        var itemName = (await item.TextContentAsync())!.Trim();
        await item.ClickAsync();
        var detail = page.GetByTestId("library-detail");
        await Assertions.Expect(detail).ToContainTextAsync(itemName);
        await AssertOnTopInViewAsync(detail, "The Library entry");
        Assert.True(await OverflowAsync(page) <= 0, "a monster's stat block scrolls the page sideways");
        await page.GetByRole(AriaRole.Link, new() { Name = "Close detail" }).First.ClickAsync();
        await Assertions.Expect(detail).ToBeHiddenAsync();
        // Bootstrap scrolls smoothly; give a scroll that should not happen time to show.
        await page.WaitForTimeoutAsync(800);
        var backScroll = await page.EvaluateAsync<double>("() => window.scrollY");
        Assert.True(Math.Abs(backScroll - listScroll) < 50, $"closing the entry moved the list from {listScroll} to {backScroll}");

        // The journal editor opens in view, with Back to cancel it.
        await page.GetByTestId("tabbar-journal").ClickAsync();
        await page.GetByTestId("journal-new").ClickAsync();
        await AssertOnTopInViewAsync(page.GetByTestId("journal-title"), "The journal editor");
        await page.Locator(".sheet-bar button").ClickAsync();
        await Assertions.Expect(page.GetByTestId("journal-title")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task CommandPanel_OnAPhone_IsASheetAboveTheTabBar_WithQuickAmounts()
    {
        var (context, page, campaignId) = await SignInAsDmAsync("Phone Table");
        await using var _ = context;

        var goblinId = "phone-goblin-" + Guid.NewGuid();
        var orcId = "phone-orc-" + Guid.NewGuid();
        await _app.SeedActorsAsync(new[]
        {
            new TestSeedActor { Id = goblinId, CampaignId = campaignId, Name = "Goblin", Kind = ActorKind.Monster, Visibility = ActorVisibility.Known, Hp = 20, HpMax = 20 },
            new TestSeedActor { Id = orcId, CampaignId = campaignId, Name = "Orc", Kind = ActorKind.Monster, Visibility = ActorVisibility.Known, Hp = 15, HpMax = 15 }
        });

        await E2EHelpers.GoToSectionAsync(page, campaignId, "table");
        await page.GetByTestId("start-table").ClickAsync();
        await page.GetByTestId("initiative-header").WaitForAsync();
        await SetInitiativeAsync(page, "Goblin", 15);
        await SetInitiativeAsync(page, "Orc", 10);

        // The first row's panel would be painted over by the second row if it stayed in the row's layer.
        var goblinRow = page.Locator($"[data-testid='participant-row'][data-actor-id='{goblinId}']");
        await goblinRow.GetByTestId("select-participant").ClickAsync();
        var panel = page.GetByTestId("command-panel");
        await Assertions.Expect(panel).ToBeVisibleAsync();
        await SettleAsync(panel);
        var box = await panel.BoundingBoxAsync();
        var bar = await page.Locator(".ws-tabbar").BoundingBoxAsync();
        Assert.True(box!.Y + box.Height <= bar!.Y + 1, "the command sheet runs under the tab bar");
        await AssertOnTopInViewAsync(panel, "The command sheet");

        // +5 twice is 10 damage, without the number keyboard.
        await panel.GetByTestId("quick-amount-5").ClickAsync();
        await panel.GetByTestId("quick-amount-5").ClickAsync();
        await Assertions.Expect(panel.GetByTestId("damage-input")).ToHaveValueAsync("10");
        await panel.GetByTestId("damage-btn").ClickAsync();
        await Assertions.Expect(goblinRow.GetByTestId("hp-badge")).ToHaveTextAsync("10/20 HP");

        // The sheet's own close button deselects.
        await panel.GetByRole(AriaRole.Button, new() { Name = "Deselect Goblin" }).ClickAsync();
        await Assertions.Expect(panel).ToBeHiddenAsync();

        // The notes drawer covers the tab bar rather than losing its bottom edge under it.
        await page.GetByTestId("notes-toggle").ClickAsync();
        await Assertions.Expect(page.GetByTestId("quick-note")).ToBeVisibleAsync();
        await SettleAsync(page.GetByTestId("notes-dock"));
        Assert.True(await page.EvaluateAsync<bool>("() => !!document.elementFromPoint(innerWidth / 2, innerHeight - 10)?.closest('.notes-drawer')"),
            "the tab bar is drawn over the open notes drawer");
    }

    private static async Task SetInitiativeAsync(IPage dm, string actorName, int roll)
    {
        var select = dm.GetByTestId("add-to-initiative-select");
        var setBtn = dm.GetByTestId("set-initiative-btn");
        await select.Locator("option", new() { HasText = actorName })
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 20_000 });

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await select.SelectOptionAsync(new SelectOptionValue { Label = actorName });
            try
            {
                await Assertions.Expect(setBtn).ToBeEnabledAsync(new() { Timeout = 3_000 });
                break;
            }
            catch (PlaywrightException) when (attempt < 5)
            {
                await select.SelectOptionAsync(new SelectOptionValue { Label = "Choose someone…" });
            }
        }

        await dm.GetByTestId("manual-initiative-input").FillAsync(roll.ToString());
        await setBtn.ClickAsync();
        await Assertions.Expect(dm.Locator("[data-testid='participant-row']", new() { HasText = actorName })).ToBeVisibleAsync();
    }
}
