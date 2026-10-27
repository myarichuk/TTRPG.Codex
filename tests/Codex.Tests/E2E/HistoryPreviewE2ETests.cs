using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>The History tab's Preview shows the text as the reader will see it, with the Markdown a model writes rendered.</summary>
[Collection("RavenDb")]
public class HistoryPreviewE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public HistoryPreviewE2ETests(AppFixture app) => _app = app;

    [Fact]
    public async Task HistoryTab_Preview_RendersHeadingsQuotesListsTablesAndLinks()
    {
        const string password = "Correct-Horse-1";
        var dmId = "hp-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");

        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await context.NewPageAsync();
        var problems = new List<string>();
        dm.PageError += (_, e) => problems.Add("pageerror: " + e);
        dm.Console += (_, m) => { if (m.Type == "error") problems.Add("console: " + m.Text); };

        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Preview campaign");
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "cast");
        await dm.GetByTestId("create-character").ClickAsync();
        await dm.GetByTestId("create-character-name").FillAsync("Mira Greystone");
        await dm.GetByTestId("create-character-submit").ClickAsync();
        await dm.WaitForURLAsync(url => url.Contains("/actors/"));
        await E2EHelpers.WaitForInteractiveAsync(dm);

        await dm.GetByTestId("tab-history").ClickAsync();
        await dm.GetByTestId("sheet-biography").FillAsync(
            "# Born in the Wood\n\n**Mira** grew up among the *refugees*.\n\n## Wants\n\n- a quiet life\n\n> \"Never trust a ledger.\"\n\n" +
            "| Ally | Why |\n|---|---|\n| Bertram | owes her |\n\n1. first\n2. second\n\nSee [the guild](https://example.com).");
        await dm.GetByTestId("sheet-biography-preview").ClickAsync();

        var pane = dm.GetByTestId("sheet-biography-preview-pane");
        await Assertions.Expect(pane.Locator("h3")).ToHaveTextAsync("Born in the Wood");
        await Assertions.Expect(pane.Locator("strong")).ToHaveTextAsync("Mira");
        await Assertions.Expect(pane.Locator("blockquote")).ToContainTextAsync("Never trust a ledger.");
        await Assertions.Expect(pane.Locator("ol > li")).ToHaveCountAsync(2);
        await Assertions.Expect(pane.Locator("table td").First).ToHaveTextAsync("Bertram");
        await Assertions.Expect(pane.GetByRole(AriaRole.Link, new() { Name = "the guild" })).ToHaveAttributeAsync("href", "https://example.com");

        await dm.GetByTestId("sheet-biography-write").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("sheet-biography")).ToBeVisibleAsync();
        Assert.Empty(problems);
    }
}
