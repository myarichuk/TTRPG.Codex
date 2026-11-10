using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>The Source pane's tabs and snippet bar, the outline tree and the GM's own chapters (docs/BOOK_EXPORT.md 52).</summary>
[Collection("RavenDb")]
public class BookSourceE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public BookSourceE2ETests(AppFixture app) => _app = app;

    private async Task<(IPage Page, string CampaignId, List<string> Errors)> StartAsync(string name, string edition)
    {
        const string password = "Correct-Horse-1";
        var dmId = "bsrc-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await context.NewPageAsync();
        var errors = new List<string>();
        dm.PageError += (_, e) => errors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, name);
        await _app.SeedActorsAsync([new Codex.Web.TestSeedActor { Id = "bsrc-vera-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Vera", Biography = "Keeps the lighthouse.", Visibility = Codex.Persistence.ActorVisibility.Known }]);
        await E2EHelpers.OpenBookAsync(dm, campaignId, edition);
        await Assertions.Expect(dm.FrameLocator("[data-testid=book-draft-preview]").Locator(".page").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        return (dm, campaignId, errors);
    }

    [Fact]
    public async Task TheSourceHasTextStyleAndTheme_TheThemeIsReadOnly_AndCopiesToStyle_AndTheSecretSnippetIsTheGmEditionsOnly()
    {
        var (dm, campaignId, errors) = await StartAsync("Tabs", "party");
        Assert.Equal(0, await dm.GetByTestId("book-snippet-secret").CountAsync());
        await E2EHelpers.OpenBookAsync(dm, campaignId, "dm");
        await Assertions.Expect(dm.GetByTestId("book-snippet-secret")).ToBeVisibleAsync();

        // A snippet writes its block around nothing at the cursor, as an edit that is saved.
        await dm.Locator("[data-testid=book-draft-chapter][data-section=cast]").ClickAsync();
        await dm.Locator(".cm-content").ClickAsync();
        await dm.Keyboard.PressAsync("ControlOrMeta+End");
        await dm.GetByTestId("book-snippet-note").ClickAsync();
        await Assertions.Expect(dm.Locator(".cm-content")).ToContainTextAsync("{{note");
        await dm.GetByTestId("book-snippet-page").ClickAsync();
        await Assertions.Expect(dm.Locator(".cm-content")).ToContainTextAsync("\\page");
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved", new() { Timeout = 15_000 });
        // The page the break starts is numbered in the gutter.
        await Assertions.Expect(dm.Locator(".cm-book-pageno").First).ToContainTextAsync(new Regex(@"p\. \d+"), new() { Timeout = 15_000 });

        // The tabs: Theme shows the theme's sheet and takes no typing; Copy to Style adds it to the GM's own sheet.
        await dm.GetByTestId("book-source-tab-theme").ClickAsync();
        await Assertions.Expect(dm.Locator(".cm-content")).ToContainTextAsync(".page");
        await dm.Locator(".cm-content").ClickAsync();
        await dm.Keyboard.TypeAsync("ZZZNOTTYPED");
        Assert.DoesNotContain("ZZZNOTTYPED", await dm.Locator(".cm-content").InnerTextAsync());
        await dm.GetByTestId("book-theme-copy").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-css")).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(dm.Locator(".cm-content")).ToContainTextAsync("Copied from the theme");
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved", new() { Timeout = 15_000 });
        // The chapter's text was not touched by the theme's tab.
        await dm.GetByTestId("book-source-tab-text").ClickAsync();
        Assert.DoesNotContain("Copied from the theme", await dm.Locator(".cm-content").InnerTextAsync());
        Assert.DoesNotContain("ZZZNOTTYPED", await dm.Locator(".cm-content").InnerTextAsync());
        Assert.Empty(errors);
    }

    [Fact]
    public async Task TheOutlineShowsTheHeadingsOfTheLiveText_AndTakesArrowKeys_AndTheGmAddsRenamesMovesAndDeletesAChapter()
    {
        var (dm, _, errors) = await StartAsync("Outline", "party");
        await dm.Locator("[data-testid=book-draft-chapter][data-section=cast]").ClickAsync();
        await dm.Locator(".cm-content").ClickAsync();
        await dm.Keyboard.PressAsync("ControlOrMeta+End");
        await dm.Keyboard.TypeAsync("\n\n## A typed heading\n\ntext\n\n```\n## inside a fence\n```\n");
        var row = dm.Locator("[data-testid=book-tree-heading]", new() { HasText = "A typed heading" });
        await Assertions.Expect(row).ToBeVisibleAsync(new() { Timeout = 10_000 });
        // A heading in a fence is not one.
        await Assertions.Expect(dm.Locator("[data-testid=book-tree-heading]", new() { HasText = "inside a fence" })).ToHaveCountAsync(0);
        await dm.Locator("[data-testid=book-draft-chapter][data-section=front]").ClickAsync();
        await row.ClickAsync(); // opens Cast again (the open chapters stay open) and puts the cursor on the heading
        await Assertions.Expect(dm.Locator(".cm-activeLine")).ToContainTextAsync("A typed heading");

        // Keys: Down moves to the next row, Home to the first.
        await dm.Locator("[data-testid=book-draft-chapter][data-section=front]").FocusAsync();
        await dm.Keyboard.PressAsync("ArrowDown");
        Assert.True(await dm.EvaluateAsync<bool>("document.activeElement?.getAttribute('role') === 'treeitem' && document.activeElement.dataset.section !== 'front'"));
        await dm.Keyboard.PressAsync("Home");
        Assert.Equal("front", await dm.EvaluateAsync<string>("document.activeElement.dataset.section"));

        // A chapter of the GM's own: added after the one shown, renamed, moved, and deleted after asking.
        await dm.GetByTestId("book-add-chapter").ClickAsync();
        await dm.GetByTestId("book-add-input").FillAsync("Handouts");
        await dm.GetByTestId("book-add-input").PressAsync("Enter");
        var own = dm.Locator("[data-testid=book-draft-chapter]", new() { HasText = "Handouts" });
        await Assertions.Expect(own).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Assertions.Expect(own).ToHaveAttributeAsync("aria-current", "true");
        await dm.GetByTestId("book-own-menu").ClickAsync();
        await dm.GetByTestId("book-own-rename").ClickAsync();
        await dm.GetByTestId("book-own-rename-input").FillAsync("Props");
        await dm.GetByTestId("book-own-rename-input").PressAsync("Enter");
        own = dm.Locator("[data-testid=book-draft-chapter]", new() { HasText = "Props" });
        await Assertions.Expect(own).ToBeVisibleAsync();
        await dm.GetByTestId("book-own-menu").ClickAsync();
        await dm.GetByTestId("book-own-up").ClickAsync();
        await Assertions.Expect(own).ToBeVisibleAsync();
        await dm.GetByTestId("book-own-menu").ClickAsync();
        await dm.GetByTestId("book-own-delete").ClickAsync();
        // Nothing is deleted until it is confirmed.
        await Assertions.Expect(dm.GetByTestId("book-own-delete-ask")).ToBeVisibleAsync();
        await Assertions.Expect(own).ToBeVisibleAsync();
        await dm.GetByTestId("book-own-delete-yes").ClickAsync();
        await Assertions.Expect(own).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        Assert.Empty(errors);
    }

    [Fact]
    public async Task AClickInThePages_MovesTheCursorToTheHeadingAbove_TheCursorMovesThePages_AndFollowCanBeTurnedOff()
    {
        var (dm, _, errors) = await StartAsync("Follow", "party");
        var preview = dm.FrameLocator("[data-testid=book-draft-preview]");
        await dm.Locator("[data-testid=book-draft-chapter][data-section=cast]").ClickAsync();
        await dm.Locator(".cm-content").ClickAsync();
        await dm.Keyboard.PressAsync("ControlOrMeta+End");
        await dm.Keyboard.TypeAsync("\n\n## First marker\n\nalpha\n\n\\page\n\n## Second marker\n\nbeta\n");
        await Assertions.Expect(preview.Locator("h2", new() { HasText = "Second marker" })).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Assertions.Expect(dm.GetByTestId("book-page-status")).ToContainTextAsync("Page");

        // A click on the second heading's page puts the cursor on that heading.
        await dm.Locator(".cm-content").ClickAsync();
        await dm.Keyboard.PressAsync("ControlOrMeta+Home");
        await preview.Locator("h2", new() { HasText = "Second marker" }).ClickAsync();
        await Assertions.Expect(dm.Locator(".cm-activeLine")).ToContainTextAsync("Second marker");

        // With following off, a click leaves the cursor where it was.
        await dm.GetByTestId("book-follow").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-follow")).ToHaveAttributeAsync("aria-pressed", "false");
        await dm.Locator(".cm-content").ClickAsync();
        await dm.Keyboard.PressAsync("ControlOrMeta+Home");
        await preview.Locator("h2", new() { HasText = "Second marker" }).ClickAsync();
        await Assertions.Expect(dm.Locator(".cm-activeLine")).Not.ToContainTextAsync("Second marker");
        Assert.Empty(errors);
    }
}
